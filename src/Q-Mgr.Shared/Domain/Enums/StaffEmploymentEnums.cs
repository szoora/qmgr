namespace QMgr.Domain.Enums;

/// <summary>
/// HOW SOMEBODY IS ENGAGED IS THE SCHOOL'S OWN LIST, NOT AN ENUM (2026-09-23).
///
/// <para>It was a six-value enum (Permanent, Contract, Probation, Part time, Volunteer, Seconded). Nothing in the
/// codebase branched on any value — no rule, no formula, nothing shown or hidden — so by the test this project
/// applies (<b>does the value carry behaviour?</b>) it is taxonomy, and taxonomy is data. It also could not say
/// what a school actually needs: the MoES staff return separates government-paid from PTA- and board-paid
/// teachers, and both squashed into "Contract". It is <c>StaffPerformancePolicyDto.EmploymentTypes</c> now, a
/// <c>VocabularyItemDto</c> list the school edits beside its staff groups, seeded with the six old names so
/// nothing stored changes meaning. <c>User.EmploymentType</c> holds the NAME, as the school spelled it.</para>
///
/// <para>Nullable on the person, as before: a school that does not track it leaves it unset rather than guess.</para>
/// </summary>
public static class EmploymentTypes
{
    /// <summary>The six names the enum had, in its order. Seeded on first policy read; the migration mapped each
    /// stored value to exactly one of these.</summary>
    public static readonly IReadOnlyList<string> Defaults = new[] { "Permanent", "Contract", "Probation", "Part time", "Volunteer", "Seconded" };

    /// <summary>The comparison form — letters and digits, upper-cased, the <c>StaffGroups.Key</c> rule — so
    /// "Part time", "part-time" and "PART TIME" are one type and a stray space cannot split one in two.</summary>
    public static string Key(string? name)
        => string.IsNullOrWhiteSpace(name) ? string.Empty : new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // Words a spreadsheet uses for the six seeded types. Only ever consulted when the school still HAS the type
    // they point at: a school that renamed "Contract" to "PTA-paid" has made "contract" mean nothing here.
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["FULLTIME"] = "Permanent", ["FULL"] = "Permanent",
        ["FIXEDTERM"] = "Contract", ["PTA"] = "Contract", ["BOARD"] = "Contract",
        ["PROBATIONARY"] = "Probation",
        ["PART"] = "Part time",
        ["VOLUNTARY"] = "Volunteer", ["INTERN"] = "Volunteer",
        ["SECONDMENT"] = "Seconded",
    };

    /// <summary>
    /// The school's own spelling of the type <paramref name="input"/> names, from <paramref name="names"/> (the
    /// ACTIVE list), or null when it names none. The ONE reader for "is this an employment type here" — the import
    /// preview, the import job and the profile editor all call it, so they cannot disagree.
    /// </summary>
    public static string? Resolve(string? input, IEnumerable<string> names)
    {
        var key = Key(input);
        if (key.Length == 0) return null;
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var exact = list.FirstOrDefault(n => Key(n) == key);
        if (exact != null) return exact;
        return Synonyms.TryGetValue(key, out var seeded) ? list.FirstOrDefault(n => Key(n) == Key(seeded)) : null;
    }
}

/// <summary>
/// The status the directory and every staff query read, derived from the employment dates rather
/// than stored, so the two can never disagree. See <c>StaffEmployment.StatusOf</c>.
/// </summary>
public enum StaffEmploymentStatus
{
    /// <summary>Started, not ended.</summary>
    Active = 0,

    /// <summary>A start date in the future — the account exists, the person has not begun.</summary>
    NotStarted = 1,

    /// <summary>An end date in the past. The person is KEPT, not deleted: their records, appraisals
    /// and the registers they appear on are history that must survive their leaving.</summary>
    Left = 2
}
