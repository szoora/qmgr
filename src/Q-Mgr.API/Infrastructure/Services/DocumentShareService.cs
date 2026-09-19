using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Content;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>Who is at the other end of a share request, as much as we can honestly say.</summary>
public sealed record ShareViewerContext(string? IpAddress, string? UserAgent, string? Email = null, Guid? SessionId = null);

/// <summary>A viewing session or content grant, decoded from its token.</summary>
public sealed record ShareGrant(Guid ShareId, Guid SessionId, string? Email);

/// <summary>
/// The single home for issuing, resolving, gating and revoking share links. Controllers call
/// this; nothing else touches <see cref="DocumentShare"/> rows.
///
/// Three tokens, three purposes, deliberately distinct:
///  - the LINK is an opaque random slug whose hash is stored — revocable, because it is a row;
///  - the EMAIL CHALLENGE is a signed, ten-minute envelope carrying the hash of the code that was
///    emailed, so the server keeps no per-attempt state and a code cannot be replayed against a
///    different address or share;
///  - the SESSION and CONTENT tokens are <see cref="ITimeLimitedDataProtector"/> payloads minted
///    only after every gate has been cleared — the content one lasts sixty seconds, long enough to
///    start the fetch and no longer, so the durable path is never exposed.
/// </summary>
public interface IDocumentShareService
{
    Task<(DocumentShare Share, string Slug)> IssueAsync(MediaContent document, CreateDocumentShareRequest request, Guid actorUserId, DocumentSharingPolicyDto policy, CancellationToken ct = default);
    Task ApplyUpdateAsync(DocumentShare share, UpdateDocumentShareRequest request, Guid actorUserId, DocumentSharingPolicyDto policy, CancellationToken ct = default);
    Task RevokeAsync(DocumentShare share, Guid actorUserId, string? reason, CancellationToken ct = default);

    /// <summary>The share behind a slug, with its document, or null. Filter-free: a viewer has no tenant.</summary>
    Task<DocumentShare?> FindBySlugAsync(string slug, CancellationToken ct = default);

    DocumentShareState EvaluateState(DocumentShare share, DateTime nowUtc);

    Task RecordAsync(DocumentShare share, DocumentShareEventType type, bool success, string? detail, ShareViewerContext viewer, int? page = null, int? dwellSeconds = null, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>True on a match. A miss counts towards the lockout; a hit resets it.</summary>
    Task<bool> TryPasscodeAsync(DocumentShare share, string passcode, ShareViewerContext viewer, CancellationToken ct = default);

    bool IsEmailAllowed(DocumentShare share, string email);
    IReadOnlyList<string> AllowedEmails(DocumentShare share);

    /// <summary>Emails a six-digit code and returns the challenge the viewer must send back with it.</summary>
    Task<string?> SendEmailChallengeAsync(DocumentShare share, string email, ShareViewerContext viewer, CancellationToken ct = default);
    /// <summary>The verified address on success, null on a wrong or expired code.</summary>
    Task<string?> VerifyEmailChallengeAsync(DocumentShare share, string challenge, string code, ShareViewerContext viewer, CancellationToken ct = default);

    /// <summary>Counts the open, stamps first/last, writes the event, notifies the creator on the first one. Returns the grant.</summary>
    Task<ShareGrant> OpenAsync(DocumentShare share, ShareViewerContext viewer, string? verifiedEmail, CancellationToken ct = default);

    string IssueSessionToken(ShareGrant grant);
    string IssueContentToken(ShareGrant grant);
    ShareGrant? ReadSessionToken(string? token);
    ShareGrant? ReadContentToken(string? token);
    int ContentTokenSeconds { get; }

    string WatermarkText(DocumentShare share, string? email, DateTime nowUtc);

    DocumentSharingPolicyDto ReadPolicy(string? organizationSettingsJson);
    string WritePolicy(string? organizationSettingsJson, DocumentSharingPolicyDto policy);
}

public class DocumentShareService : IDocumentShareService
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(4);
    private static readonly TimeSpan ContentLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    private const string PolicyKey = "DocumentSharing";

    private readonly QMgrDbContext _db;
    private readonly ITimeLimitedDataProtector _sessions;
    private readonly ITimeLimitedDataProtector _content;
    private readonly ITimeLimitedDataProtector _challenges;
    private readonly PasswordHasher<DocumentShare> _hasher = new();
    private readonly INotificationService _notifications;
    private readonly ILogger<DocumentShareService> _logger;

