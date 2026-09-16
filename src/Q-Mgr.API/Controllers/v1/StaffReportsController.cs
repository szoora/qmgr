using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The Staff Performance reports page (plan §10, Phase 4): by department, by parameter, band
/// distribution, trend by week, observer dispersion (the "rate everyone average" check), who logs
/// what (the consistency check), registers not taken, appraisals by stage, the leaderboard behind
/// the policy switch, and the structure coverage list.
///
/// Every figure is restricted to the caller's visible staff (IStaffScopeService) and
/// <c>ScopedToDepartments</c> says so, exactly as WelfareReports does — a head of Maths reading
/// their department's total as the school's is a wrong conclusion drawn from a correct query.
/// Individual ranks appear ONLY when the tenant's LeaderboardMode is Public (plan §13 decision 2).
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StaffPerformance)]
public class StaffReportsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IStaffScopeService _scope;
    private readonly IStaffScoringService _scoring;
    private readonly IStaffPerformancePolicyService _policy;

    public StaffReportsController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService scope,
        IStaffScoringService scoring,
        IStaffPerformancePolicyService policy)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _scope = scope;
        _scoring = scoring;
        _policy = policy;
    }

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

    [HttpGet("branches/{branchId:guid}/staff/reports")]
    [RequirePermission(Permissions.StaffReportsView)]
    [ProducesResponseType(typeof(StaffReportsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReports(Guid branchId, [FromQuery] string? period = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var p = _policy.FindPeriod(policy, period) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));

        var visible = await _scope.GetVisibleUserIdsAsync(branchId);
        var dto = await StaffReportBuilder.BuildAsync(_context, _scoring, _policy, organizationId, branchId, p, policy, visible);

        return Ok(dto with { ScopedToDepartments = (await _scope.GetScopedDepartmentNamesAsync()).ToList() });
    }
}
