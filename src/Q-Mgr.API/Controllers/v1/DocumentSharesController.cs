using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Content;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The staff side of secure document sharing: publishing flags on a Library document, issuing and
/// revoking links, the activity log, and the tenant's sharing policy.
///
/// Sharing only knows about the Library (<see cref="MediaContent"/>). Nothing else in the app gets
/// a share button — a report is exported INTO the Library first, and that export runs the report's
/// own permission and row-scope checks. That is why no student scope appears anywhere in here.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[RequireModule(ModuleCodes.EngagementCommunications)]
[Produces("application/json")]
public class DocumentSharesController : ControllerBase
{
    private readonly QMgrDbContext _db;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IDocumentShareService _shares;
    private readonly INotificationService _notifications;
    private readonly IUploadAuthorizer _uploadAuthorizer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentSharesController> _logger;

    public DocumentSharesController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IDocumentShareService shares,
        INotificationService notifications,
        IUploadAuthorizer uploadAuthorizer,
        IConfiguration configuration,
        ILogger<DocumentSharesController> logger)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
        _shares = shares;
        _notifications = notifications;
        _uploadAuthorizer = uploadAuthorizer;
        _configuration = configuration;
        _logger = logger;
    }

    private Guid CurrentUserId()
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    /// <summary>The document, org-scoped by the MediaContent query filter (SuperAdmin sees all). 404 outside the tenant.</summary>
    private Task<MediaContent?> FindDocumentAsync(Guid mediaId)
        => _db.MediaContents.Include(m => m.PlaylistItems).FirstOrDefaultAsync(m => m.Id == mediaId);

    // Permissions aren't JWT claims here (see PermissionAuthorizationHandler); resolved by role
    // lookup, the same shape ContentController and WelfareController use.
    // Role AND posts, through PostPermissionService.EffectiveCodesAsync — the one home for that union.
    private async Task<bool> HasPermissionAsync(string code)
    {
        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole)) return true;
        var userId = CurrentUserId();
        if (userId == Guid.Empty) return false;
        _effectiveCodes ??= await PostPermissionService.EffectiveCodesAsync(_db, userId);
        return _effectiveCodes.Contains(code);
    }

    private HashSet<string>? _effectiveCodes;

    private async Task<DocumentSharingPolicyDto> PolicyForAsync(Guid organizationId)
    {
        var settings = await _db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Settings).FirstOrDefaultAsync();
        return _shares.ReadPolicy(settings);
    }

    // ---- Publishing ------------------------------------------------------------------------------

    /// <summary>
    /// Flags a Library document as shareable (or not) and sets its public summary and provenance.
    /// Turning IsShareable OFF does not delete its links: they evaluate to Unavailable and refuse —
    /// fail closed — and the history stays.
    /// </summary>
    [HttpPut("media/{mediaId:guid}/publishing")]
    [RequirePermission(Permissions.LibraryPublish)]
    [ProducesResponseType(typeof(MediaContentDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePublishing(Guid mediaId, [FromBody] UpdateMediaPublishingRequest request)
    {
        var media = await FindDocumentAsync(mediaId);
        if (media == null) return NotFound();

        if (request.IsShareable && media.ContentType != ContentType.Pdf)
            return BadRequest(new ProblemDetails { Title = "Only PDF documents can be shared", Detail = "Sharing is for documents. Images, video and audio stay on the signage side of the Library.", Status = StatusCodes.Status400BadRequest });

        if (request.IsShareable && string.IsNullOrEmpty(media.FilePath))
            return BadRequest(new ProblemDetails { Title = "Only uploaded documents can be shared", Detail = "A document linked by URL is hosted elsewhere; Q-Mgr cannot gate it.", Status = StatusCodes.Status400BadRequest });

        // The other half of the §1 invariant, mirroring ContentController.AddPlaylistItem: a document
        // already on a playlist cannot be made shareable, because a shared document's bytes are gated
        // and the display fetches them anonymously. One wording, held in MediaServing, so the two
        // refusals cannot drift.
        if (request.IsShareable && await _db.PlaylistItems.AnyAsync(pi => pi.MediaContentId == media.Id))
            return BadRequest(new ProblemDetails
            {
                Title = MediaServing.CombinationRefusedTitle,
                Detail = MediaServing.CombinationRefusedDetail,
                Status = StatusCodes.Status400BadRequest
            });

        var wasShareable = media.IsShareable;
        media.IsShareable = request.IsShareable;
        media.Summary = string.IsNullOrWhiteSpace(request.Summary) ? null : request.Summary.Trim();
        media.PublishedFrom = string.IsNullOrWhiteSpace(request.PublishedFrom) ? media.PublishedFrom : request.PublishedFrom.Trim();
        if (request.IsShareable && !wasShareable)
        {
            // The publish audit: who made this document leave-able, and when.
            media.PublishedAt ??= DateTime.UtcNow;
            media.PublishedByUserId ??= CurrentUserId();
        }
        media.UpdatedBy = CurrentUserId();
        await _db.SaveChangesAsync();

        // The serving rule for its file may have just changed.
        var fileName = UploadAccessService.FileNameOf(media.FileUrl);
        if (fileName != null) _uploadAuthorizer.Invalidate(fileName);

        _logger.LogInformation("Document {MediaId} publishing changed by {UserId}: shareable={Shareable}", media.Id, CurrentUserId(), media.IsShareable);
        return Ok(await ContentController.ToDtoAsync(_db, media));
    }

    /// <summary>
    /// Sets how sensitive a document is, which narrows what its links may do (plan D7).
    ///
    /// <para><b>Raising is ordinary publishing work</b> — <c>library.publish</c>, no reason needed.
    /// <b>Lowering is the dangerous direction</b>, because it is how a restriction would be escaped:
    /// it needs <c>documents.share.manage</c> and a written reason, and both are kept on the row.
    /// Google locks a DLP-applied label against exactly this; Microsoft requires a recorded
    /// justification. Without the lock the label is decoration.</para>
    /// </summary>
    [HttpPut("media/{mediaId:guid}/classification")]
    [RequirePermissionAny(Permissions.LibraryPublish, Permissions.DocumentsShareManage)]
    [ProducesResponseType(typeof(MediaContentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateClassification(Guid mediaId, [FromBody] UpdateMediaClassificationRequest request)
    {
        var media = await FindDocumentAsync(mediaId);
        if (media == null) return NotFound();

        if (!Enum.IsDefined(request.Classification))
            return BadRequest(new ProblemDetails { Title = "Unknown classification", Status = StatusCodes.Status400BadRequest });

        var lowering = request.Classification < media.Classification;
        if (lowering)
        {
            if (!await HasPermissionAsync(Permissions.DocumentsShareManage))
                return Forbid();
            if ((request.Reason ?? string.Empty).Trim().Length < 10)
                return BadRequest(new ProblemDetails
                {
                    Title = "Lowering a classification needs a reason",
                    Detail = "Say why this document is no longer at the higher classification. It is kept with the document.",
                    Status = StatusCodes.Status400BadRequest
                });
        }
        else if (!await HasPermissionAsync(Permissions.LibraryPublish) && !await HasPermissionAsync(Permissions.DocumentsShareManage))
        {
            return Forbid();
        }

        if (request.Classification == media.Classification)
            return Ok(await ContentController.ToDtoAsync(_db, media));

        var from = media.Classification;
        media.Classification = request.Classification;
        media.ClassificationSetAt = DateTime.UtcNow;
        media.ClassificationSetByUserId = CurrentUserId();
        media.ClassificationReason = lowering ? request.Reason!.Trim() : null;
        media.UpdatedBy = CurrentUserId();
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Document {MediaId} classification changed by {UserId}: {From} -> {To}{Reason}",
            media.Id, CurrentUserId(), from, media.Classification,
            lowering ? " (lowered, reason recorded)" : string.Empty);

        return Ok(await ContentController.ToDtoAsync(_db, media));
    }

    // ---- Links -----------------------------------------------------------------------------------

    [HttpGet("media/{mediaId:guid}/shares")]
    [RequirePermission(Permissions.DocumentsShareCreate)]
    [ProducesResponseType(typeof(List<DocumentShareDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListShares(Guid mediaId)
    {
        var media = await FindDocumentAsync(mediaId);
        if (media == null) return NotFound();

        var shares = await _db.DocumentShares
            .Include(s => s.MediaContent)
            .Where(s => s.MediaContentId == mediaId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        return Ok(await MapSharesAsync(shares));
    }

    [HttpPost("media/{mediaId:guid}/shares")]
    [RequirePermission(Permissions.DocumentsShareCreate)]
    [ProducesResponseType(typeof(DocumentShareIssuedDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateShare(Guid mediaId, [FromBody] CreateDocumentShareRequest request)
    {
        var media = await FindDocumentAsync(mediaId);
        if (media == null) return NotFound();

        if (!media.IsShareable)
            return BadRequest(new ProblemDetails { Title = "This document is not shareable", Detail = "Mark it as shareable in the Document Library first.", Status = StatusCodes.Status400BadRequest });

        var policy = await PolicyForAsync(media.OrganizationId);

        // The document's classification narrows the tenant policy, never widens it (plan D7).
        // Microsoft's conflict rule where a site default and a document label disagree: "the more
        // restrictive scope settings will be applied". So a Confidential document gets its shorter
        // cap, its forced email gate and its withheld download even if the tenant's own defaults are
        // laxer — and the person creating the link cannot opt out of any of them.
        var rule = policy.EffectiveFor(media.Classification);
        if (!rule.MayShare)
            return BadRequest(new ProblemDetails
            {
                Title = $"{media.Classification} documents cannot be shared by link",
                Detail = "This organization's sharing policy forbids it for documents at this classification.",
                Status = StatusCodes.Status400BadRequest
            });

        policy = policy with { MaxLinkDays = rule.MaxLinkDays ?? policy.MaxLinkDays };
        if (rule.AllowDownload == false) request = request with { AllowDownload = false };
        if (rule.RequireEmail == true) request = request with { RequireEmail = true };

        // Refuse a foreign link origin BEFORE the row exists, so a refusal never leaves a live
        // link behind that the caller was not told about.
        if (!string.IsNullOrWhiteSpace(request.LinkBaseUrl) && !TryBuildLink(request.LinkBaseUrl, "probe", out _))
            return BadRequest(new ProblemDetails { Title = "The link base URL is not one of this deployment's own origins", Detail = "Links are only ever built on the Web app's public address.", Status = StatusCodes.Status400BadRequest });

        DocumentShare share;
        string slug;
        try
        {
            (share, slug) = await _shares.IssueAsync(media, request, CurrentUserId(), policy);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = ex.Message, Status = StatusCodes.Status400BadRequest });
        }

        string? url = null;
        if (!string.IsNullOrWhiteSpace(request.LinkBaseUrl))
        {
            if (!TryBuildLink(request.LinkBaseUrl, slug, out var built))
            {
                // The link is issued (the row exists) but no URL is built or emailed for it. A
                // refusal here, not a silent omission, so the caller learns the origin is wrong.
                return BadRequest(new ProblemDetails { Title = "The link base URL is not one of this deployment's own origins", Detail = "Links are only ever built on the Web app's public address.", Status = StatusCodes.Status400BadRequest });
            }
            url = built;
        }

        var sent = 0;
        var failures = new List<string>();
        if (request.SendTo is { Count: > 0 })
        {
            if (url == null)
                return BadRequest(new ProblemDetails { Title = "A link base URL is needed to email the link", Status = StatusCodes.Status400BadRequest });

            foreach (var to in request.SendTo.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().Take(50))
            {
                var body = BuildLinkEmail(media, share, url, request.Message);
                var result = await _notifications.SendEmailAsync(media.OrganizationId, to, $"{media.Name} — shared with you", body, isHtml: true);
                if (result.IsSent) sent++;
                else failures.Add($"{to}: {result.Reason ?? "not sent"}");
            }
        }

        share.MediaContent = media;
        var dto = (await MapSharesAsync(new[] { share })).First();
        _logger.LogInformation("Share link {ShareId} issued for document {MediaId} by {UserId}; {Sent} email(s) sent", share.Id, media.Id, CurrentUserId(), sent);

        return CreatedAtAction(nameof(ListShares), new { mediaId }, new DocumentShareIssuedDto
        {
            Share = dto,
            Slug = slug,
            Url = url,
            EmailsSent = sent,
            EmailFailures = failures
        });
    }

    [HttpPut("media/{mediaId:guid}/shares/{shareId:guid}")]
    [RequirePermission(Permissions.DocumentsShareManage)]
    [ProducesResponseType(typeof(DocumentShareDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateShare(Guid mediaId, Guid shareId, [FromBody] UpdateDocumentShareRequest request)
    {
        var share = await _db.DocumentShares.Include(s => s.MediaContent).FirstOrDefaultAsync(s => s.Id == shareId && s.MediaContentId == mediaId);
        if (share == null) return NotFound();
        if (share.RevokedAt != null)
            return BadRequest(new ProblemDetails { Title = "A revoked link cannot be edited", Detail = "Issue a new link instead.", Status = StatusCodes.Status400BadRequest });

        try
        {
            await _shares.ApplyUpdateAsync(share, request, CurrentUserId(), await PolicyForAsync(share.MediaContent!.OrganizationId));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = ex.Message, Status = StatusCodes.Status400BadRequest });
        }

        return Ok((await MapSharesAsync(new[] { share })).First());
    }

    /// <summary>Revokes. Never deletes: the history of who could open a document is the audit answer.</summary>
    [HttpDelete("media/{mediaId:guid}/shares/{shareId:guid}")]
    [RequirePermission(Permissions.DocumentsShareManage)]
    [ProducesResponseType(typeof(DocumentShareDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeShare(Guid mediaId, Guid shareId, [FromBody] RevokeDocumentShareRequest? request)
    {
        var share = await _db.DocumentShares.Include(s => s.MediaContent).FirstOrDefaultAsync(s => s.Id == shareId && s.MediaContentId == mediaId);
        if (share == null) return NotFound();

        await _shares.RevokeAsync(share, CurrentUserId(), request?.Reason);
        _logger.LogInformation("Share link {ShareId} revoked by {UserId}", shareId, CurrentUserId());
        return Ok((await MapSharesAsync(new[] { share })).First());
    }

    // ---- Activity --------------------------------------------------------------------------------

    [HttpGet("media/{mediaId:guid}/shares/{shareId:guid}/events")]
    [RequirePermission(Permissions.DocumentsShareAudit)]
    [ProducesResponseType(typeof(List<DocumentShareEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListShareEvents(Guid mediaId, Guid shareId, [FromQuery] int limit = 500)
    {
        var exists = await _db.DocumentShares.AnyAsync(s => s.Id == shareId && s.MediaContentId == mediaId);
        if (!exists) return NotFound();

        var events = await _db.DocumentShareEvents
            .Include(e => e.Share)
            .Where(e => e.ShareId == shareId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(limit, 1, 2000))
            .ToListAsync();

        return Ok(await MapEventsAsync(events));
    }

    [HttpGet("media/{mediaId:guid}/activity")]
    [RequirePermission(Permissions.DocumentsShareAudit)]
    [ProducesResponseType(typeof(DocumentActivityDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivity(Guid mediaId)
    {
        var media = await FindDocumentAsync(mediaId);
        if (media == null) return NotFound();

        var now = DateTime.UtcNow;
        var shares = await _db.DocumentShares.Include(s => s.MediaContent).Where(s => s.MediaContentId == mediaId).ToListAsync();
        var events = await _db.DocumentShareEvents.Include(e => e.Share)
            .Where(e => e.Share!.MediaContentId == mediaId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        var opens = events.Where(e => e.Type == DocumentShareEventType.Opened).ToList();
        var pages = events.Where(e => e.Type == DocumentShareEventType.PageViewed && e.Page.HasValue)
            .GroupBy(e => e.Page!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new DocumentPageDwellDto { Page = g.Key, Views = g.Count(), TotalDwellSeconds = g.Sum(e => e.DwellSeconds ?? 0) })
            .ToList();

        var policy = await PolicyForAsync(media.OrganizationId);

        return Ok(new DocumentActivityDto
        {
            MediaContentId = media.Id,
            DocumentName = media.Name,
            Opens = opens.Count,
            UniqueViewers = opens.Where(e => e.Email != null).Select(e => e.Email!).Distinct().Count(),
            AnonymousOpens = opens.Count(e => e.Email == null),
            Downloads = events.Count(e => e.Type == DocumentShareEventType.Downloaded && e.Success),
            Denials = events.Count(e => e.Type == DocumentShareEventType.Denied || (e.Type == DocumentShareEventType.PasscodeFailed) || e.Type == DocumentShareEventType.EmailRejected),
            LinksTotal = shares.Count,
            LinksActive = shares.Count(s => _shares.EvaluateState(s, now) == DocumentShareState.Active),
            LastOpenedAt = opens.Select(e => (DateTime?)e.CreatedAt).FirstOrDefault(),
            Pages = pages,
            RecentEvents = await MapEventsAsync(events.Take(200).ToList()),
            AttributionRetentionDays = policy.AttributionRetentionDays
        });
    }

    /// <summary>The full event log for one document as CSV — the auditor's format.</summary>
    [HttpGet("media/{mediaId:guid}/activity/export")]
    [RequirePermission(Permissions.DocumentsShareAudit)]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportActivity(Guid mediaId)
    {
        var media = await FindDocumentAsync(mediaId);
        if (media == null) return NotFound();

        var events = await _db.DocumentShareEvents.Include(e => e.Share)
            .Where(e => e.Share!.MediaContentId == mediaId)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();
        var mapped = await MapEventsAsync(events);

        var sb = new StringBuilder();
        sb.AppendLine("Timestamp (UTC),Link,Event,Success,Viewer email,Address,Browser,Page,Dwell seconds,Staff member,Detail");
        foreach (var e in mapped)
        {
            sb.AppendLine(string.Join(",",
                Csv(e.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(e.ShareLabel ?? e.ShareId.ToString("N")[..8]),
                Csv(e.Type.ToString()),
                Csv(e.Success ? "yes" : "no"),
                Csv(e.Email), Csv(e.IpAddress), Csv(e.UserAgent),
                Csv(e.Page?.ToString()), Csv(e.DwellSeconds?.ToString()),
                Csv(e.ActorName), Csv(e.Detail)));
        }

        var safeName = string.Concat(media.Name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Trim();
        if (safeName.Length == 0) safeName = "document";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"{safeName} - share activity.csv");
    }

    /// <summary>
    /// One CSV cell. Staff open this file in Excel, and several columns are text an ANONYMOUS
    /// viewer typed (the address on a rejected email attempt, for one), so a cell that starts
    /// with a formula character is neutralised with a leading apostrophe — the standard
    /// defence against CSV injection (`=HYPERLINK(...)`, `=cmd|...`). Found by the security review.
    /// </summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value;
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            v = "'" + v;
        var needsQuotes = v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r') || v[0] == '\'';
        var escaped = v.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    // ---- Policy ----------------------------------------------------------------------------------

    [HttpGet("organizations/{organizationId:guid}/document-sharing/policy")]
    [RequirePermission(Permissions.DocumentsShareCreate)]
    [ProducesResponseType(typeof(DocumentSharingPolicyDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPolicy(Guid organizationId)
    {
        var orgId = ResolveOrganization(organizationId, out var error);
        if (error != null) return error;
        return Ok(await PolicyForAsync(orgId));
    }

    [HttpPut("organizations/{organizationId:guid}/document-sharing/policy")]
    [RequirePermission(Permissions.DocumentsShareManage)]
    [ProducesResponseType(typeof(DocumentSharingPolicyDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePolicy(Guid organizationId, [FromBody] DocumentSharingPolicyDto request)
    {
        var orgId = ResolveOrganization(organizationId, out var error);
        if (error != null) return error;

        if (request.MaxLinkDays < 0 || request.MaxLinkDays > 3650)
            return BadRequest(new ProblemDetails { Title = "Maximum link lifetime must be between 0 (no cap) and 3650 days", Status = StatusCodes.Status400BadRequest });
        if (request.AttributionRetentionDays < 1 || request.AttributionRetentionDays > 3650)
            return BadRequest(new ProblemDetails { Title = "Attribution retention must be between 1 and 3650 days", Status = StatusCodes.Status400BadRequest });

        // Organization.Settings is one JSON blob shared with the staff-performance policy, staff
        // onboarding, industry features, visitor retention and module billing. Every writer
        // read-modify-writes the whole thing, so a save here that raced a save there silently
        // dropped one of the two keys. Take the lock, then RE-READ inside it — the blob fetched
        // before the lock is exactly the stale copy the race is made of.
        var strategy = _db.Database.CreateExecutionStrategy();
        var saved = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            await OrganizationSettingsLock.AcquireAsync(_db, orgId);

            var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId);
            if (org == null) return false;

            org.Settings = _shares.WritePolicy(org.Settings, request);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return true;
        });
        if (!saved) return NotFound();

        _logger.LogInformation("Document-sharing policy updated for organization {OrganizationId} by {UserId}", orgId, CurrentUserId());
        return Ok(request);
    }

    /// <summary>A tenant caller is pinned to their own organization whatever the route says; a SuperAdmin may name one.</summary>
    private Guid ResolveOrganization(Guid requested, out IActionResult? error)
    {
        var tenant = _tenantAccessor.TenantContext;
        if (RoleCodes.IsSuperAdmin(tenant?.UserRole)) { error = null; return requested; }
        if (tenant == null || !tenant.IsResolved)
        {
            error = Unauthorized(new ProblemDetails { Title = "Organization not resolved", Status = StatusCodes.Status401Unauthorized });
            return Guid.Empty;
        }
        error = null;
        return tenant.OrganizationId;
    }

    // ---- Mapping ---------------------------------------------------------------------------------

    private async Task<List<DocumentShareDto>> MapSharesAsync(IEnumerable<DocumentShare> shares)
    {
        var list = shares.ToList();
        var ids = list.Select(s => s.Id).ToList();
        var now = DateTime.UtcNow;

        var stats = await _db.DocumentShareEvents
            .Where(e => ids.Contains(e.ShareId))
            .GroupBy(e => e.ShareId)
            .Select(g => new
            {
                ShareId = g.Key,
                Viewers = g.Where(e => e.Type == DocumentShareEventType.Opened && e.Email != null).Select(e => e.Email).Distinct().Count(),
                Downloads = g.Count(e => e.Type == DocumentShareEventType.Downloaded && e.Success),
                Denials = g.Count(e => e.Type == DocumentShareEventType.Denied || e.Type == DocumentShareEventType.PasscodeFailed || e.Type == DocumentShareEventType.EmailRejected)
            })
            .ToDictionaryAsync(x => x.ShareId);

        var userIds = list.SelectMany(s => new[] { s.CreatedBy, s.RevokedByUserId }).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var names = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.FullName ?? u.Username })
            .ToDictionaryAsync(u => u.Id, u => u.Name);

        return list.Select(s =>
        {
            stats.TryGetValue(s.Id, out var st);
            return new DocumentShareDto
            {
                Id = s.Id,
                MediaContentId = s.MediaContentId,
                Label = s.Label,
                RequiresPasscode = s.RequiresPasscode,
                RequireEmail = s.RequiresEmailVerification,
                AllowedEmails = _shares.AllowedEmails(s).ToList(),
                NotBefore = s.NotBefore,
                ExpiresAt = s.ExpiresAt,
                MaxViews = s.MaxViews,
                ViewCount = s.ViewCount,
                AllowDownload = s.AllowDownload,
                Watermark = s.Watermark,
                WatermarkText = s.WatermarkText,
                NotifyOnFirstOpen = s.NotifyOnFirstOpen,
                State = _shares.EvaluateState(s, now),
                RevokedAt = s.RevokedAt,
                RevokeReason = s.RevokeReason,
                RevokedByName = s.RevokedByUserId.HasValue ? names.GetValueOrDefault(s.RevokedByUserId.Value) : null,
                FirstOpenedAt = s.FirstOpenedAt,
                LastOpenedAt = s.LastOpenedAt,
                CreatedAt = s.CreatedAt,
                CreatedByName = s.CreatedBy.HasValue ? names.GetValueOrDefault(s.CreatedBy.Value) : null,
                UniqueViewers = st?.Viewers ?? 0,
                Downloads = st?.Downloads ?? 0,
                Denials = st?.Denials ?? 0
            };
        }).ToList();
    }

    private async Task<List<DocumentShareEventDto>> MapEventsAsync(List<DocumentShareEvent> events)
    {
        var actorIds = events.Where(e => e.ActorUserId.HasValue).Select(e => e.ActorUserId!.Value).Distinct().ToList();
        var names = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, Name = u.FullName ?? u.Username })
                .ToDictionaryAsync(u => u.Id, u => u.Name);

        return events.Select(e => new DocumentShareEventDto
        {
            Id = e.Id,
            ShareId = e.ShareId,
            ShareLabel = e.Share?.Label,
            Type = e.Type,
            Success = e.Success,
            Email = e.Email,
            IpAddress = e.IpAddress,
            UserAgent = e.UserAgent,
            Page = e.Page,
            DwellSeconds = e.DwellSeconds,
            ActorName = e.ActorUserId.HasValue ? names.GetValueOrDefault(e.ActorUserId.Value) : null,
            Detail = e.Detail,
            SessionId = e.SessionId,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    /// <summary>
    /// The emailed link is built on the WEB app's public origin. The Web app sends its own origin,
    /// but that value arrives from a client and this is a URL a stranger will click in an email
    /// sent from the platform's mailbox — so the origin must be one this deployment KNOWS:
    /// MediaStorage:PublicBaseUrl (the public host in production) or a CORS-allowed origin (the
    /// Web app's own address, locally). Anything else is refused rather than emailed. The
    /// security review's point: without this, any tenant could send platform-branded mail whose
    /// link pointed at a look-alike host.
    /// </summary>
    private bool TryBuildLink(string? baseUrl, string slug, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var allowed = new List<string>();
        var publicBase = _configuration["MediaStorage:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(publicBase) && Uri.TryCreate(publicBase, UriKind.Absolute, out var pb))
            allowed.Add(pb.GetLeftPart(UriPartial.Authority));
        allowed.AddRange(_configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>());

        if (!allowed.Any(a => string.Equals(a.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Refused a share-link base URL outside the allowed origins: {Origin}", origin);
            return false;
        }

        url = new Uri(uri, $"/s/{slug}").ToString();
        return true;
    }

    private static string BuildLinkEmail(MediaContent doc, DocumentShare share, string url, string? message)
    {
        Func<string?, string> e = s => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var rules = new List<string>();
        if (share.RequiresPasscode) rules.Add("You will need the passcode, which is being sent to you separately.");
        if (share.RequiresEmailVerification) rules.Add("You will be asked to confirm your email address with a one-time code.");
        if (share.ExpiresAt.HasValue) rules.Add($"The link expires on {share.ExpiresAt.Value.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}.");
        if (!share.AllowDownload) rules.Add("The document is view-only; the original cannot be downloaded.");

        var sb = new StringBuilder();
        sb.Append("<p>A document has been shared with you");
        sb.Append(string.IsNullOrWhiteSpace(doc.Organization?.Name) ? "" : $" by {e(doc.Organization!.Name)}");
        sb.Append(".</p>");
        sb.Append($"<p><strong>{e(doc.Name)}</strong>");
        if (!string.IsNullOrWhiteSpace(doc.Summary)) sb.Append($"<br/>{e(doc.Summary)}");
        sb.Append("</p>");
        if (!string.IsNullOrWhiteSpace(message)) sb.Append($"<p>{e(message.Trim())}</p>");
        sb.Append($"<p><a href=\"{e(url)}\">Open the document</a><br/><small>{e(url)}</small></p>");
        if (rules.Count > 0) sb.Append("<ul>" + string.Concat(rules.Select(r => $"<li>{e(r)}</li>")) + "</ul>");
        return sb.ToString();
    }
}
