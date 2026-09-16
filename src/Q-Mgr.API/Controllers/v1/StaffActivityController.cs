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
[RequireModule(ModuleCodes.StaffPerformance)]
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
                query = query.Where(e => (e.SubjectUserId != null && ids.Contains(e.SubjectUserId.Value))
                                         || (e.ActorUserId != null && ids.Contains(e.ActorUserId.Value)));
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
}
