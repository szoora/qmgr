using System.Globalization;
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
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffPolicyController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly ILogger<StaffPolicyController> _logger;

    public StaffPolicyController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        ILogger<StaffPolicyController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _logger = logger;
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
        // Closures are the close / reopen endpoints' to write, with their own permission and reason.
        request.ClosedPeriods = current.ClosedPeriods ?? new();
        request.Periods = (request.Periods ?? new()).OrderBy(p => p.Start).ToList();
        request.Bands = request.Bands.OrderByDescending(b => b.MinScore).ToList();
        request.DataProtectionOfficerContact = string.IsNullOrWhiteSpace(request.DataProtectionOfficerContact) ? null : request.DataProtectionOfficerContact.Trim();

        // Under the same lock the close / reopen endpoints take, re-reading the bookkeeping inside it, so a
        // save that raced a closure keeps the closure.
        await WithPolicyLockAsync(organizationId.Value, async locked =>
        {
            request.LastSummarySentAt = locked.LastSummarySentAt;
            request.ClosedPeriods = locked.ClosedPeriods ?? new();
            await _policy.SaveAsync(organizationId.Value, request);
            return false;
        });

        // Switching automatic credit on must actually credit something: make sure the four automatic-credit
        // parameters exist (a tenant seeded before 2026-09-17 has none, and the award matches by name).
        if (request.SystemAwardsEnabled && !current.SystemAwardsEnabled)
            await StaffParameterDefaults.EnsureSystemSourceAsync(Db, _policy, organizationId.Value, _logger);

        await Activity.RecordAsync(ActivityActions.PolicySaved, "StaffPerformancePolicy", organizationId, null,
            "Staff performance policy saved",
            new
            {
                Before = Snapshot(current),
                After = Snapshot(request)
            });

        return Ok(await _policy.GetAsync(organizationId.Value));
    }

    // ---- Closing a period (plan §7) ---------------------------------------------------------------

    /// <summary>
    /// A period and its state: open or closed, and how many appraisals across the organization are
    /// not yet Signed. Anyone who may open or approve appraisals needs this before closing.
    /// </summary>
    [HttpGet("periods/{key}")]
    [RequirePermission(Permissions.StaffAppraisalsApprove)]
    [ProducesResponseType(typeof(PeriodStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPeriodStatus(string key)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();

        var policy = await _policy.GetAsync(organizationId.Value);
        var period = _policy.FindPeriod(policy, key);
        if (period == null) return NotFoundProblem("Period not found");
        return Ok(await BuildPeriodStatusAsync(organizationId.Value, policy, period));
    }

    /// <summary>
    /// Closes a period. "A period cannot be closed while any appraisal is before Signed unless an
    /// approver overrides with a reason": with unsigned appraisals and no reason this is a 409 naming
    /// the count, and the reason is kept on the closure. The policy blob is written under an advisory
    /// lock keyed on the organization, so a close racing a policy save or a second close cannot drop
    /// either write.
    /// </summary>
    [HttpPost("periods/{key}/close")]
    [RequirePermission(Permissions.StaffAppraisalsApprove)]
    [ProducesResponseType(typeof(PeriodStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClosePeriod(string key, [FromBody] ClosePeriodRequest request)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();
        var orgId = organizationId.Value;

        var initial = await _policy.GetAsync(orgId);
        var period = _policy.FindPeriod(initial, key);
        if (period == null) return NotFoundProblem("Period not found");
        if (DateOnly.FromDateTime(DateTime.UtcNow) < period.Start)
            return BadRequestProblem($"{period.Name} has not started", "A period can only be closed once it has begun.");

        var unsigned = await UnsignedAppraisalsAsync(orgId, period.Key);
        var reason = string.IsNullOrWhiteSpace(request.OverrideReason) ? null : request.OverrideReason.Trim();
        if (unsigned > 0 && (reason == null || reason.Length < 10))
            return ConflictProblem($"{unsigned} appraisal(s) for {period.Name} are not yet signed",
                "Sign them first, or close the period anyway with a reason of at least ten characters; the reason is kept on the closure.");

        var me = CurrentUserId();
        IActionResult? conflict = null;
        await WithPolicyLockAsync(orgId, async policy =>
        {
            if (_policy.ClosureOf(policy, period.Key) != null)
            {
                conflict = ConflictProblem($"{period.Name} is already closed");
                return false;
            }
            policy.ClosedPeriods ??= new();
            policy.ClosedPeriods.Add(new ClosedPeriodDto
            {
                Key = period.Key,
                ClosedAt = DateTime.UtcNow,
                ClosedByUserId = me,
                OverrideReason = unsigned > 0 ? reason : null,
                UnsignedAppraisalsAtClose = unsigned
            });
            return true;
        });
        if (conflict != null) return conflict;

        await Activity.RecordAsync(ActivityActions.PeriodClosed, "PerformancePeriod", null, null,
            unsigned > 0 ? $"{period.Name} closed with {unsigned} appraisal(s) unsigned (override recorded)" : $"{period.Name} closed",
            new { period.Key, Unsigned = unsigned, OverrideReason = unsigned > 0 ? reason : null }, null, orgId, visibility: WelfareVisibility.Standard);

        return Ok(await BuildPeriodStatusAsync(orgId, await _policy.GetAsync(orgId), period));
    }

    [HttpPost("periods/{key}/reopen")]
    [RequirePermission(Permissions.StaffAppraisalsApprove)]
    [ProducesResponseType(typeof(PeriodStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReopenPeriod(string key, [FromBody] ReopenPeriodRequest request)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();
        var orgId = organizationId.Value;

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5)
            return BadRequestProblem("Say why the period is being reopened");

        var initial = await _policy.GetAsync(orgId);
        var period = _policy.FindPeriod(initial, key);
        if (period == null) return NotFoundProblem("Period not found");

        IActionResult? problem = null;
        await WithPolicyLockAsync(orgId, policy =>
        {
            var closure = _policy.ClosureOf(policy, period.Key);
            if (closure == null)
            {
                problem = BadRequestProblem($"{period.Name} is not closed");
                return Task.FromResult(false);
            }
            policy.ClosedPeriods.Remove(closure);
            return Task.FromResult(true);
        });
        if (problem != null) return problem;

        await Activity.RecordAsync(ActivityActions.PeriodReopened, "PerformancePeriod", null, null,
            $"{period.Name} reopened: {request.Reason.Trim()}", new { period.Key, Reason = request.Reason.Trim() }, null, orgId);

        return Ok(await BuildPeriodStatusAsync(orgId, await _policy.GetAsync(orgId), period));
    }

    private Task<int> UnsignedAppraisalsAsync(Guid organizationId, string periodKey)
        => Db.StaffAppraisals.IgnoreQueryFilters()
            .CountAsync(a => a.OrganizationId == organizationId && a.IsActive && a.PeriodKey == periodKey && a.Stage != AppraisalStage.Signed);

    private async Task<PeriodStatusDto> BuildPeriodStatusAsync(Guid organizationId, StaffPerformancePolicyDto policy, PerformancePeriodDto period)
    {
        var closure = _policy.ClosureOf(policy, period.Key);
        if (closure != null)
        {
            var names = await BuildNamesAsync(new Guid?[] { closure.ClosedByUserId });
            closure = closure with { ClosedByName = names[closure.ClosedByUserId] };
        }
        return new PeriodStatusDto
        {
            Period = period,
            IsClosed = closure != null,
            Closure = closure,
            AppraisalCount = await Db.StaffAppraisals.IgnoreQueryFilters().CountAsync(a => a.OrganizationId == organizationId && a.IsActive && a.PeriodKey == period.Key),
            UnsignedAppraisals = await UnsignedAppraisalsAsync(organizationId, period.Key)
        };
    }

    /// <summary>Read-modify-write of the policy blob under pg_advisory_xact_lock on the organization. The mutation returns false to write nothing.</summary>
    private async Task WithPolicyLockAsync(Guid organizationId, Func<StaffPerformancePolicyDto, Task<bool>> mutate)
    {
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"staff-policy:{organizationId}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");
            var policy = await _policy.GetAsync(organizationId);
            if (await mutate(policy))
                await _policy.SaveAsync(organizationId, policy);
            await tx.CommitAsync();
        });
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

        // 50 is the MET ceiling the plan is built on ("no measure above about half the weight"), so a tenant
        // may tighten it but not lift it (found by the plan audit, 2026-09-17: it could be raised to 100).
        if (p.MaxParameterWeightPercent < 10 || p.MaxParameterWeightPercent > 50)
            return BadRequestProblem("The weight cap must be between 10% and 50%", "No single parameter may be more than half of the composite; composites above that were the least stable in the MET study.");
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
                        string.Create(CultureInfo.InvariantCulture, $"'{periods[i - 1].Name}' runs to {periods[i - 1].End:dd MMM yyyy} but '{periods[i].Name}' starts on {periods[i].Start:dd MMM yyyy}. A date in two periods would be scored twice."));
            }
        }

        return ValidateDutyRota(p);
    }

    /// <summary>The duty rota plan's policy additions (§4, §6.1, §7.2, §8.1).</summary>
    private IActionResult? ValidateDutyRota(StaffPerformancePolicyDto p)
    {
        foreach (var ladder in p.ReminderLadders ?? new())
        {
            if (!Enum.IsDefined(ladder.Subject)) return BadRequestProblem("Unrecognised reminder ladder");
            if (ladder.Stages.Count > 8) return BadRequestProblem("A reminder ladder has at most eight stages");
            if (ladder.Stages.Select(s => s.Stage).Distinct().Count() != ladder.Stages.Count || ladder.Stages.Any(s => s.Stage < 1))
                return BadRequestProblem("Reminder stages must be numbered from 1, each once");
            if ((p.ReminderLadders ?? new()).Count(l => l.Subject == ladder.Subject) > 1)
                return BadRequestProblem("Each reminder ladder may be defined once");
            foreach (var stage in ladder.Stages)
            {
                if (Math.Abs(stage.OffsetMinutes) > 60 * 24 * 31)
                    return BadRequestProblem("A reminder stage may be at most 31 days from its start or due time");
                // The sweep looks MaxLeadDays ahead for a start; a stage earlier than a fortnight would be accepted
                // and then never sent, which is worse than refusing it.
                if (stage.OffsetMinutes < -60 * 24 * 14)
                    return BadRequestProblem("A reminder stage may be at most 14 days before a start");
                if (stage.AtLocalHour is < 0 or > 23)
                    return BadRequestProblem("A stage's send hour must be between 0 and 23");
                if (stage.Channels == ReminderChannels.None)
                    return BadRequestProblem("Every reminder stage needs a channel", "Digest, bell, email or SMS.");
            }
            // Stage numbers are the escalation order: a later stage must not fall due before an earlier one, or
            // collapse would skip the earlier stage for ever.
            var ordered = ladder.Stages.OrderBy(s => s.Stage).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].OffsetMinutes < ordered[i - 1].OffsetMinutes)
                    return BadRequestProblem("Reminder stages must fall due in order", $"Stage {ordered[i].Stage} is due before stage {ordered[i - 1].Stage}.");
        }

        var quiet = p.QuietHours ?? new QuietHoursDto();
        if (quiet.StartHour is < 0 or > 23 || quiet.EndHour is < 0 or > 23 || quiet.MorningHour is < 0 or > 23)
            return BadRequestProblem("Quiet hours must be whole hours between 0 and 23");

        if (p.LessonReminderMinutes is < 0 or > 120)
            return BadRequestProblem("The lesson reminder must be between 0 and 120 minutes before the lesson");
        if (!TimeOnly.TryParseExact(p.MyDayLocalTime ?? "", "HH:mm", out _))
            return BadRequestProblem("The My Day digest time must be a 24-hour time such as 06:30");
        if (p.LessonRecoveryDeadlineDays is < 1 or > 90)
            return BadRequestProblem("The lesson recovery deadline must be between 1 and 90 days");
        if (p.UnrecordedLessonWindowDays is < 1 or > 30)
            return BadRequestProblem("The unrecorded-lesson window must be between 1 and 30 days");

        var defaults = p.DutyReportDefaults ?? new DutyReportDefaultsDto();
        if (!TimeOnly.TryParseExact(defaults.DueLocalTime ?? "", "HH:mm", out _))
            return BadRequestProblem("The report due time must be a 24-hour time such as 18:00");
        if (defaults.MaxRotaSlotsPerTerm is < 0 or > 200)
            return BadRequestProblem("The rota fairness limit must be between 0 and 200 slots a term");
        if (!Enum.IsDefined(defaults.DayLongCadence) || !Enum.IsDefined(defaults.WeekLongCadence) || !Enum.IsDefined(defaults.MonthLongCadence))
            return BadRequestProblem("Unrecognised report cadence");

        var template = p.DutyReportTemplate ?? new();
        if (template.Count > 20) return BadRequestProblem("A duty report has at most 20 sections");
        foreach (var section in template)
        {
            if (string.IsNullOrWhiteSpace(section.Key) || string.IsNullOrWhiteSpace(section.Title))
                return BadRequestProblem("Every report section needs a key and a title");
            if (section.Kind == DutyReportSectionKind.Choice && section.Choices.Count(c => !string.IsNullOrWhiteSpace(c)) < 2)
                return BadRequestProblem($"The choice section '{section.Title}' needs at least two choices");
        }
        if (template.Select(s => s.Key.Trim().ToLowerInvariant()).Distinct().Count() != template.Count)
            return BadRequestProblem("Report section keys must be unique", "A submitted report stores its answers by key.");

        var norms = p.TeachingLoadNorms ?? new TeachingLoadNormsDto();
        if (norms.MinLessonsPerWeek < 0 || norms.MaxLessonsPerWeek > 80 || norms.MinLessonsPerWeek > norms.MaxLessonsPerWeek)
            return BadRequestProblem("The weekly lesson band must run from a minimum to a higher maximum, at most 80");
        if (norms.MinLessonsPerWeekMixedLevel < 0 || norms.MinLessonsPerWeekMixedLevel > norms.MaxLessonsPerWeek)
            return BadRequestProblem("The mixed-level minimum must sit below the weekly maximum");
        if (norms.MaxPeriodsPerDay is < 1 or > 16 || norms.MaxConsecutivePeriods is < 1 or > 16)
            return BadRequestProblem("Daily and consecutive period limits must be between 1 and 16");

        return null;
    }
}
