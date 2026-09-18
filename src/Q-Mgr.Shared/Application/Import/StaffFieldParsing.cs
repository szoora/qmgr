using System.Globalization;
using QMgr.Domain.Enums;

namespace QMgr.Application.Import;

/// <summary>
/// The staff-record fields an import row carries, parsed the SAME WAY on both sides.
///
/// <para>This lives in Q-Mgr.Shared deliberately. The browser parses a row to tell the reader what
/// will happen before they commit; the server parses it again because a client can post whatever it
/// likes. Those two answers must agree, or the preview is a lie — "03/04/2026 → 3 April" in the
/// browser and "→ 4 March" on the server would be a silent, permanent data error that nothing in
/// the UI would ever reveal. One implementation is the only way to guarantee they agree.</para>
///
/// <para>Day-first precedence matches the welfare import's, which is the product's existing rule:
/// this product's schools write dates day-first, so an ambiguous slash date resolves to d/M/y.</para>
/// </summary>
public static class StaffFieldParsing
{
    /// <summary>The date spellings accepted, in precedence order. Day-first before anything ambiguous.</summary>
    public static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
        "dd.MM.yyyy", "d.M.yyyy", "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy"
    };

    /// <summary>A date, or null when the cell is empty or unreadable. Never throws.</summary>
    public static DateOnly? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (DateOnly.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return DateOnly.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;
    }

    /// <summary>True when the cell held something that is not a readable date — a row WARNING, not a refusal.</summary>
    public static bool Unreadable(string? s) => !string.IsNullOrWhiteSpace(s) && Date(s) is null;

    /// <summary>
    /// "Permanent", "contract", "PART TIME", "part-time", "probation"… Anything unrecognised returns
    /// null and is warned about rather than refused: an employment type is not worth losing a row over.
    /// </summary>
    public static StaffEmploymentType? EmploymentType(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return t switch
        {
            "permanent" or "fulltime" or "full" => StaffEmploymentType.Permanent,
            "contract" or "fixedterm" or "pta" or "board" => StaffEmploymentType.Contract,
            "probation" or "probationary" => StaffEmploymentType.Probation,
            "parttime" or "part" => StaffEmploymentType.PartTime,
            "volunteer" or "voluntary" or "intern" => StaffEmploymentType.Volunteer,
            "seconded" or "secondment" => StaffEmploymentType.Seconded,
            _ => null
        };
    }

    /// <summary>"F", "Female", "M", "Male", "Other". Unrecognised returns null and is warned about.</summary>
    public static PersonSex? Sex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().ToLowerInvariant();
        return t switch
        {
            "f" or "female" or "woman" => PersonSex.Female,
            "m" or "male" or "man" => PersonSex.Male,
            "o" or "other" => PersonSex.Other,
            _ => null
        };
    }

    /// <summary>Trimmed, or null when blank — so an empty cell never overwrites a stored value with "".</summary>
    public static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>
    /// The one rule for whether a pair of employment dates makes sense. Returns the complaint, or
    /// null when they are fine. Checked on both sides for the same reason everything else here is.
    /// </summary>
    public static string? DateRangeProblem(DateOnly? start, DateOnly? end)
    {
        if (start is { } s && end is { } e && e < s)
            return "The end date is before the start date.";
        if (start is { } s2 && s2.Year < 1950)
            return "That start date looks wrong — it is before 1950.";
        if (end is { } e2 && e2 > DateOnly.FromDateTime(DateTime.UtcNow).AddYears(5))
            return "That end date is more than five years away.";
        return null;
    }

    /// <summary>A date of birth that would make somebody under 16 or over 100 is almost certainly a typo.</summary>
    public static string? DateOfBirthProblem(DateOnly? dob)
    {
        if (dob is not { } d) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - d.Year - (today < d.AddYears(today.Year - d.Year) ? 1 : 0);
        if (age < 16) return $"That date of birth makes this person {age} — check the column.";
        if (age > 100) return $"That date of birth makes this person {age} — check the column.";
        return null;
    }
}
