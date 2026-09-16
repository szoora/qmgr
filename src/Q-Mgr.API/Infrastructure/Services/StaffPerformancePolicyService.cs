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

    /// <summary>The period containing <paramref name="date"/>, from the policy or derived.</summary>
    PerformancePeriodDto PeriodFor(StaffPerformancePolicyDto policy, DateOnly date);

    /// <summary>The period with this key ("2026-T2", or "2026" for the annual roll-up), or null.</summary>
    PerformancePeriodDto? FindPeriod(StaffPerformancePolicyDto policy, string? key);

    /// <summary>Every period that touches the given year, defined or derived, in date order.</summary>
    IReadOnlyList<PerformancePeriodDto> PeriodsForYear(StaffPerformancePolicyDto policy, int year);

    /// <summary>The band a composite falls in, highest MinScore first.</summary>
    ScoreBandDto BandFor(StaffPerformancePolicyDto policy, decimal composite);

    /// <summary>Teaching or support, from the role code. Support staff hold the support-staff role; everyone else teaches.</summary>
    StaffGroup GroupFor(string? roleCode);

    /// <summary>The Ugandan default set of parameters, seeded per tenant on first use.</summary>
    IReadOnlyList<SavePerformanceParameterRequest> DefaultParameters();
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
        if (string.IsNullOrWhiteSpace(organizationSettingsJson)) return new StaffPerformancePolicyDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson);
            if (root != null && root.TryGetValue(PolicyKey, out var element))
            {
                var policy = JsonSerializer.Deserialize<StaffPerformancePolicyDto>(element.GetRawText()) ?? new StaffPerformancePolicyDto();
                if (policy.Bands == null || policy.Bands.Count == 0) policy.Bands = new StaffPerformancePolicyDto().Bands;
                return policy;
            }
        }
        catch (JsonException) { /* malformed settings blob — fall back to defaults */ }
        return new StaffPerformancePolicyDto();
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
        var org = await _db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken)
                  ?? throw new InvalidOperationException("Organization not found");
        org.Settings = WritePolicy(org.Settings, policy);
        await _db.SaveChangesAsync(cancellationToken);
    }

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

    public ScoreBandDto BandFor(StaffPerformancePolicyDto policy, decimal composite)
    {
        var bands = (policy.Bands ?? new StaffPerformancePolicyDto().Bands).OrderByDescending(b => b.MinScore).ToList();
        return bands.FirstOrDefault(b => composite >= b.MinScore) ?? bands.Last();
    }

    public StaffGroup GroupFor(string? roleCode)
        => RoleCodes.IsSupportStaff(roleCode) ? StaffGroup.SupportStaff : StaffGroup.TeachingStaff;

    public IReadOnlyList<SavePerformanceParameterRequest> DefaultParameters() => new List<SavePerformanceParameterRequest>
    {
        new() { Name = "Lesson Attendance", Kind = ParameterKind.Attendance, AppliesTo = StaffGroup.TeachingStaff, DefaultPoints = 1, MaxPointsPerEntry = 1, Weight = 3, Purpose = "Collected from the lesson attendance register for cover planning and as appraisal evidence.", Color = "#7a2847", SortOrder = 1 },
        new() { Name = "Lesson Recovery", Kind = ParameterKind.Contribution, AppliesTo = StaffGroup.TeachingStaff, DefaultPoints = 1, MaxPointsPerEntry = 1, Weight = 0, Purpose = "A lesson missed and later recovered, per the Lesson Recovery Schedule. Offsets a missed lesson; not scored on its own.", Color = "#8c2f52", SortOrder = 2 },
        new() { Name = "Lesson Observation", Kind = ParameterKind.Observation, AppliesTo = StaffGroup.TeachingStaff, RatingScale = 4, Rubric = new() { "Poor — immediate remedial action", "Fair — meets some expectations", "Good — meets expectations", "Very Good — exceeds expectations" }, Weight = 3, DefaultVisibility = WelfareVisibility.Confidential, Purpose = "At least one observed lesson per term, with a meeting before and a feedback session after. Developmental first; appraisal evidence second.", Color = "#5a9c92", SortOrder = 3 },
        new() { Name = "Exam Supervision", Kind = ParameterKind.Duty, AppliesTo = StaffGroup.TeachingStaff, DefaultPoints = 2, MaxPointsPerEntry = 2, Weight = 2, Purpose = "Invigilation duties carried out as rostered.", Color = "#c99a5b", SortOrder = 4 },
        new() { Name = "Prep Supervision", Kind = ParameterKind.Duty, AppliesTo = StaffGroup.TeachingStaff, DefaultPoints = 1, MaxPointsPerEntry = 1, Weight = 1, Purpose = "Evening or weekend prep supervision as rostered.", Color = "#c2624f", SortOrder = 5 },
        new() { Name = "Meeting Attendance", Kind = ParameterKind.Attendance, AppliesTo = StaffGroup.AllStaff, DefaultPoints = 1, MaxPointsPerEntry = 1, Weight = 1, Purpose = "Attendance at staff, departmental and committee meetings, from the register taken by the named recorder.", Color = "#3f8a80", SortOrder = 6 },
        new() { Name = "Co-curricular Activity", Kind = ParameterKind.Contribution, AppliesTo = StaffGroup.AllStaff, DefaultPoints = 3, MaxPointsPerEntry = 5, MaxPointsPerPeriod = 30, Weight = 2, Purpose = "Clubs, sports, music, drama and other activities run or supported.", Color = "#a8783a", SortOrder = 7 },
        new() { Name = "Records & Schemes of Work", Kind = ParameterKind.Contribution, AppliesTo = StaffGroup.TeachingStaff, DefaultPoints = 2, MaxPointsPerEntry = 5, MaxPointsPerPeriod = 20, Weight = 1, Purpose = "Schemes of work, lesson plans and mark books submitted on time and to standard.", Color = "#6e2340", SortOrder = 8 },
        new() { Name = "Recognition", Kind = ParameterKind.Recognition, AppliesTo = StaffGroup.AllStaff, DefaultPoints = 1, MaxPointsPerEntry = 1, MaxPointsPerPeriod = 20, Weight = 1, Purpose = "Recognition from a colleague, within the monthly budget. Informational and unexpected by design.", Color = "#d1a35e", SortOrder = 9 },
        new() { Name = "Professional Development", Kind = ParameterKind.Contribution, AppliesTo = StaffGroup.AllStaff, DefaultPoints = 2, MaxPointsPerEntry = 5, MaxPointsPerPeriod = 20, Weight = 1, Purpose = "Training, mentoring, subject symposiums and professional learning community work.", Color = "#5a9c92", SortOrder = 10 },
        new() { Name = "Conduct", Kind = ParameterKind.Conduct, AppliesTo = StaffGroup.AllStaff, DefaultPoints = -2, MaxPointsPerEntry = 10, MaxPointsPerPeriod = 30, Weight = 1, DefaultVisibility = WelfareVisibility.Confidential, Purpose = "A conduct matter, recorded with the subject's right of reply.", Color = "#a3302a", SortOrder = 11 },
        new() { Name = "Wellbeing", Kind = ParameterKind.Wellbeing, AppliesTo = StaffGroup.AllStaff, DefaultPoints = null, MaxPointsPerEntry = 0, Weight = 0, DefaultVisibility = WelfareVisibility.Confidential, Purpose = "A welfare-of-staff matter — a bereavement, a workload concern, a health matter. Recorded to support, never scored.", Color = "#8a7a81", SortOrder = 12 },
    };
}
