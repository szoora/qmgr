using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Identity;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services.Mobile;

/// <summary>
/// Issues, redeems-with-rotation, revokes and lists the mobile app's device sessions.
///
/// <para>The subtle work of "keep me signed in", adapted from the ERP's
/// <c>RefreshTokenService</c> (checked in at <c>docs/onboarding-kit/reference/</c> in the mobile
/// repo). The ERP stores these in ASP.NET Identity's <c>AspNetUserTokens</c>; Q-Mgr is not on
/// Identity, so they live in <see cref="UserDeviceSession"/> — see that class for why a table is
/// right here against the standing enhance-before-add rule.</para>
///
/// <para><b>The token shape is <c>{userId}.{deviceId}.{secret}</c></b> so validation is one keyed
/// lookup. Split with a count of 3, so a secret containing a '.' — it cannot, but do not depend on
/// that — never shifts the field boundaries.</para>
/// </summary>
public interface IDeviceSessionService
{
    /// <summary>
    /// Mint a token for a device and persist only its hash. The returned string cannot be
    /// recovered afterwards. Re-signing in on the same device REPLACES that device's token rather
    /// than accumulating rows.
    /// </summary>
    Task<string> IssueAsync(User user, string? deviceId, string? deviceName, string? platform,
                            string? appVersion, long appVersionCode, CancellationToken ct = default);

    /// <summary>
    /// Validate and consume a token, rotating it. On success the presented token is dead.
    /// </summary>
    Task<DeviceRedeemResult> RedeemAsync(string? refreshToken, CancellationToken ct = default);

    /// <summary>Revoke one device. Safe for a device that has no session.</summary>
    Task<bool> RevokeAsync(Guid userId, string? deviceId, string reason, CancellationToken ct = default);

    /// <summary>Revoke every device — "sign out everywhere", or a lost phone.</summary>
    Task<int> RevokeAllAsync(Guid userId, string reason, CancellationToken ct = default);

    /// <summary>Devices holding a live session, newest use first.</summary>
    Task<IReadOnlyList<DeviceSessionDto>> ListAsync(Guid userId, string? currentDeviceId, CancellationToken ct = default);

    /// <summary>
    /// Record what the app told us about itself, and take its push token. Creates nothing: a
    /// check-in from a device with no live session is not an authorisation to make one.
    /// </summary>
    Task<UserDeviceSession?> CheckInAsync(Guid userId, DeviceCheckInRequest request, CancellationToken ct = default);

    /// <summary>
    /// Devices of these users that could actually display a notification right now. Used by the
    /// push sender, which must not invent a delivery for a handset that cannot show one.
    /// </summary>
    Task<IReadOnlyList<UserDeviceSession>> PushTargetsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// FCM said this token is gone. Clear it and keep the session — the session may be perfectly
    /// live, and signing somebody out because their notifications broke is the wrong trade.
    /// </summary>
    Task RetirePushTokenAsync(Guid sessionId, CancellationToken ct = default);
}

public sealed record DeviceRedeemResult
{
    public bool Succeeded { get; init; }

    /// <summary>The sentence the app shows. Every failure is terminal — the app stops retrying.</summary>
    public string? Error { get; init; }

    public User? User { get; init; }
    public string? RefreshToken { get; init; }
    public string? DeviceId { get; init; }

    public static DeviceRedeemResult Fail(string error) => new() { Succeeded = false, Error = error };
}

public sealed class DeviceSessionService : IDeviceSessionService
{
    /// <summary>
    /// How long a device may go unused before it has to sign in again. Slid forward on every
    /// redeem, so a handset in daily use never expires and one left in a drawer does.
    /// </summary>
    private const int SessionDays = 30;

    private readonly QMgrDbContext _db;
    private readonly ILogger<DeviceSessionService> _log;

    public DeviceSessionService(QMgrDbContext db, ILogger<DeviceSessionService> log)
    {
        _db = db;
        _log = log;
    }

    // ── Issue ────────────────────────────────────────────────────────────────

    public async Task<string> IssueAsync(User user, string? deviceId, string? deviceName, string? platform,
                                         string? appVersion, long appVersionCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var device = NormaliseDeviceId(deviceId);
        var secret = NewSecret();
        var now = DateTime.UtcNow;

        var row = await _db.UserDeviceSessions
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.DeviceId == device, ct);

