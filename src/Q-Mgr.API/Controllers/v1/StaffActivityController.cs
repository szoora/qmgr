using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Audit;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The activity log for a branch: who did what, to which record, from where. Read scoped the same
/// way as the records (plan §11): an unscoped caller sees the branch; a head sees actions on their
/// department's people, whether as subject or as actor; the subject's own trail is the portal's
/// business, not this controller's. Summaries were written at the actor's visibility when the event
/// happened, so nothing here needs to re-redact.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff/activity")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — the action also carries its own [RequirePermission]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffActivityController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private const int MaxPageSize = 200;

    public StaffActivityController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
    }

    [HttpGet]
    [RequirePermission(Permissions.StaffRecordsView)]
    [ProducesResponseType(typeof(ActivityLogPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivity(
        Guid branchId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? action = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);

        // Branch events plus org-level ones (a parameter or policy change has no branch) for the
        // unscoped caller; only events touching visible people for a scoped one — which by
        // construction excludes the org-level rows, since those have no subject.
        IQueryable<ActivityEvent> query = Db.ActivityEvents.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && (e.BranchId == branchId || e.BranchId == null));

        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);
        if (visible != null)
        {
            if (visible.Count == 0) query = query.Where(_ => false);
            else
            {
                var ids = visible.ToList();
                // An event ABOUT somebody is shown only when that somebody is in scope; the actor rule
                // covers subject-less events alone (a head seeing their teacher edit a duty). Before
                // 2026-09-17 an in-scope actor was enough, so once registers wrote a line per marked
                // person, a Maths teacher recording a mixed meeting put "Luke Opio marked Present" —
                // a Languages teacher — into the head of Maths's log (caught by e2e 14.9).
                query = query.Where(e => (e.SubjectUserId != null && ids.Contains(e.SubjectUserId.Value))
                                         || (e.SubjectUserId == null && e.ActorUserId != null && ids.Contains(e.ActorUserId.Value)));
            }
        }

        if (userId.HasValue) query = query.Where(e => e.SubjectUserId == userId.Value || e.ActorUserId == userId.Value);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(e => e.Action == action.Trim());

        var total = await query.CountAsync();
        var counts = await query.GroupBy(e => e.Action).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var events = await query.OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();

        var names = await BuildNamesAsync(events.Select(e => e.ActorUserId).Concat(events.Select(e => e.SubjectUserId)));

        return Ok(new ActivityLogPageDto
        {
            Items = events.Select(e => StaffPerformanceMapping.ToDto(e, names)).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            AttributionRetentionDays = policy.ActivityAttributionRetentionDays,
            CountsByAction = counts,
            ScopedToDepartments = (await StaffScope.GetScopedDepartmentNamesAsync()).ToList()
        });
    }

    /// <summary>
    /// Records an export or a Publish to Library that happened in the browser (plan §11: "exports and PDF
    /// publishes"). The CSV/Excel/PDF is produced client-side by QDataExport and the print route renders its
    /// own PDF, so the server never sees the file — the page reports it here straight after. The caller
    /// must hold the permission the exported list itself needs, and a person's timeline needs that person
    /// in scope, so this cannot be used to write a line about someone the caller could not have exported.
    /// Before 2026-09-17 two of these actions were defined and never written; only "export my file" was.
    /// </summary>
    [HttpPost("exports")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordExport(Guid branchId, [FromBody] RecordStaffExportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var (permission, label) = request.Kind switch
        {
            StaffExportKinds.Directory => (Permissions.StaffRecordsView, "the staff directory"),
            StaffExportKinds.Records => (Permissions.StaffRecordsView, "a staff records search"),
            StaffExportKinds.Timeline => (Permissions.StaffRecordsView, "a staff timeline"),
            StaffExportKinds.Reports => (Permissions.StaffReportsView, "the staff performance reports"),
            StaffExportKinds.NoticeAcknowledgements => (Permissions.StaffNoticesManage, "a notice's acknowledgements"),
            StaffExportKinds.Activity => (Permissions.StaffRecordsView, "the activity log"),
            StaffExportKinds.DutyReports => (Permissions.StaffDutyReportsView, "a duty report pack"),
            // Adopted minutes are readable by everybody who was expected at the meeting, so the print
            // carries no permission of its own — but it IS logged, because a set of minutes leaving
            // the system as a file is the same class of event as a timetable leaving it.
            StaffExportKinds.Minutes => (string.Empty, "minutes of a meeting"),
            // A published timetable is readable by all branch staff (plan §13.5); the print is logged all the same.
            StaffExportKinds.Timetable => (string.Empty, "a timetable"),
            StaffExportKinds.TeachingReports => (string.Empty, "the lessons report"),
            // An appraisal is read by its subject, appraiser, moderator or a confidential-rung holder;
            // publishing one is staff-records work, so it is gated like a timeline.
            StaffExportKinds.Appraisal => (Permissions.StaffRecordsView, "an appraisal report"),
            _ => (null, null)
        };
        if (permission == null) return BadRequestProblem("Unrecognised export kind");
        if (permission.Length > 0 && !await HasPermissionAsync(permission)) return Forbid();

        string? subjectName = null;
        if (request.SubjectUserId is { } subjectId)
        {
            if (subjectId != CurrentUserId())
            {
                var scopeError = await StaffScope.VerifyStaffAccessAsync(branchId, subjectId);
                if (scopeError != null) return scopeError;
            }
            var names = await BuildNamesAsync(new Guid?[] { subjectId });
            subjectName = names[subjectId];
        }

        var format = string.IsNullOrWhiteSpace(request.Format) ? null : request.Format.Trim().ToUpperInvariant();
        var what = subjectName != null ? $"{label} of {subjectName}" : label;
        var summary = request.Published
            ? $"Published {what} to the Library" + (string.IsNullOrWhiteSpace(request.DocumentName) ? "" : $" as \"{Truncate(request.DocumentName.Trim(), 120)}\"")
            : $"Exported {what}" + (format != null ? $" as {format}" : "") + (request.RowCount is { } rows ? $" ({rows} row(s))" : "");

        var action = request.Published
            ? ActivityActions.ReportPublished
            : request.Kind == StaffExportKinds.Timeline ? ActivityActions.TimelineExported : ActivityActions.ListExported;

        await Activity.RecordAsync(action, request.Kind, request.MediaContentId, request.SubjectUserId, summary,
            new { request.Kind, Format = format, request.RowCount, request.Published, request.MediaContentId, request.PeriodKey },
            branchId, organizationId);

        return NoContent();
    }
}
