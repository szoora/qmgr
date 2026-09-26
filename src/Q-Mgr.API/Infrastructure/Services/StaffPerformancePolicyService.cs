using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// The one reader and writer of <c>Organization.Settings["StaffPerformance"]</c>: periods, bands,
/// weight cap, leaderboard switch, recognition budget, lead times, retention, the DPO contact. The
/// same shape as <c>IDocumentShareService.ReadPolicy</c>; never a second JSON parser.
///
/// Periods: when the tenant has defined none, Ugandan terms are derived — T1 Jan–Apr, T2 May–Aug,
/// T3 Sep–Dec — so every date has a period and a key ("2026-T3"; the annual roll-up is "2026").
/// </summary>
public interface IStaffPerformancePolicyService
{
    StaffPerformancePolicyDto ReadPolicy(string? organizationSettingsJson);
    string WritePolicy(string? organizationSettingsJson, StaffPerformancePolicyDto policy);

    Task<StaffPerformancePolicyDto> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task SaveAsync(Guid organizationId, StaffPerformancePolicyDto policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps the monthly summary as sent, re-reading the policy under the organization-settings lock.
    /// The sweep used to save back the WHOLE policy it had read at the start of its run, so an edit or a
    /// period closure made meanwhile was overwritten by the job's stale copy.
    /// </summary>
    Task MarkSummarySentAsync(Guid organizationId, DateTime sentAtUtc, CancellationToken cancellationToken = default);

    /// <summary>The period containing <paramref name="date"/>, from the policy or derived.</summary>
    PerformancePeriodDto PeriodFor(StaffPerformancePolicyDto policy, DateOnly date);

    /// <summary>The period with this key ("2026-T2", or "2026" for the annual roll-up), or null.</summary>
    PerformancePeriodDto? FindPeriod(StaffPerformancePolicyDto policy, string? key);

    /// <summary>Every period that touches the given year, defined or derived, in date order.</summary>
    IReadOnlyList<PerformancePeriodDto> PeriodsForYear(StaffPerformancePolicyDto policy, int year);

    /// <summary>
    /// The closure that covers <paramref name="whenUtc"/>, or null when it is open: the term containing the
    /// date, or that year's annual roll-up, whichever was closed. The one reader of ClosedPeriods.
    /// </summary>
    ClosedPeriodDto? ClosureFor(StaffPerformancePolicyDto policy, DateTime whenUtc);

    /// <summary>The closure of the period with this key, or null.</summary>
    ClosedPeriodDto? ClosureOf(StaffPerformancePolicyDto policy, string? key);

    /// <summary>The band a composite falls in, highest MinScore first.</summary>
    ScoreBandDto BandFor(StaffPerformancePolicyDto policy, decimal composite);

    /// <summary>
    /// Which staff group somebody is in, from their ROLE row (Role.StaffGroup), falling back to the
    /// tenant's first active group. Was roleCode == "support-staff" ? Support : Teaching until
    /// 2026-09-22 — see StaffGroups for why that made the bursar teaching staff.
    /// </summary>
    string? GroupFor(string? roleStaffGroup, StaffPerformancePolicyDto? policy = null);

    /// <summary>The Ugandan default set of parameters, seeded per tenant on first use.</summary>
    IReadOnlyList<SavePerformanceParameterRequest> DefaultParameters();

    /// <summary>Default parameter name → the default parameter it offsets. Linked by id at seed time.</summary>
    IReadOnlyList<(string From, string To)> DefaultParameterOffsets();

    /// <summary>
    /// The reminder ladder for one subject (duty rota plan §8.1): the tenant's own when it defined one with
    /// stages, otherwise the plan's default. Stages ascending. The ONE reader of ReminderLadders.
    /// </summary>
    ReminderLadderDto LadderFor(StaffPerformancePolicyDto policy, ReminderSubject subject);

    /// <summary>The plan's default ladders (§4.2, §4.4, §7.2), for the editor's "restore defaults".</summary>
    IReadOnlyList<ReminderLadderDto> DefaultLadders(StaffPerformancePolicyDto policy);

    /// <summary>The tenant's duty report template, or the default sections (plan §15 decision 1).</summary>
    IReadOnlyList<DutyReportSectionDto> ReportTemplate(StaffPerformancePolicyDto policy);

    /// <summary>The tenant's minutes template, or the default sections. The ONE reader of MinutesTemplate.</summary>
    IReadOnlyList<DutyReportSectionDto> MinutesTemplate(StaffPerformancePolicyDto policy);
    /// <summary>The school's lesson-planning settings, clamped (2026-09-26). The ONE reader of TeachingPlans.</summary>
    TeachingPlanSettingsDto PlanSettings(StaffPerformancePolicyDto policy);

    /// <summary>The Uganda lower-secondary subject set a school sees before configuring anything (plan §5.2).</summary>
    IReadOnlyList<SaveSubjectRequest> DefaultSubjects();
}

public class StaffPerformancePolicyService : IStaffPerformancePolicyService
{
    private const string PolicyKey = "StaffPerformance";
    private readonly QMgrDbContext _db;