        if (row == null)
        {
            row = new UserDeviceSession
            {
                UserId = user.Id,
                OrganizationId = user.OrganizationId,
                DeviceId = device,
                IssuedAt = now
            };
            _db.UserDeviceSessions.Add(row);
        }
        else
        {
            // Re-signing in on a known device. The row is reused so the push token and the
            // device's history survive, and the revocation is cleared because this IS a new
            // sign-in with a password — not a redeem of the old token.
            row.IssuedAt = now;
            row.RevokedAt = null;
            row.RevokedReason = null;
            row.PushFailedAt = null;
        }

        row.OrganizationId = user.OrganizationId;
        row.TokenHash = Hash(secret);
        row.CredentialStamp = user.CredentialStamp;
        row.LastUsedAt = now;
        row.ExpiresAt = now.AddDays(SessionDays);
        if (!string.IsNullOrWhiteSpace(deviceName)) row.DeviceName = Clip(deviceName, 120);
        if (!string.IsNullOrWhiteSpace(platform)) row.Platform = Clip(platform.ToLowerInvariant(), 20);
        if (!string.IsNullOrWhiteSpace(appVersion)) row.AppVersion = Clip(appVersion, 40);
        if (appVersionCode > 0) row.AppVersionCode = appVersionCode;

        await _db.SaveChangesAsync(ct);

