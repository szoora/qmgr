using QMgr.Application.DTOs;

namespace QMgr.Web.Components.Admin.Staff;

/// <summary>
/// The period keys a page can offer: the tenant's own defined periods when the policy has any,
/// otherwise the derived Ugandan terms the API derives too (T1 Jan–Apr, T2 May–Aug, T3 Sep–Dec)
/// plus the annual roll-up, for this year and last. One place, so the appraisal board, the
/// reports page and the print routes agree on what "2026-T3" means.
/// </summary>
public static class StaffPeriods
{
    public record Option(string Key, string Label);

    /// <summary>The key the API would pick for today: "{year}-T{n}".</summary>
    public static string CurrentKey(DateTime? today = null)
    {
        var d = today ?? DateTime.Today;
        var term = d.Month <= 4 ? 1 : d.Month <= 8 ? 2 : 3;
        return $"{d.Year}-T{term}";
    }

    public static IReadOnlyList<Option> Options(IEnumerable<PerformancePeriodDto>? policyPeriods = null, DateTime? today = null)
    {
        var defined = (policyPeriods ?? Enumerable.Empty<PerformancePeriodDto>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .OrderByDescending(p => p.Start)
            .Select(p => new Option(p.Key, string.IsNullOrWhiteSpace(p.Name) ? p.Key : $"{p.Name} ({p.Key})"))
            .ToList();
        if (defined.Count > 0) return defined;

        var year = (today ?? DateTime.Today).Year;
        var list = new List<Option>();
        foreach (var y in new[] { year, year - 1 })
        {
            list.Add(new Option($"{y}-T3", $"Term 3 {y} (Sep–Dec)"));
            list.Add(new Option($"{y}-T2", $"Term 2 {y} (May–Aug)"));
            list.Add(new Option($"{y}-T1", $"Term 1 {y} (Jan–Apr)"));
            list.Add(new Option($"{y}", $"Annual {y}"));
        }
        return list;
    }
}