    public StaffPerformancePolicyService(QMgrDbContext db)
    {
        _db = db;
    }

    public StaffPerformancePolicyDto ReadPolicy(string? organizationSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(organizationSettingsJson)) return new StaffPerformancePolicyDto { StaffGroups = DefaultStaffGroups(), EmploymentTypes = DefaultEmploymentTypes() };
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson);
            if (root != null && root.TryGetValue(PolicyKey, out var element))
            {
                var policy = JsonSerializer.Deserialize<StaffPerformancePolicyDto>(element.GetRawText()) ?? new StaffPerformancePolicyDto();
                if (policy.Bands == null || policy.Bands.Count == 0) policy.Bands = new StaffPerformancePolicyDto().Bands;
                // The MET ceiling is 50; a blob written before the editor enforced it must not lift it.
                policy.MaxParameterWeightPercent = Math.Clamp(policy.MaxParameterWeightPercent, 10, 50);
                policy.ClosedPeriods ??= new();
                // Duty rota plan additions: a blob written before 2026-09-17 has none of these.
                policy.ReminderLadders ??= new();
                policy.QuietHours ??= new QuietHoursDto();
                policy.DutyReportTemplate ??= new();
                policy.DutyReportDefaults ??= new DutyReportDefaultsDto();
                policy.TeachingLoadNorms ??= new TeachingLoadNormsDto();
                // Lesson plans (2026-09-26): a blob written before carries none.
                policy.TeachingPlans ??= new TeachingPlanSettingsDto();
                if (policy.LessonReminderMinutes is < 0 or > 120) policy.LessonReminderMinutes = 10;
                if (string.IsNullOrWhiteSpace(policy.MyDayLocalTime)) policy.MyDayLocalTime = "06:30";
                // Seeded on first read, the way StaffParameterDefaults seeds parameters: a blob
                // written before 2026-09-22 carries no groups, and an empty list would leave every
                // role resolving to nothing.
                if (policy.StaffGroups == null || policy.StaffGroups.Count == 0)
                    policy.StaffGroups = DefaultStaffGroups();
                // Employment types (2026-09-23), seeded the same way: a blob written before then carries none, and the
                // six names are exactly what the migration wrote onto people, so nothing stored reads differently.
                if (policy.EmploymentTypes == null || policy.EmploymentTypes.Count == 0)
                    policy.EmploymentTypes = DefaultEmploymentTypes();
                // The three scoring dials are clamped here as well as validated in the editor, so a
                // blob written before they existed — or edited by hand — can never produce nonsense.
                policy.LateCreditFraction = Math.Clamp(policy.LateCreditFraction, 0m, 1m);
                policy.DefaultEntriesPerPeriod = Math.Max(1, policy.DefaultEntriesPerPeriod);
                policy.NeutralScore = Math.Clamp(policy.NeutralScore, 0m, 100m);
                return policy;
            }
        }
        catch (JsonException) { /* malformed settings blob — fall back to defaults */ }
        return new StaffPerformancePolicyDto { StaffGroups = DefaultStaffGroups(), EmploymentTypes = DefaultEmploymentTypes() };
    }

    public string WritePolicy(string? organizationSettingsJson, StaffPerformancePolicyDto policy)
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

    public async Task<StaffPerformancePolicyDto> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => o.Settings)
            .FirstOrDefaultAsync(cancellationToken);
        return ReadPolicy(settings);
    }

    public async Task SaveAsync(Guid organizationId, StaffPerformancePolicyDto policy, CancellationToken cancellationToken = default)
    {
        // Through the one writer of Organization.Settings, so a save here can never drop a key
        // another writer set at the same moment. Joins the editor's own transaction when it has one.
        var found = await OrganizationSettingsLock.MutateAsync(_db, organizationId, org =>
        {
            org.Settings = WritePolicy(org.Settings, policy);
            return true;
        }, cancellationToken);
        if (!found) throw new InvalidOperationException("Organization not found");
    }

    public Task MarkSummarySentAsync(Guid organizationId, DateTime sentAtUtc, CancellationToken cancellationToken = default)
        => OrganizationSettingsLock.MutateAsync(_db, organizationId, org =>
        {
            var policy = ReadPolicy(org.Settings);
            policy.LastSummarySentAt = sentAtUtc;
            org.Settings = WritePolicy(org.Settings, policy);
            return true;
        }, cancellationToken);

    public PerformancePeriodDto PeriodFor(StaffPerformancePolicyDto policy, DateOnly date)
    {
        var defined = policy.Periods?.FirstOrDefault(p => p.Start <= date && date <= p.End);
        if (defined != null) return defined;
        return Derived(date.Year).First(p => p.Start <= date && date <= p.End);
    }

    public PerformancePeriodDto? FindPeriod(StaffPerformancePolicyDto policy, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();

        var defined = policy.Periods?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (defined != null) return defined;

        // Annual roll-up: "2026".
        if (key.Length == 4 && int.TryParse(key, out var year))
            return new PerformancePeriodDto { Key = key, Name = $"{year} (annual)", Start = new DateOnly(year, 1, 1), End = new DateOnly(year, 12, 31) };

        // Derived term: "2026-T2".
        var dash = key.IndexOf("-T", StringComparison.OrdinalIgnoreCase);
        if (dash > 0 && int.TryParse(key[..dash], out var y) && int.TryParse(key[(dash + 2)..], out var term))
            return Derived(y).FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        return null;
    }

    public IReadOnlyList<PerformancePeriodDto> PeriodsForYear(StaffPerformancePolicyDto policy, int year)
    {
        var defined = policy.Periods?
            .Where(p => p.Start.Year == year || p.End.Year == year)
            .OrderBy(p => p.Start)
            .ToList();
        if (defined != null && defined.Count > 0) return defined;
        return Derived(year);
    }

    private static List<PerformancePeriodDto> Derived(int year) => new()
    {
        new() { Key = $"{year}-T1", Name = $"Term 1 {year}", Start = new DateOnly(year, 1, 1), End = new DateOnly(year, 4, 30) },
        new() { Key = $"{year}-T2", Name = $"Term 2 {year}", Start = new DateOnly(year, 5, 1), End = new DateOnly(year, 8, 31) },
        new() { Key = $"{year}-T3", Name = $"Term 3 {year}", Start = new DateOnly(year, 9, 1), End = new DateOnly(year, 12, 31) },
    };

    public ClosedPeriodDto? ClosureFor(StaffPerformancePolicyDto policy, DateTime whenUtc)
    {
        if (policy.ClosedPeriods == null || policy.ClosedPeriods.Count == 0) return null;
        var date = DateOnly.FromDateTime(whenUtc);
        return ClosureOf(policy, PeriodFor(policy, date).Key) ?? ClosureOf(policy, date.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public ClosedPeriodDto? ClosureOf(StaffPerformancePolicyDto policy, string? key)
        => string.IsNullOrWhiteSpace(key) ? null
            : policy.ClosedPeriods?.FirstOrDefault(c => string.Equals(c.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    public ScoreBandDto BandFor(StaffPerformancePolicyDto policy, decimal composite)
    {
        var bands = (policy.Bands ?? new StaffPerformancePolicyDto().Bands).OrderByDescending(b => b.MinScore).ToList();
        return bands.FirstOrDefault(b => composite >= b.MinScore) ?? bands.Last();
    }

    public string? GroupFor(string? roleStaffGroup, StaffPerformancePolicyDto? policy = null)
    {
        if (!string.IsNullOrWhiteSpace(roleStaffGroup)) return roleStaffGroup.Trim();

        // A role with nothing set falls back to the tenant's FIRST active group rather than to a
        // hard-coded "teaching": a school whose first group is Administration should not have its
        // unassigned roles silently counted as teachers. Absent a policy, the seeded default.
        var first = policy?.StaffGroups?.Where(g => g.IsActive).OrderBy(g => g.SortOrder)
                                        .Select(g => g.Name).FirstOrDefault();
        return first ?? StaffGroups.Teaching;
    }

    /// <summary>
    /// The two groups every tenant starts with, seeded on first policy read the way
    /// <c>StaffParameterDefaults</c> seeds parameters. They are exactly what the old enum resolved
    /// to, so seeding them moves no score.
    /// </summary>
    /// <summary>The six names <c>StaffEmploymentType</c> had, in its order (see <c>EmploymentTypes</c>).</summary>
    public static List<VocabularyItemDto> DefaultEmploymentTypes()
        => QMgr.Domain.Enums.EmploymentTypes.Defaults.Select((name, i) => new VocabularyItemDto { Name = name, IsActive = true, SortOrder = i }).ToList();

    public static List<VocabularyItemDto> DefaultStaffGroups() => new()
    {
        new VocabularyItemDto { Name = StaffGroups.Teaching, IsActive = true, SortOrder = 0 },
        new VocabularyItemDto { Name = StaffGroups.Support, IsActive = true, SortOrder = 1 }
    };

    public ReminderLadderDto LadderFor(StaffPerformancePolicyDto policy, ReminderSubject subject)
    {
        var own = policy.ReminderLadders?.FirstOrDefault(l => l.Subject == subject && l.Stages is { Count: > 0 });
        var ladder = own ?? DefaultLadders(policy).First(l => l.Subject == subject);
        return ladder with { Stages = ladder.Stages.OrderBy(s => s.Stage).ToList() };
    }

    /// <summary>One copy, in Shared, so the policy editor's "restore defaults" shows exactly what the sweep runs.</summary>
    public IReadOnlyList<ReminderLadderDto> DefaultLadders(StaffPerformancePolicyDto policy) => ReminderLadderDefaults.For(policy);

    public IReadOnlyList<DutyReportSectionDto> ReportTemplate(StaffPerformancePolicyDto policy)
        => policy.DutyReportTemplate is { Count: > 0 } own ? own : DutyReportTemplateDefaults.Sections;

    public IReadOnlyList<DutyReportSectionDto> MinutesTemplate(StaffPerformancePolicyDto policy)
        => policy.MinutesTemplate is { Count: > 0 } own ? own : MinutesTemplateDefaults.Sections;

    public TeachingPlanSettingsDto PlanSettings(StaffPerformancePolicyDto policy)
        => TeachingPlanRules.Clamp(policy.TeachingPlans ?? new TeachingPlanSettingsDto());

    public IReadOnlyList<SaveSubjectRequest> DefaultSubjects() => QMgr.API.Application.Services.SubjectDefaults.Catalogue;

    public IReadOnlyList<(string From, string To)> DefaultParameterOffsets() => new List<(string, string)>
    {
        ("Lesson Recovery", "Lesson Attendance")
    };

    public IReadOnlyList<SavePerformanceParameterRequest> DefaultParameters() => new List<SavePerformanceParameterRequest>
    {
        new() { Name = "Lesson Attendance", Kind = ParameterKind.Attendance, AppliesToGroup = StaffGroups.Teaching, DefaultPoints = 1, MaxPointsPerEntry = 1, Weight = 3, Purpose = "Collected from the lesson attendance register for cover planning and as appraisal evidence.", Color = "#7a2847", SortOrder = 1 },
        new() { Name = "Lesson Recovery", Kind = ParameterKind.Contribution, AppliesToGroup = StaffGroups.Teaching, DefaultPoints = 1, MaxPointsPerEntry = 1, Weight = 0, Purpose = "A lesson missed and later recovered, per the Lesson Recovery Schedule. Offsets a missed lesson; not scored on its own.", Color = "#8c2f52", SortOrder = 2 },
        new() { Name = "Lesson Observation", Kind = ParameterKind.Observation, AppliesToGroup = StaffGroups.Teaching, RatingScale = 4, Rubric = new() { "Poor — immediate remedial action", "Fair — meets some expectations", "Good — meets expectations", "Very Good — exceeds expectations" }, Weight = 3, DefaultVisibility = WelfareVisibility.Confidential, Purpose = "At least one observed lesson per term, with a meeting before and a feedback session after. Developmental first; appraisal evidence second.", Color = "#5a9c92", SortOrder = 3 },
        new() { Name = "Exam Supervision", Kind = ParameterKind.Duty, AppliesToGroup = StaffGroups.Teaching, DefaultPoints = 2, MaxPointsPerEntry = 2, Weight = 2, Purpose = "Invigilation duties carried out as rostered.", Color = "#c99a5b", SortOrder = 4 },
        new() { Name = "Prep Supervision", Kind = ParameterKind.Duty, AppliesToGroup = StaffGroups.Teaching, DefaultPoints = 1, MaxPointsPerEntry = 1, Weight = 1, Purpose = "Evening or weekend prep supervision as rostered.", Color = "#c2624f", SortOrder = 5 },
        new() { Name = "Meeting Attendance", Kind = ParameterKind.Attendance, DefaultPoints = 1, MaxPointsPerEntry = 1, Weight = 1, Purpose = "Attendance at staff, departmental and committee meetings, from the register taken by the named recorder.", Color = "#3f8a80", SortOrder = 6 },
        new() { Name = "Co-curricular Activity", Kind = ParameterKind.Contribution, DefaultPoints = 3, MaxPointsPerEntry = 5, MaxPointsPerPeriod = 30, Weight = 2, Purpose = "Clubs, sports, music, drama and other activities run or supported.", Color = "#a8783a", SortOrder = 7 },
        new() { Name = "Records & Schemes of Work", Kind = ParameterKind.Contribution, AppliesToGroup = StaffGroups.Teaching, DefaultPoints = 2, MaxPointsPerEntry = 5, MaxPointsPerPeriod = 20, Weight = 1, Purpose = "Schemes of work, lesson plans and mark books submitted on time and to standard.", Color = "#6e2340", SortOrder = 8 },
        new() { Name = "Recognition", Kind = ParameterKind.Recognition, DefaultPoints = 1, MaxPointsPerEntry = 1, MaxPointsPerPeriod = 20, Weight = 1, Purpose = "Recognition from a colleague, within the monthly budget. Informational and unexpected by design.", Color = "#d1a35e", SortOrder = 9 },
        new() { Name = "Professional Development", Kind = ParameterKind.Contribution, DefaultPoints = 2, MaxPointsPerEntry = 5, MaxPointsPerPeriod = 20, Weight = 1, Purpose = "Training, mentoring, subject symposiums and professional learning community work.", Color = "#5a9c92", SortOrder = 10 },
        new() { Name = "Conduct", Kind = ParameterKind.Conduct, DefaultPoints = -2, MaxPointsPerEntry = 10, MaxPointsPerPeriod = 30, Weight = 1, DefaultVisibility = WelfareVisibility.Confidential, Purpose = "A conduct matter, recorded with the subject's right of reply.", Color = "#a3302a", SortOrder = 11 },
        new() { Name = "Wellbeing", Kind = ParameterKind.Wellbeing, DefaultPoints = null, MaxPointsPerEntry = 0, Weight = 0, DefaultVisibility = WelfareVisibility.Confidential, Purpose = "A welfare-of-staff matter — a bereavement, a workload concern, a health matter. Recorded to support, never scored.", Color = "#8a7a81", SortOrder = 12 },
        // Automatic credit (plan §6 item 5, decision 4). Inert until the policy switches SystemAwardsEnabled on,
        // and refused as a manual entry so nobody mistakes an automatic credit for a colleague's judgement.
        new() { Name = StaffSystemAwards.WelfareRecordFiled, Kind = ParameterKind.Contribution, DefaultPoints = 1, MaxPointsPerEntry = 1, MaxPointsPerPeriod = 10, Weight = 1, IsSystemSource = true, Purpose = "Credited automatically when the staff member finalises a student welfare record. Pastoral work counts.", Color = "#5a9c92", SortOrder = 20 },
        new() { Name = StaffSystemAwards.CustomerServed, Kind = ParameterKind.Contribution, DefaultPoints = 1, MaxPointsPerEntry = 1, MaxPointsPerPeriod = 20, Weight = 1, IsSystemSource = true, Purpose = "Credited automatically when the staff member completes service for a queue ticket.", Color = "#3f8a80", SortOrder = 21 },
        new() { Name = StaffSystemAwards.VisitorHosted, Kind = ParameterKind.Contribution, DefaultPoints = 1, MaxPointsPerEntry = 1, MaxPointsPerPeriod = 10, Weight = 1, IsSystemSource = true, Purpose = "Credited automatically when a visitor the staff member hosts is checked in.", Color = "#a8783a", SortOrder = 22 },
        new() { Name = StaffSystemAwards.PositiveFeedback, Kind = ParameterKind.Contribution, DefaultPoints = 1, MaxPointsPerEntry = 1, MaxPointsPerPeriod = 20, Weight = 1, IsSystemSource = true, Purpose = "Credited automatically when a customer the staff member served rates the service 4 or 5.", Color = "#d1a35e", SortOrder = 23 },
        // Lesson plans (2026-09-26). Its own automatic parameter rather than points on "Records & Schemes of Work", which
        // stays a head of department's judgement: an automatic credit must never be mistaken for one.
        new() { Name = StaffSystemAwards.PlanApprovedOnTime, Kind = ParameterKind.Contribution, AppliesToGroup = StaffGroups.Teaching, DefaultPoints = 1, MaxPointsPerEntry = 1, MaxPointsPerPeriod = 20, Weight = 1, IsSystemSource = true, Purpose = "Credited automatically when a lesson plan or scheme of work submitted by the school's deadline is approved.", Color = "#7a5a8c", SortOrder = 24 },
    };
}
