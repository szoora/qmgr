using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
/// The tenant's scoring policy: periods, bands, the weight cap, the leaderboard switch, budgets,
/// lead times, retention and the DPO contact. Lives in <c>Organization.Settings["StaffPerformance"]</c>
/// and is read and written ONLY through <see cref="IStaffPerformancePolicyService"/> — the same
/// one-reader rule as <c>"DocumentSharing"</c>; this controller never touches the JSON.
///
/// The band count is fixed at five so a stored rating means the same thing across periods; names
/// and thresholds are the tenant's. Periods, when defined, may not overlap: a date in two periods
/// would be scored twice.
/// </summary>
[ApiController]
[Route("api/v1/staff/policy")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — the write also carries its own [RequirePermission]
[RequireModule(ModuleCodes.StaffPerformance)]
public class StaffPolicyController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;

    public StaffPolicyController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
    }

    /// <summary>Any authenticated member of the module: the portal needs the bands, the budget and the leaderboard mode.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(StaffPerformancePolicyDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPolicy()
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();

        return Ok(await _policy.GetAsync(organizationId.Value));
    }

    [HttpPut]
    [RequirePermission(Permissions.StaffParametersManage)]
    [ProducesResponseType(typeof(StaffPerformancePolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdatePolicy([FromBody] StaffPerformancePolicyDto request)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();

        var error = Validate(request);
        if (error != null) return error;

        var current = await _policy.GetAsync(organizationId.Value);

        // Job bookkeeping is the job's to write, not the editor's; keep whatever the sweep last set.
        request.LastSummarySentAt = current.LastSummarySentAt;
        request.Periods = (request.Periods ?? new()).OrderBy(p => p.Start).ToList();
        request.Bands = request.Bands.OrderByDescending(b => b.MinScore).ToList();
        request.DataProtectionOfficerContact = string.IsNullOrWhiteSpace(request.DataProtectionOfficerContact) ? null : request.DataProtectionOfficerContact.Trim();

        await _policy.SaveAsync(organizationId.Value, request);

        await Activity.RecordAsync(ActivityActions.PolicySaved, "StaffPerformancePolicy", organizationId, null,
            "Staff performance policy saved",
            new
            {
                Before = Snapshot(current),
                After = Snapshot(request)
            });

        return Ok(await _policy.GetAsync(organizationId.Value));
    }

    private static object Snapshot(StaffPerformancePolicyDto p) => new
    {
        Periods = p.Periods?.Count ?? 0,
        Bands = p.Bands.Select(b => $"{b.Rating}:{b.Name}@{b.MinScore}").ToList(),
        p.MaxParameterWeightPercent,
        p.LeaderboardMode,
        p.LeaderboardTopN,
        p.RecognitionMonthlyBudget,
        p.DutyReminderLeadHours,
        p.LateEntryThresholdDays,
        p.ActivityAttributionRetentionDays,
        p.SystemAwardsEnabled,
        p.DigestWeekday,
        p.DigestHour,
        p.SummaryHour,
        HasDpoContact = !string.IsNullOrWhiteSpace(p.DataProtectionOfficerContact)
    };

    private IActionResult? Validate(StaffPerformancePolicyDto p)
    {
        // Bands: exactly five, ratings 1..5 each used once, thresholds strictly descending with
        // rating, and the lowest band starting at 0 so every composite lands somewhere.
        if (p.Bands == null || p.Bands.Count != 5)
            return BadRequestProblem("There must be exactly five bands", "The five-point scale is fixed so a stored rating means the same thing across periods; rename the bands instead.");
        var ordered = p.Bands.OrderByDescending(b => b.Rating).ToList();
        if (ordered.Select(b => b.Rating).Distinct().Count() != 5 || ordered.Any(b => b.Rating < 1 || b.Rating > 5))
            return BadRequestProblem("Bands must carry the ratings 1 to 5, each once");
        if (ordered.Any(b => string.IsNullOrWhiteSpace(b.Name)))
            return BadRequestProblem("Every band needs a name");
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].MinScore >= ordered[i - 1].MinScore)
                return BadRequestProblem("Band thresholds must descend with the rating",
                    $"'{ordered[i - 1].Name}' (rating {ordered[i - 1].Rating}) starts at {ordered[i - 1].MinScore} but '{ordered[i].Name}' (rating {ordered[i].Rating}) starts at {ordered[i].MinScore}.");
        }
        if (ordered[^1].MinScore != 0)
            return BadRequestProblem("The lowest band must start at 0", "Otherwise a very low composite would fall into no band at all.");
        if (ordered[0].MinScore > 100)
            return BadRequestProblem("A band cannot start above 100");

        if (p.MaxParameterWeightPercent < 10 || p.MaxParameterWeightPercent > 100)
            return BadRequestProblem("The weight cap must be between 10% and 100%");
        if (!Enum.IsDefined(p.LeaderboardMode))
            return BadRequestProblem("Unrecognised leaderboard mode");
        if (p.LeaderboardTopN < 1 || p.LeaderboardTopN > 100)
            return BadRequestProblem("The leaderboard must show between 1 and 100 people");
        if (p.RecognitionMonthlyBudget < 0)
            return BadRequestProblem("The recognition budget cannot be negative", "Use 0 to switch peer recognition off.");
        if (p.DutyReminderLeadHours < 0 || p.DutyReminderLeadHours > 24 * 14)
            return BadRequestProblem("The duty reminder lead time must be between 0 and 336 hours");
        if (p.LateEntryThresholdDays < 0 || p.LateEntryThresholdDays > 365)
            return BadRequestProblem("The late-entry threshold must be between 0 and 365 days");
        if (p.ActivityAttributionRetentionDays < 30 || p.ActivityAttributionRetentionDays > 3650)
            return BadRequestProblem("Attribution retention must be between 30 days and 10 years",
                "The activity log keeps every event for ever; this decides how long the reader's address and browser stay attached to it.");
        if (!Enum.IsDefined(p.DigestWeekday))
            return BadRequestProblem("Unrecognised digest weekday");
        if (p.DigestHour < 0 || p.DigestHour > 23 || p.SummaryHour < 0 || p.SummaryHour > 23)
            return BadRequestProblem("Send hours must be between 0 and 23");
        if (p.DataProtectionOfficerContact is { Length: > 200 })
            return BadRequestProblem("The data protection contact is too long", "200 characters at most.");

        // Periods, if the tenant defines any: a key and a name each, start before end, no overlap.
        if (p.Periods is { Count: > 0 })
        {
            var periods = p.Periods.OrderBy(x => x.Start).ToList();
            foreach (var period in periods)
            {
                if (string.IsNullOrWhiteSpace(period.Key) || string.IsNullOrWhiteSpace(period.Name))
                    return BadRequestProblem("Every period needs a key and a name", "For example key '2026-T3' and name 'Term 3 2026'.");
                if (period.Key.Trim().Length > 20)
                    return BadRequestProblem($"Period key '{period.Key}' is too long", "20 characters at most; it is stored on every appraisal.");
                if (period.End < period.Start)
                    return BadRequestProblem($"Period '{period.Name}' ends before it starts");
            }
            if (periods.Select(x => x.Key.Trim().ToLowerInvariant()).Distinct().Count() != periods.Count)
                return BadRequestProblem("Period keys must be unique");
            for (var i = 1; i < periods.Count; i++)
            {
                if (periods[i].Start <= periods[i - 1].End)
                    return BadRequestProblem("Periods may not overlap",
                        $"'{periods[i - 1].Name}' runs to {periods[i - 1].End:dd MMM yyyy} but '{periods[i].Name}' starts on {periods[i].Start:dd MMM yyyy}. A date in two periods would be scored twice.");
            }
        }

        return null;
    }
}