        return $"{user.Id}.{device}.{secret}";
    }

    // ── Redeem ───────────────────────────────────────────────────────────────

    public async Task<DeviceRedeemResult> RedeemAsync(string? refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return DeviceRedeemResult.Fail("No refresh token was supplied.");

        var parts = refreshToken.Split('.', 3);
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
            return DeviceRedeemResult.Fail("That session is no longer valid. Please sign in again.");

        if (!Guid.TryParse(parts[0], out var userId))
            return DeviceRedeemResult.Fail("That session is no longer valid. Please sign in again.");

        var device = parts[1];
        var secret = parts[2];

        // ── UNDER A LOCK, AND THIS IS THE WHOLE POINT ────────────────────────────────────
        //
        // Rotation is only worth having if a REPLAY is detectable, and without this it is not.
        // Each request gets its own scoped DbContext, so five concurrent redemptions of one token
        // each read the same hash, each find it matches, each rotate, and each report success —
        // last write wins and nothing errors. A stolen token used alongside the legitimate one
        // would then work indefinitely, which is exactly the attack rotation exists to stop.
        //
        // Caught by e2e 25.8, which fires five at once: it read "5 of 5 succeeded". The advisory
        // lock is this codebase's own idiom for "exactly once" (the recognition budget, the
        // register double-submit, the notice fan-out) and it is keyed on the SESSION, so two
        // different handsets never wait on each other.
        //
        // Through the execution strategy, because the context uses NpgsqlRetryingExecutionStrategy
        // and BeginTransactionAsync outside one throws — found live when the Subjects page's first
        // read returned 400.
        DeviceRedeemResult? outcome = null;

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var lockKey = $"device-session:{userId}:{device}";
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);

            outcome = await RedeemUnderLockAsync(userId, device, secret, ct);
            await tx.CommitAsync(ct);
        });

        return outcome ?? DeviceRedeemResult.Fail("That session is no longer valid. Please sign in again.");
    }

    /// <summary>
    /// The redemption itself. Separated only so <see cref="RedeemAsync"/> can hold the lock around
    /// it — every read here happens INSIDE the transaction, which is what makes the hash check and
    /// the rotation one indivisible step.
    /// </summary>
    private async Task<DeviceRedeemResult> RedeemUnderLockAsync(Guid userId, string device, string secret,
                                                                CancellationToken ct)
    {
        // IgnoreQueryFilters: a refresh arrives with no tenant context and no principal, so the
        // organisation filter would hide the very row being redeemed.
        var row = await _db.UserDeviceSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.DeviceId == device, ct);

        if (row == null)
            return DeviceRedeemResult.Fail("That session has been signed out. Please sign in again.");

        if (row.RevokedAt != null)
            return DeviceRedeemResult.Fail("That session has been signed out. Please sign in again.");

        // Fixed-time compare. A length-independent equality check here leaks the hash a byte at
        // a time.
        if (!FixedTimeEquals(row.TokenHash, Hash(secret)))
        {
            // A wrong secret against a real (user, device) is either a stale copy left behind by
            // rotation or a REPLAY. Either way the safe answer is to kill this device's session:
            // if it is a replay the thief is stopped, and if it is a stale copy the legitimate
            // device signs in again. This is the half that makes rotation worth having — without
            // it a stolen token is merely single-use, rather than detectable.
            row.RevokedAt = DateTime.UtcNow;
            row.RevokedReason = "A token was presented twice — the session was revoked.";
            await _db.SaveChangesAsync(ct);

            _log.LogWarning(
                "Device session {DeviceId} for user {UserId} was revoked: refresh token mismatch (replay or stale copy)",
                device, userId);

            return DeviceRedeemResult.Fail("That session is no longer valid. Please sign in again.");
        }

        if (row.ExpiresAt <= DateTime.UtcNow)
        {
            row.RevokedAt = DateTime.UtcNow;
            row.RevokedReason = "Expired.";
            await _db.SaveChangesAsync(ct);
            return DeviceRedeemResult.Fail("That session has expired. Please sign in again.");
        }

        var user = await _db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Role)
                .ThenInclude(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user == null || !user.IsActive)
        {
            row.RevokedAt = DateTime.UtcNow;
            row.RevokedReason = "The account is no longer active.";
            await _db.SaveChangesAsync(ct);
            return DeviceRedeemResult.Fail("That session is no longer valid. Please sign in again.");
        }

        if (!CredentialStamps.Matches(row.CredentialStamp, user.CredentialStamp))
        {
            row.RevokedAt = DateTime.UtcNow;
            row.RevokedReason = "The account's credentials changed.";
            await _db.SaveChangesAsync(ct);
            return DeviceRedeemResult.Fail("Your password changed. Please sign in again.");
        }

        // A temporary password is not something a device may refresh past: the whole point is that
        // the person must set a real one, and a password-change-only token cannot reach anything.
        if (user.MustChangePassword)
        {
            row.RevokedAt = DateTime.UtcNow;
            row.RevokedReason = "A temporary password must be changed before signing in again.";
            await _db.SaveChangesAsync(ct);
            return DeviceRedeemResult.Fail("Please sign in to set a new password.");
        }

        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
            return DeviceRedeemResult.Fail("This account is locked. Contact your administrator.");

        // Rotate. The presented token dies here.
        var newSecret = NewSecret();
        var now = DateTime.UtcNow;
        row.TokenHash = Hash(newSecret);
        row.LastUsedAt = now;
        row.ExpiresAt = now.AddDays(SessionDays);       // sliding window
        await _db.SaveChangesAsync(ct);

        return new DeviceRedeemResult
        {
            Succeeded = true,
            User = user,
            DeviceId = device,
            RefreshToken = $"{user.Id}.{device}.{newSecret}"
        };
    }

    // ── Revoke ───────────────────────────────────────────────────────────────

    public async Task<bool> RevokeAsync(Guid userId, string? deviceId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var device = NormaliseDeviceId(deviceId);

        var row = await _db.UserDeviceSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.DeviceId == device && s.RevokedAt == null, ct);

        if (row == null) return false;

        row.RevokedAt = DateTime.UtcNow;
        row.RevokedReason = reason;
        // The push token goes with the session. A revoked device must stop receiving this
        // person's notifications immediately — that is most of the point of revoking it.
        row.PushToken = null;
        row.PushPermitted = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> RevokeAllAsync(Guid userId, string reason, CancellationToken ct = default)
    {
        var rows = await _db.UserDeviceSessions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ToListAsync(ct);

        if (rows.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.RevokedAt = now;
            row.RevokedReason = reason;
            row.PushToken = null;
            row.PushPermitted = false;
        }

        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    // ── List ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeviceSessionDto>> ListAsync(Guid userId, string? currentDeviceId,
                                                                 CancellationToken ct = default)
    {
        var current = string.IsNullOrWhiteSpace(currentDeviceId) ? null : NormaliseDeviceId(currentDeviceId);
        var now = DateTime.UtcNow;

        // An expired row is not a live session, and it is LEFT IN PLACE rather than deleted here:
        // listing is a read, and a GET that mutates is a trap for the next person.
        var rows = await _db.UserDeviceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastUsedAt)
            .ToListAsync(ct);

        return rows.Select(s => new DeviceSessionDto
        {
            DeviceId = s.DeviceId,
            DeviceName = string.IsNullOrWhiteSpace(s.DeviceName) ? "Unknown device" : s.DeviceName!,
            Platform = s.Platform,
            IssuedUtc = s.IssuedAt,
            LastUsedUtc = s.LastUsedAt,
            ExpiresUtc = s.ExpiresAt,
            IsCurrent = current != null && s.DeviceId == current,
            PushEnabled = s.CanReceivePush
        }).ToList();
    }

    // ── Check-in ─────────────────────────────────────────────────────────────

    public async Task<UserDeviceSession?> CheckInAsync(Guid userId, DeviceCheckInRequest request,
                                                       CancellationToken ct = default)
    {
        var device = NormaliseDeviceId(request.DeviceId);

        var row = await _db.UserDeviceSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.DeviceId == device, ct);

        // Creates nothing. A check-in from a device with no session is not an authorisation to
        // make one — the session is created by signing in, and only there.
        if (row == null || row.RevokedAt != null) return null;

        var now = DateTime.UtcNow;
        row.LastCheckInAt = now;
        if (!string.IsNullOrWhiteSpace(request.DeviceName)) row.DeviceName = Clip(request.DeviceName, 120);
        if (!string.IsNullOrWhiteSpace(request.Platform)) row.Platform = Clip(request.Platform.ToLowerInvariant(), 20);
        if (!string.IsNullOrWhiteSpace(request.AppVersion)) row.AppVersion = Clip(request.AppVersion, 40);
        if (request.AppVersionCode > 0) row.AppVersionCode = request.AppVersionCode;

        // A token that has changed is a NEW token, so whatever FCM said about the old one no
        // longer applies. Clearing PushFailedAt here is what lets a handset recover from a wipe.
        if (!string.IsNullOrWhiteSpace(request.PushToken)
            && !string.Equals(row.PushToken, request.PushToken, StringComparison.Ordinal))
        {
            row.PushToken = Clip(request.PushToken, 4000);
            row.PushTokenUpdatedAt = now;
            row.PushFailedAt = null;
        }

        row.PushPermitted = request.PushPermitted;

        await _db.SaveChangesAsync(ct);
        return row;
    }

    // ── Push targets ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDeviceSession>> PushTargetsAsync(IReadOnlyCollection<Guid> userIds,
                                                                         CancellationToken ct = default)
    {
        // Fail closed on an empty list rather than letting it collapse into a no-op WHERE — the
        // same shape as IStudentScopeService, and here it would mean notifying every handset in
        // the estate.
        if (userIds.Count == 0) return Array.Empty<UserDeviceSession>();

        var now = DateTime.UtcNow;
        return await _db.UserDeviceSessions
            .IgnoreQueryFilters()
            .Where(s => userIds.Contains(s.UserId)
                        && s.RevokedAt == null
                        && s.ExpiresAt > now
                        && s.PushPermitted
                        && s.PushFailedAt == null
                        && s.PushToken != null && s.PushToken != "")
            .ToListAsync(ct);
    }

    public async Task RetirePushTokenAsync(Guid sessionId, CancellationToken ct = default)
    {
        var row = await _db.UserDeviceSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (row == null) return;

        row.PushToken = null;
        row.PushFailedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NewSecret()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string secret)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (a == null || b == null) return false;
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    /// <summary>
    /// A device id travels inside the dotted token, so it is constrained to an alphabet that
    /// cannot shift a field boundary. A client that sends nothing gets a generated id rather than
    /// an error — the alternative is a device that cannot stay signed in for a reason its user
    /// cannot see.
    /// </summary>
    public static string NormaliseDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return Guid.NewGuid().ToString("n");

        var cleaned = new string(deviceId
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
            .Take(64)
            .ToArray());

        return cleaned.Length == 0 ? Guid.NewGuid().ToString("n") : cleaned;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Clip(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);
}
