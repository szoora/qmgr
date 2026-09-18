using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Jobs;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The teaching reports (duty rota plan §11) and the dashboard's Teaching tiles. Readable by holders of
/// <c>staff.reports.view</c>, <c>timetable.lessons.flag</c> or <c>timetable.manage</c>; every row and aggregate is built
/// from the caller's staff scope, and the response says when it is scoped. Timetable health — whole-school clashes that
/// name teachers — is for an unscoped timetable master only.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff/reports/teaching")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class TeachingReportsController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly ITimetableSettingsService _settings;

    public TeachingReportsController(QMgrDbContext db, ITenantContextAccessor tenantAccessor, IStaffScopeService staffScope, IActivityLogger activity,
        IStaffPerformancePolicyService policy, ITimetableSettingsService settings)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _settings = settings;
    }

    [HttpGet]
    [ProducesResponseType(typeof(TeachingReportsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(Guid branchId, [FromQuery] string? period = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await MayReadAsync()) return Forbid();

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var zone = await ZoneAsync(branchId);
        var p = _policy.FindPeriod(policy, period) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone)));
        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);
        var health = visible == null && await HasPermissionAsync(Permissions.TimetableManage);

        return Ok(await TeachingReportBuilder.BuildAsync(Db, _settings, organizationId, branchId, p, policy, visible,
            await StaffScope.GetScopedDepartmentNamesAsync(), health, zone, DateTime.UtcNow));
    }

    /// <summary>This week's lessons, overdue duty reports and (for a master) the timetable's hard clashes, in scope.</summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(TeachingDashboardDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await MayReadAsync()) return Forbid();

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var zone = await ZoneAsync(branchId);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        var week = TeachingReportBuilder.WeekStart(today);
        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);
        var master = visible == null && await HasPermissionAsync(Permissions.TimetableManage);

        var thisWeek = new PerformancePeriodDto { Key = "week", Name = "This week", Start = week, End = week.AddDays(6) };
        var report = await TeachingReportBuilder.BuildAsync(Db, _settings, organizationId, branchId, thisWeek, policy, visible,
            await StaffScope.GetScopedDepartmentNamesAsync(), includeTimetableHealth: false, zone, now);
        var overdue = await Db.StaffDutyReports.AsNoTracking()
            .Where(r => r.BranchId == branchId && r.Duty!.IsActive && (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned)
                        && r.DueAt < now && r.DueAt >= now.AddDays(-31))
            .Select(r => r.AuthorUserId).ToListAsync();

        return Ok(new TeachingDashboardDto
        {
            TaughtPercentThisWeek = report.LessonsTotal.TaughtPercent,
            LessonsThisWeek = report.LessonsTotal.Scheduled,
            UnrecordedThisWeek = report.LessonsTotal.Unrecorded,
            NotRecovered = report.LessonsTotal.NotRecovered,
            DutyReportsOverdue = overdue.Count(id => visible == null || visible.Contains(id)),
            HardClashes = master ? (await TeachingReportBuilder.HealthAsync(Db, _settings, policy, branchId, zone)).Hard : null,
            ScopedToDepartments = report.ScopedToDepartments
        });
    }

    /// <summary>
    /// Runs the weekly lesson analysis NOW, ignoring its Monday-and-after-the-digest-hour gate.
    /// Development only (404 elsewhere): the analysis is otherwise unreachable on six days in seven, which is
    /// why it shipped unexercised. It still cannot double-send — the job's own once-a-week check stands — and
    /// it still reports in each recipient's own staff scope. Needs an unscoped timetable master.
    /// </summary>
    [HttpPost("weekly-analysis/run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RunWeeklyAnalysis(Guid branchId, [FromServices] IWebHostEnvironment environment,
        [FromServices] StaffPerformanceJobs jobs, [FromQuery] DateOnly? weekStart = null)
    {
        if (!environment.IsDevelopment()) return NotFound();
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (await StaffScope.GetVisibleUserIdsAsync(branchId) != null) return Forbid();
        if (!await HasPermissionAsync(Permissions.TimetableManage)) return Forbid();

        // weekStart names the Monday whose PREVIOUS week is reported, so a check can aim it at a week
        // that has lessons. Without it the job reports last week, exactly as the Monday run does.
        await jobs.SendWeeklyLessonAnalysisAsync(force: true, weekStartOverride: weekStart);
        return Accepted();
    }

    private async Task<bool> MayReadAsync()
        => await HasPermissionAsync(Permissions.StaffReportsView) || await HasPermissionAsync(Permissions.TimetableLessonsFlag) || await HasPermissionAsync(Permissions.TimetableManage);

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => AppointmentScheduling.ResolveTimeZone(await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());
}
