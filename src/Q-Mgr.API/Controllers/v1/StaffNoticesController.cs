using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Staff notices (plan §8, Phase 3): to a branch, a set of departments, a set of roles or a staff
/// group; optionally pinned, optionally requiring acknowledgement, with Library documents attached.
///
/// Publishing fans out one Notification per recipient through <see cref="StaffNoticeFanOut"/>, so
/// the bell, email preferences, the delivery log and per-person read state all come free; the
/// Notification row and the notice's Acknowledgements map together are the read receipt — there is
/// no third table. A notice scheduled for later is fanned out by StaffPerformanceJobs when its
/// PublishAt arrives, through the SAME helper, so the audience rule has one home.
///
/// The body is sanitised on write with <see cref="StaffNoticeHtml"/>. The reader's own
/// "acknowledge" lives on the portal controller (it is the caller's own action); this controller
/// is the publisher's side.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffNoticesController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly INotificationService _notifications;
    private readonly IActivityLogger _activity;
    private readonly ILogger<StaffNoticesController> _logger;

    public StaffNoticesController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        INotificationService notifications,
        IActivityLogger activity,
        ILogger<StaffNoticesController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _notifications = notifications;
        _activity = activity;
        _logger = logger;
    }

    // ---- Guards -------------------------------------------------------------------------------------

    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails { Title = "Tenant not resolved", Status = StatusCodes.Status401Unauthorized });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var superAdminBranchExists = await _context.Branches.AnyAsync(b => b.Id == branchId);
            return superAdminBranchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
        }

        var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        return branchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
    }

    private async Task<Guid> ResolveOrganizationIdAsync(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext!;
        return RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync()
            : tenantContext.OrganizationId;
    }

    private Guid CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : Guid.Empty;
    }

    private static IActionResult NotFoundNotice() => new NotFoundObjectResult(new ProblemDetails { Title = "Notice not found", Status = StatusCodes.Status404NotFound });

    private static IActionResult Problem400(string title, string? detail = null)
        => new BadRequestObjectResult(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status400BadRequest });

    // ---- Read ---------------------------------------------------------------------------------------

    /// <summary>The caller's notices: published, not expired, active, addressed to them by branch, department, role and group. Pinned first.</summary>
    [HttpGet("branches/{branchId:guid}/staff/notices")]
    [ProducesResponseType(typeof(List<StaffNoticeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMine(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var caller = await _context.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == me)
            .Select(u => new { u.AssignedBranchId, RoleCode = u.Role.Code, u.DepartmentIds })
            .FirstOrDefaultAsync();
        if (caller == null) return Ok(new List<StaffNoticeDto>());

        var now = DateTime.UtcNow;
        var notices = await _context.StaffNotices.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.OrganizationId == organizationId && n.IsActive
                        && (n.BranchId == null || n.BranchId == branchId)
                        && n.PublishAt <= now
                        && (n.ExpiresAt == null || n.ExpiresAt > now))
            .OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.PublishAt)
            .ToListAsync();

        // The reader-side filter is the fan-out's own predicate: what you see is what you were told about.
        var mine = notices.Where(n => StaffNoticeFanOut.IsRecipient(n, caller.AssignedBranchId ?? branchId, caller.RoleCode, caller.DepartmentIds)).ToList();
        return Ok(await MapManyAsync(mine, organizationId, me, withRecipientCounts: false));
    }

    /// <summary>Every notice of the organization addressed to this branch or org-wide, any state, with recipient and acknowledgement counts.</summary>
    [HttpGet("branches/{branchId:guid}/staff/notices/manage")]
    [RequirePermission(Permissions.StaffNoticesManage)]
    [ProducesResponseType(typeof(List<StaffNoticeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetManage(Guid branchId, [FromQuery] bool includeWithdrawn = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var query = _context.StaffNotices.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.OrganizationId == organizationId && (n.BranchId == null || n.BranchId == branchId));
        if (!includeWithdrawn) query = query.Where(n => n.IsActive);

        var notices = await query.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.PublishAt).ToListAsync();
        return Ok(await MapManyAsync(notices, organizationId, CurrentUserId(), withRecipientCounts: true));
    }

    /// <summary>Who the notice reached, who has acknowledged it, and who has at least opened the bell entry.</summary>
    [HttpGet("branches/{branchId:guid}/staff/notices/{noticeId:guid}/acknowledgements")]
    [RequirePermission(Permissions.StaffNoticesManage)]
    [ProducesResponseType(typeof(List<NoticeAcknowledgementDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAcknowledgements(Guid branchId, Guid noticeId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var notice = await _context.StaffNotices.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == noticeId && n.OrganizationId == organizationId && (n.BranchId == null || n.BranchId == branchId));
        if (notice == null) return NotFoundNotice();

        var recipients = await StaffNoticeFanOut.ResolveRecipientsAsync(_context, notice);
        var acks = StaffPerformanceMapping.ParseAcknowledgements(notice.Acknowledgements);

        // Read state comes from the fan-out's own Notification rows, matched on the noticeId the
        // fan-out stamps into MetaData — more reliable than a title match, which a retitled notice
        // would break.
        var marker = noticeId.ToString();
        var readBy = (await _context.Notifications.AsNoTracking()
                .Where(n => n.OrganizationId == organizationId
                            && n.EventKey == NotificationEventKeys.StaffNoticePublished
                            && n.UserId != null && n.MetaData != null && n.MetaData.Contains(marker))
                .Select(n => new { n.UserId, n.IsRead })
                .ToListAsync())
            .Where(n => n.IsRead)
            .Select(n => n.UserId!.Value)
            .ToHashSet();

        var result = recipients
            .Select(r => new NoticeAcknowledgementDto
            {
                UserId = r.UserId,
                FullName = r.FullName,
                AcknowledgedAt = acks.TryGetValue(r.UserId, out var at) ? at : null,
                IsRead = readBy.Contains(r.UserId) || acks.ContainsKey(r.UserId)
            })
            .OrderBy(r => r.AcknowledgedAt.HasValue).ThenBy(r => r.FullName)
            .ToList();

        return Ok(result);
    }

    // ---- Write --------------------------------------------------------------------------------------

    /// <summary>Publishes now (PublishAt ≤ now) or schedules. A published notice is fanned out before this returns.</summary>
    [HttpPost("branches/{branchId:guid}/staff/notices")]
    [RequirePermission(Permissions.StaffNoticesManage)]
    [ProducesResponseType(typeof(StaffNoticeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(Guid branchId, [FromBody] SaveStaffNoticeRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var notice = new StaffNotice { OrganizationId = organizationId, PublishedByUserId = me, CreatedBy = me };

        var error = await ApplyAsync(notice, request, organizationId, branchId);
        if (error != null) return error;

        _context.StaffNotices.Add(notice);
        await _context.SaveChangesAsync();

        var scheduled = notice.PublishAt > DateTime.UtcNow;
        await _activity.RecordAsync(ActivityActions.NoticePublished, nameof(StaffNotice), notice.Id, null,
            scheduled ? $"Notice \"{notice.Title}\" scheduled for {notice.PublishAt:dd MMM yyyy HH:mm} UTC" : $"Notice \"{notice.Title}\" published",
            new { notice.BranchId, Departments = notice.AudienceDepartmentIds?.Length, Roles = notice.AudienceRoleCodes, notice.AudienceStaffGroup, notice.IsPinned, notice.RequiresAcknowledgement, notice.PublishAt },
            branchId, organizationId);

        // After the commit; the helper swallows per-recipient failures and stamps NotificationsSentAt.
        if (!scheduled)
            await StaffNoticeFanOut.FanOutAsync(_context, _notifications, notice, _logger);

        var dto = (await MapManyAsync(new List<StaffNotice> { notice }, organizationId, me, withRecipientCounts: true)).First();
        return CreatedAtAction(nameof(GetManage), new { branchId }, dto);
    }

    /// <summary>Edits a notice. A scheduled notice whose PublishAt is moved into the past is fanned out here; one already fanned out is never fanned out again.</summary>
    [HttpPut("branches/{branchId:guid}/staff/notices/{noticeId:guid}")]
    [RequirePermission(Permissions.StaffNoticesManage)]
    [ProducesResponseType(typeof(StaffNoticeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid branchId, Guid noticeId, [FromBody] SaveStaffNoticeRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var notice = await _context.StaffNotices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.Id == noticeId && n.OrganizationId == organizationId && (n.BranchId == null || n.BranchId == branchId));
        if (notice == null || !notice.IsActive) return NotFoundNotice();

        var error = await ApplyAsync(notice, request, organizationId, branchId);
        if (error != null) return error;

        var me = CurrentUserId();
        notice.UpdatedAt = DateTime.UtcNow;
        notice.UpdatedBy = me;
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.NoticeUpdated, nameof(StaffNotice), notice.Id, null,
            $"Notice \"{notice.Title}\" updated", new { notice.PublishAt, notice.ExpiresAt, notice.IsPinned, notice.RequiresAcknowledgement }, branchId, organizationId);

        await StaffNoticeFanOut.FanOutAsync(_context, _notifications, notice, _logger);

        return Ok((await MapManyAsync(new List<StaffNotice> { notice }, organizationId, me, withRecipientCounts: true)).First());
    }

    /// <summary>Withdraws a notice (IsActive = false). The row and its acknowledgements stay; readers stop seeing it at once.</summary>
    [HttpDelete("branches/{branchId:guid}/staff/notices/{noticeId:guid}")]
    [RequirePermission(Permissions.StaffNoticesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Withdraw(Guid branchId, Guid noticeId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var notice = await _context.StaffNotices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.Id == noticeId && n.OrganizationId == organizationId && (n.BranchId == null || n.BranchId == branchId));
        if (notice == null || !notice.IsActive) return NotFoundNotice();

        notice.IsActive = false;
        notice.UpdatedAt = DateTime.UtcNow;
        notice.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.NoticeWithdrawn, nameof(StaffNotice), notice.Id, null,
            $"Notice \"{notice.Title}\" withdrawn", null, branchId, organizationId);

        return NoContent();
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private async Task<IActionResult?> ApplyAsync(StaffNotice notice, SaveStaffNoticeRequest request, Guid organizationId, Guid branchId)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return Problem400("A title is required");

        // The route branch, or org-wide. A notice cannot be addressed to some other branch from here.
        if (request.BranchId.HasValue && request.BranchId.Value != branchId)
            return Problem400("A notice is published to this branch or to the whole organization", "Leave the branch empty for an organization-wide notice.");

        var body = StaffNoticeHtml.Sanitize(UploadLinks.StripAll(request.BodyHtml));
        if (StaffNoticeHtml.ToPlainText(body).Length == 0) return Problem400("The notice needs a body");

        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= request.PublishAt)
            return Problem400("The notice would expire before it is published");

        Guid[]? departments = null;
        if (request.AudienceDepartmentIds is { Count: > 0 } deptIds)
        {
            var wanted = deptIds.Distinct().ToList();
            var known = await _context.Departments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => wanted.Contains(d.Id) && d.OrganizationId == organizationId)
                .Select(d => d.Id).ToListAsync();
            if (known.Count != wanted.Count) return Problem400("One or more departments were not found in this organization");
            departments = known.ToArray();
        }

        string[]? roles = null;
        if (request.AudienceRoleCodes is { Count: > 0 } roleCodes)
        {
            var wanted = roleCodes.Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).Distinct().ToList();
            var known = await _context.Roles.IgnoreQueryFilters().AsNoTracking()
                .Where(r => wanted.Contains(r.Code.ToLower()) && (r.OrganizationId == null || r.OrganizationId == organizationId))
                .Select(r => r.Code.ToLower()).Distinct().ToListAsync();
            if (known.Count != wanted.Count) return Problem400("One or more role codes are not roles of this organization");
            roles = known.ToArray();
        }

        Guid[]? attachments = null;
        if (request.AttachmentMediaContentIds is { Count: > 0 } mediaIds)
        {
            var wanted = mediaIds.Distinct().ToList();
            var known = await _context.MediaContents.IgnoreQueryFilters().AsNoTracking()
                .Where(m => wanted.Contains(m.Id) && m.OrganizationId == organizationId && m.IsActive)
                .Select(m => m.Id).ToListAsync();
            if (known.Count != wanted.Count) return Problem400("One or more attachments are not documents in this organization's Library");
            attachments = known.ToArray();
        }

        notice.BranchId = request.BranchId;
        notice.Title = title.Length > 200 ? title[..200] : title;
        notice.BodyHtml = body;
        notice.AudienceDepartmentIds = departments;
        notice.AudienceRoleCodes = roles;
        notice.AudienceStaffGroup = request.AudienceStaffGroup == StaffGroup.AllStaff ? null : request.AudienceStaffGroup;
        notice.PublishAt = DateTime.SpecifyKind(request.PublishAt == default ? DateTime.UtcNow : request.PublishAt, DateTimeKind.Utc);
        notice.ExpiresAt = request.ExpiresAt.HasValue ? DateTime.SpecifyKind(request.ExpiresAt.Value, DateTimeKind.Utc) : null;
        notice.IsPinned = request.IsPinned;
        notice.RequiresAcknowledgement = request.RequiresAcknowledgement;
        notice.AttachmentMediaContentIds = attachments;
        return null;
    }

    private async Task<List<StaffNoticeDto>> MapManyAsync(List<StaffNotice> notices, Guid organizationId, Guid callerId, bool withRecipientCounts)
    {
        if (notices.Count == 0) return new List<StaffNoticeDto>();

        var names = await StaffLookups.LoadNamesAsync(_context, notices.Select(n => (Guid?)n.PublishedByUserId));

        var mediaIds = notices.Where(n => n.AttachmentMediaContentIds != null).SelectMany(n => n.AttachmentMediaContentIds!).Distinct().ToList();
        var media = mediaIds.Count == 0
            ? new Dictionary<Guid, (string Name, string? Url)>()
            : (await _context.MediaContents.IgnoreQueryFilters().AsNoTracking()
                .Where(m => mediaIds.Contains(m.Id))
                .Select(m => new { m.Id, m.Name, m.FileUrl })
                .ToListAsync())
              .ToDictionary(m => m.Id, m => (Name: m.Name, Url: m.FileUrl));

        // Recipient counts: one candidate load, the predicate applied per notice in memory. A branch
        // has a few hundred staff at most; this is cheaper than a query per notice.
        List<(Guid? Branch, string? Role, Guid[]? Depts)>? candidates = null;
        if (withRecipientCounts)
        {
            candidates = (await _context.Users.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.OrganizationId == organizationId && u.IsActive && u.Role.Code != RoleCodes.SuperAdmin)
                    .Select(u => new { u.AssignedBranchId, RoleCode = u.Role.Code, u.DepartmentIds })
                    .ToListAsync())
                .Select(u => (u.AssignedBranchId, (string?)u.RoleCode, u.DepartmentIds))
                .ToList();
        }

        return notices.Select(n =>
        {
            var attachments = (n.AttachmentMediaContentIds ?? Array.Empty<Guid>())
                .Where(media.ContainsKey)
                .Select(id => new NoticeAttachmentDto { MediaContentId = id, Name = media[id].Name, FileUrl = UploadLinks.Sign(media[id].Url) ?? media[id].Url })
                .ToList();
            var recipientCount = candidates?.Count(c => StaffNoticeFanOut.IsRecipient(n, c.Branch, c.Role, c.Depts)) ?? 0;
            return StaffPerformanceMapping.ToDto(n, names, callerId, recipientCount, attachments);
        }).ToList();
    }
}