    public DocumentShareService(
        QMgrDbContext db,
        IDataProtectionProvider protection,
        INotificationService notifications,
        ILogger<DocumentShareService> logger)
    {
        _db = db;
        _sessions = protection.CreateProtector("DocumentShare.Session.v1").ToTimeLimitedDataProtector();
        _content = protection.CreateProtector("DocumentShare.Content.v1").ToTimeLimitedDataProtector();
        _challenges = protection.CreateProtector("DocumentShare.EmailChallenge.v1").ToTimeLimitedDataProtector();
        _notifications = notifications;
        _logger = logger;
    }

    public int ContentTokenSeconds => (int)ContentLifetime.TotalSeconds;

    // ---- Slugs -----------------------------------------------------------------------------------

    /// <summary>160 bits of RandomNumberGenerator entropy, base64url: 27 characters, typed off a printed page if need be.</summary>
    private static string NewSlug() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(20));

    public static string HashSlug(string slug)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(slug.Trim()))).ToLowerInvariant();

    // ---- Issue / update / revoke -----------------------------------------------------------------

    public async Task<(DocumentShare Share, string Slug)> IssueAsync(MediaContent document, CreateDocumentShareRequest request, Guid actorUserId, DocumentSharingPolicyDto policy, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        ValidateWindow(request.NotBefore, request.ExpiresAt, request.MaxViews, policy, now);

        var allowed = NormalizeEmails(request.AllowedEmails);
        var slug = NewSlug();

        var share = new DocumentShare
        {
            MediaContentId = document.Id,
            SlugHash = HashSlug(slug),
            Label = Trim(request.Label, 200),
            PasscodeHash = string.IsNullOrWhiteSpace(request.Passcode) ? null : _hasher.HashPassword(null!, request.Passcode.Trim()),
            NotBefore = request.NotBefore,
            ExpiresAt = request.ExpiresAt,
            MaxViews = request.MaxViews,
            AllowDownload = request.AllowDownload,
            RequireEmail = request.RequireEmail || allowed.Count > 0,
            AllowedEmailsJson = allowed.Count == 0 ? null : JsonSerializer.Serialize(allowed),
            Watermark = request.Watermark,
            WatermarkText = Trim(request.WatermarkText, 200),
            NotifyOnFirstOpen = request.NotifyOnFirstOpen,
            Reason = Trim(request.Reason, 500),
            CreatedBy = actorUserId
        };
        ValidateWatermark(share);

        _db.DocumentShares.Add(share);
        _db.DocumentShareEvents.Add(new DocumentShareEvent
        {
            ShareId = share.Id,
            Type = DocumentShareEventType.Issued,
            ActorUserId = actorUserId,
            // The rules, and the why when one was given. ISO 23081-1 (quoted in MoReq2010) expects an
            // event history to say why something happened, not only who and when.
            Detail = Trim(string.IsNullOrWhiteSpace(share.Reason)
                ? DescribeRules(share)
                : DescribeRules(share) + " — " + share.Reason, 500)
        });
        await _db.SaveChangesAsync(ct);

        return (share, slug);
    }

    public async Task ApplyUpdateAsync(DocumentShare share, UpdateDocumentShareRequest request, Guid actorUserId, DocumentSharingPolicyDto policy, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        ValidateWindow(request.NotBefore, request.ExpiresAt, request.MaxViews, policy, now);

        var allowed = NormalizeEmails(request.AllowedEmails);

        share.Label = Trim(request.Label, 200);
        share.NotBefore = request.NotBefore;
        share.ExpiresAt = request.ExpiresAt;
        share.MaxViews = request.MaxViews;
        share.AllowDownload = request.AllowDownload;
        share.RequireEmail = request.RequireEmail || allowed.Count > 0;
        share.AllowedEmailsJson = allowed.Count == 0 ? null : JsonSerializer.Serialize(allowed);
        share.Watermark = request.Watermark;
        share.WatermarkText = Trim(request.WatermarkText, 200);
        ValidateWatermark(share);
        share.NotifyOnFirstOpen = request.NotifyOnFirstOpen;
        if (request.ClearPasscode) share.PasscodeHash = null;
        else if (!string.IsNullOrWhiteSpace(request.NewPasscode)) share.PasscodeHash = _hasher.HashPassword(null!, request.NewPasscode.Trim());
        share.UpdatedBy = actorUserId;

        _db.DocumentShareEvents.Add(new DocumentShareEvent
        {
            ShareId = share.Id,
            Type = DocumentShareEventType.Updated,
            ActorUserId = actorUserId,
            Detail = DescribeRules(share)
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(DocumentShare share, Guid actorUserId, string? reason, CancellationToken ct = default)
    {
        if (share.RevokedAt != null) return;
        share.RevokedAt = DateTime.UtcNow;
        share.RevokedByUserId = actorUserId;
        share.RevokeReason = Trim(reason, 500);
        _db.DocumentShareEvents.Add(new DocumentShareEvent
        {
            ShareId = share.Id,
            Type = DocumentShareEventType.Revoked,
            ActorUserId = actorUserId,
            Detail = share.RevokeReason
        });
        await _db.SaveChangesAsync(ct);
    }

    private static void ValidateWindow(DateTime? notBefore, DateTime? expiresAt, int? maxViews, DocumentSharingPolicyDto policy, DateTime now)
    {
        if (expiresAt.HasValue && expiresAt.Value <= now)
            throw new ArgumentException("The expiry must be in the future.");
        if (notBefore.HasValue && expiresAt.HasValue && notBefore.Value >= expiresAt.Value)
            throw new ArgumentException("The link would expire before it opens.");
        if (policy.MaxLinkDays > 0)
        {
            var cap = now.AddDays(policy.MaxLinkDays);
            if (!expiresAt.HasValue)
                throw new ArgumentException($"Your organization requires every link to expire within {policy.MaxLinkDays} days. Set an expiry date.");
            if (expiresAt.Value > cap.AddMinutes(1))
                throw new ArgumentException($"Your organization caps link lifetime at {policy.MaxLinkDays} days (until {cap.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}).");
        }
        if (maxViews.HasValue && maxViews.Value < 1)
            throw new ArgumentException("The view limit must be at least 1.");
    }

    // ---- Resolution & state ----------------------------------------------------------------------

    public Task<DocumentShare?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 64) return Task.FromResult<DocumentShare?>(null);
        var hash = HashSlug(slug);
        return _db.DocumentShares
            .IgnoreQueryFilters()
            .Include(s => s.MediaContent)
            .FirstOrDefaultAsync(s => s.SlugHash == hash, ct);
    }

    public DocumentShareState EvaluateState(DocumentShare share, DateTime nowUtc) => EvaluateStateOf(share, nowUtc);

    /// <summary>
    /// The same rule, reachable without an instance. <c>ContentController.ToDtosAsync</c> is static
    /// and batch-shaped, and it needs this to count a document's live links; before 2026-09-18 it
    /// wrote its own predicate in SQL instead and the two had already drifted apart. One
    /// implementation, two entry points — never a second predicate.
    /// </summary>
    public static DocumentShareState EvaluateStateOf(DocumentShare share, DateTime nowUtc)
    {
        // Fail closed first: a share whose document is gone, deactivated, or no longer shareable
        // refuses. It never falls back to serving the file.
        var doc = share.MediaContent;
        if (doc == null || !doc.IsActive || !doc.IsShareable || string.IsNullOrEmpty(doc.FilePath))
            return DocumentShareState.Unavailable;
        if (share.RevokedAt != null || !share.IsActive) return DocumentShareState.Revoked;
        if (share.LockedUntil.HasValue && share.LockedUntil.Value > nowUtc) return DocumentShareState.Locked;
        if (share.NotBefore.HasValue && share.NotBefore.Value > nowUtc) return DocumentShareState.NotYetOpen;
        if (share.ExpiresAt.HasValue && share.ExpiresAt.Value <= nowUtc) return DocumentShareState.Expired;
        if (share.MaxViews.HasValue && share.ViewCount >= share.MaxViews.Value) return DocumentShareState.ViewLimitReached;
        return DocumentShareState.Active;
    }

    // ---- Events ----------------------------------------------------------------------------------

    public async Task RecordAsync(DocumentShare share, DocumentShareEventType type, bool success, string? detail, ShareViewerContext viewer, int? page = null, int? dwellSeconds = null, Guid? actorUserId = null, CancellationToken ct = default)
    {
        _db.DocumentShareEvents.Add(new DocumentShareEvent
        {
            ShareId = share.Id,
            Type = type,
            Success = success,
            Detail = Trim(detail, 500),
            Email = Trim(viewer.Email, 320),
            IpAddress = TruncateIp(viewer.IpAddress),
            UserAgent = CoarseUserAgent(viewer.UserAgent),
            Page = page,
            DwellSeconds = dwellSeconds,
            SessionId = viewer.SessionId,
            ActorUserId = actorUserId
        });
        await _db.SaveChangesAsync(ct);
    }

    // ---- Gates -----------------------------------------------------------------------------------

    public async Task<bool> TryPasscodeAsync(DocumentShare share, string passcode, ShareViewerContext viewer, CancellationToken ct = default)
    {
        if (share.PasscodeHash == null) return true;

        var result = _hasher.VerifyHashedPassword(null!, share.PasscodeHash, (passcode ?? string.Empty).Trim());
        if (result != PasswordVerificationResult.Failed)
        {
            share.FailedAttempts = 0;
            share.LockedUntil = null;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await RegisterFailureAsync(share, viewer, DocumentShareEventType.PasscodeFailed, "wrong passcode", ct);
        return false;
    }

    private async Task RegisterFailureAsync(DocumentShare share, ShareViewerContext viewer, DocumentShareEventType type, string detail, CancellationToken ct)
    {
        share.FailedAttempts++;
        var locked = share.FailedAttempts >= MaxFailedAttempts;
        if (locked)
        {
            share.LockedUntil = DateTime.UtcNow.Add(LockoutFor);
            share.FailedAttempts = 0;
        }
        await RecordAsync(share, type, success: false, detail, viewer, ct: ct);
        if (locked)
            await RecordAsync(share, DocumentShareEventType.Locked, success: false, $"locked for {LockoutFor.TotalMinutes:0} minutes after {MaxFailedAttempts} failed attempts", viewer, ct: ct);
    }

    public IReadOnlyList<string> AllowedEmails(DocumentShare share)
    {
        if (string.IsNullOrWhiteSpace(share.AllowedEmailsJson)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(share.AllowedEmailsJson) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    public bool IsEmailAllowed(DocumentShare share, string email)
    {
        var allowed = AllowedEmails(share);
        if (allowed.Count == 0) return true;
        var normalized = NormalizeEmail(email);
        return allowed.Any(a => string.Equals(a, normalized, StringComparison.Ordinal));
    }

    public async Task<string?> SendEmailChallengeAsync(DocumentShare share, string email, ShareViewerContext viewer, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (!IsValidEmail(normalized)) return null;

        // Six digits, from the CSPRNG. Brute force is bounded by the per-share lockout (five
        // misses, fifteen minutes) and the ten-minute life of the challenge, not by secrecy of the
        // format.
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var codeHash = HashCode(share, normalized, code);
        var challenge = _challenges.Protect($"{share.Id:N}|{normalized}|{codeHash}", DateTimeOffset.UtcNow.Add(ChallengeLifetime));

        var doc = share.MediaContent!;
        var body = $@"<p>Your verification code to open <strong>{System.Net.WebUtility.HtmlEncode(doc.Name)}</strong> is:</p>
<p style=""font-size:28px;letter-spacing:6px;font-weight:700"">{code}</p>
<p>It expires in {ChallengeLifetime.TotalMinutes:0} minutes. If you did not request this, ignore this message.</p>";

        var sent = await _notifications.SendEmailAsync(doc.OrganizationId, normalized, "Your document verification code", body, isHtml: true, cancellationToken: ct);
        await RecordAsync(share, DocumentShareEventType.EmailCodeSent, sent.IsSent, sent.IsSent ? null : sent.Reason, viewer with { Email = normalized }, ct: ct);
        if (!sent.IsSent)
        {
            _logger.LogWarning("Could not email a share verification code for share {ShareId}: {Reason}", share.Id, sent.Reason);
            return null;
        }
        return challenge;
    }

    public async Task<string?> VerifyEmailChallengeAsync(DocumentShare share, string challenge, string code, ShareViewerContext viewer, CancellationToken ct = default)
    {
        string payload;
        try { payload = _challenges.Unprotect(challenge ?? string.Empty); }
        catch
        {
            await RegisterFailureAsync(share, viewer, DocumentShareEventType.EmailRejected, "expired or invalid challenge", ct);
            return null;
        }

        var parts = payload.Split('|');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var shareId) || shareId != share.Id)
        {
            await RegisterFailureAsync(share, viewer, DocumentShareEventType.EmailRejected, "challenge for another link", ct);
            return null;
        }

        var email = parts[1];
        var expected = parts[2];
        var actual = HashCode(share, email, (code ?? string.Empty).Trim());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual)))
        {
            await RegisterFailureAsync(share, viewer with { Email = email }, DocumentShareEventType.EmailRejected, "wrong code", ct);
            return null;
        }

        share.FailedAttempts = 0;
        await RecordAsync(share, DocumentShareEventType.EmailVerified, true, null, viewer with { Email = email }, ct: ct);
        return email;
    }

    private static string HashCode(DocumentShare share, string email, string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{share.Id:N}:{share.SlugHash}:{email}:{code}"))).ToLowerInvariant();

    // ---- Open ------------------------------------------------------------------------------------

    public async Task<ShareGrant> OpenAsync(DocumentShare share, ShareViewerContext viewer, string? verifiedEmail, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var first = share.FirstOpenedAt == null;
        share.ViewCount++;
        share.FirstOpenedAt ??= now;
        share.LastOpenedAt = now;

        var sessionId = Guid.NewGuid();
        await RecordAsync(share, DocumentShareEventType.Opened, true, null, viewer with { Email = verifiedEmail, SessionId = sessionId }, ct: ct);

        if (first && share.NotifyOnFirstOpen && share.CreatedBy.HasValue)
            await NotifyFirstOpenAsync(share, verifiedEmail, ct);

        return new ShareGrant(share.Id, sessionId, verifiedEmail);
    }

    /// <summary>
    /// A side effect after the open has been recorded; it must never fail the open. Goes through
    /// the existing dispatch path (bell now, email off-thread and retried) and is preference-aware
    /// through its EventKey.
    /// </summary>
    private async Task NotifyFirstOpenAsync(DocumentShare share, string? viewerEmail, CancellationToken ct)
    {
        try
        {
            var doc = share.MediaContent!;
            var creator = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == share.CreatedBy!.Value)
                .Select(u => new { u.Email })
                .FirstOrDefaultAsync(ct);

            var by = string.IsNullOrEmpty(viewerEmail) ? "" : $" by {viewerEmail}";
            var label = string.IsNullOrWhiteSpace(share.Label) ? "" : $" ({share.Label})";
            await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = share.CreatedBy,
                OrganizationId = doc.OrganizationId,
                EventKey = NotificationEventKeys.DocumentShareOpened,
                Title = "Shared document opened",
                Message = $"\"{doc.Name}\"{label} was opened for the first time{by}.",
                Type = NotificationType.Custom,
                IconClass = "bi-share-fill",
                ActionUrl = $"/content/library?tab=documents&document={doc.Id}",
                Channels = NotificationChannel.InApp | NotificationChannel.Email,
                Email = creator?.Email,
                EmailSubject = $"Q-Mgr: \"{doc.Name}\" was opened"
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "First-open notification for share {ShareId} failed; the open itself succeeded", share.Id);
        }
    }

    // ---- Tokens ----------------------------------------------------------------------------------

    public string IssueSessionToken(ShareGrant g) => _sessions.Protect(Pack(g), DateTimeOffset.UtcNow.Add(SessionLifetime));
    public string IssueContentToken(ShareGrant g) => _content.Protect(Pack(g), DateTimeOffset.UtcNow.Add(ContentLifetime));
    public ShareGrant? ReadSessionToken(string? token) => Read(_sessions, token);
    public ShareGrant? ReadContentToken(string? token) => Read(_content, token);

    private static string Pack(ShareGrant g) => $"{g.ShareId:N}|{g.SessionId:N}|{g.Email}";

    private static ShareGrant? Read(ITimeLimitedDataProtector protector, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var parts = protector.Unprotect(token).Split('|');
            if (parts.Length != 3) return null;
            if (!Guid.TryParseExact(parts[0], "N", out var shareId) || !Guid.TryParseExact(parts[1], "N", out var sessionId)) return null;
            return new ShareGrant(shareId, sessionId, string.IsNullOrEmpty(parts[2]) ? null : parts[2]);
        }
        catch { return null; }
    }

    public string WatermarkText(DocumentShare share, string? email, DateTime nowUtc)
    {
        if (share.Watermark == DocumentWatermarkMode.None) return string.Empty;

        var who = string.IsNullOrWhiteSpace(email) ? "Anonymous viewer" : email;
        // Invariant, like every date the app shows (QDateFormat): the server's en-GB culture
        // rendered this as "15 Sept", seen in the browser check.
        var viewer = $"{who} · {nowUtc.ToString("dd MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC";
        var custom = share.WatermarkText?.Trim() ?? string.Empty;

        return share.Watermark switch
        {
            DocumentWatermarkMode.Custom => custom,
            DocumentWatermarkMode.CustomAndViewer => custom.Length == 0 ? viewer : $"{custom} · {viewer}",
            _ => viewer
        };
    }

    /// <summary>A custom mode with no text would draw nothing and claim a watermark; refuse it at the source.</summary>
    private static void ValidateWatermark(DocumentShare share)
    {
        if (share.Watermark is DocumentWatermarkMode.Custom or DocumentWatermarkMode.CustomAndViewer
            && string.IsNullOrWhiteSpace(share.WatermarkText))
            throw new ArgumentException("Enter the watermark text, or choose the viewer-identity watermark.");
    }

    // ---- Policy ----------------------------------------------------------------------------------

    public DocumentSharingPolicyDto ReadPolicy(string? organizationSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(organizationSettingsJson)) return new DocumentSharingPolicyDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson);
            if (root != null && root.TryGetValue(PolicyKey, out var element))
                return JsonSerializer.Deserialize<DocumentSharingPolicyDto>(element.GetRawText()) ?? new DocumentSharingPolicyDto();
        }
        catch (JsonException) { /* malformed settings blob — fall back to defaults */ }
        return new DocumentSharingPolicyDto();
    }

    public string WritePolicy(string? organizationSettingsJson, DocumentSharingPolicyDto policy)
    {
        Dictionary<string, JsonElement> root;
        try
        {
            root = string.IsNullOrWhiteSpace(organizationSettingsJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson) ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException) { root = new Dictionary<string, JsonElement>(); }

        root[PolicyKey] = JsonSerializer.SerializeToElement(policy);
        return JsonSerializer.Serialize(root);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320) return false;
        try { return new System.Net.Mail.MailAddress(email).Address == email; }
        catch { return false; }
    }

    private static List<string> NormalizeEmails(IEnumerable<string>? emails)
        => (emails ?? Array.Empty<string>())
            .Select(NormalizeEmail)
            .Where(IsValidEmail)
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToList();

    private static string DescribeRules(DocumentShare s)
    {
        var parts = new List<string>();
        parts.Add(s.RequiresPasscode ? "passcode" : "no passcode");
        parts.Add(s.RequiresEmailVerification ? "email verified" : "no email check");
        parts.Add(s.AllowDownload ? "download allowed" : "view only");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (s.ExpiresAt.HasValue) parts.Add($"expires {s.ExpiresAt.Value.ToString("dd MMM yyyy", inv)}");
        if (s.NotBefore.HasValue) parts.Add($"opens {s.NotBefore.Value.ToString("dd MMM yyyy", inv)}");
        if (s.MaxViews.HasValue) parts.Add($"max {s.MaxViews} view(s)");
        return string.Join(", ", parts);
    }

    /// <summary>IPv4 to /24, IPv6 to /48: "opened from three countries" stays answerable, "which house" does not.</summary>
    public static string? TruncateIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        if (!System.Net.IPAddress.TryParse(ip.Trim(), out var address)) return null;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0";
        if (bytes.Length == 16) return $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}::";
        return null;
    }

    /// <summary>Browser family and OS only. The full string is a fingerprint; this is a description.</summary>
    public static string? CoarseUserAgent(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        string browser =
            ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge" :
            ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera" :
            ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox" :
            ua.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome" :
            ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari" : "Browser";
        string os =
            ua.Contains("Windows", StringComparison.Ordinal) ? "Windows" :
            ua.Contains("Android", StringComparison.Ordinal) ? "Android" :
            ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal) ? "iOS" :
            ua.Contains("Mac OS", StringComparison.Ordinal) ? "macOS" :
            ua.Contains("Linux", StringComparison.Ordinal) ? "Linux" : "Unknown OS";
        return $"{browser} on {os}";
    }
}
