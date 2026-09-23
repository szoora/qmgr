using System.Globalization;
using System.Text.RegularExpressions;

namespace QMgr.Application.Import.Programme;

/// <summary>What <see cref="SchoolDateText.Parse"/> read out of one date cell.</summary>
public sealed record SchoolDateResult
{
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public bool HasDate => Start.HasValue;

    /// <summary>Nothing in the text looks like a date at all ("Administrator", blank). Not a problem — just not a date.</summary>
    public bool NotADate { get; init; }

    /// <summary>Only a year ("2026"): the row is undated and somebody has to choose.</summary>
    public bool YearOnly { get; init; }

    /// <summary>The written weekday disagrees with the date and nothing could reconcile them: a person must decide.</summary>
    public bool WeekdayMismatch { get; init; }

    /// <summary>The year was repaired by the weekday check — shown to the reader, never applied silently.</summary>
    public bool YearCorrected { get; init; }

    /// <summary>No year was written; it was taken from the document.</summary>
    public bool YearInferred { get; init; }

    /// <summary>A sentence for the reader: the repair made, or why the text could not be read.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Dates as a school writes them (plan §5), the one home for reading them. InvariantCulture throughout.
///
/// <list type="bullet">
/// <item>Weekday optional; ordinals, abbreviations ("Sept", "Thurs") and full stops: <c>Thurs 10th Sept, 2026</c>.</item>
/// <item>Ranges are one event: <c>Fri 25th – Sat 26th Sept 2026</c>, <c>Fri 2nd – 3rd Oct, 2026</c>, <c>25th -26th Sept</c>,
/// <c>Saturday 12th Sept. 2026 to Sunday 20th Sept. 2026</c> — at most 92 days.</item>
/// <item>Week bands: <c>7th - 13th September 2026</c>; <c>30th - 4th December 2026</c> carries the month BACKWARDS.</item>
/// <item>Numeric dates are DAY-FIRST: <c>12/09/2026</c> is 12 September — this product's market writes them that way.</item>
/// <item>A bare year (<c>2026</c>) is no date.</item>
/// <item>The WEEKDAY IS A CHECK DIGIT. When the stated weekday is wrong in the stated year but right in the document's
/// year, the document's year is used and the reader is told ("2025 read as 2026 — 23 Nov 2025 is a Sunday").</item>
/// </list>
/// </summary>
public static class SchoolDateText
{
    /// <summary>A range longer than this is not one event — it is two dates somebody should look at.</summary>
    public const int MaxRangeDays = 92;

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1, ["feb"] = 2, ["febr"] = 2, ["february"] = 2, ["mar"] = 3, ["march"] = 3,
        ["apr"] = 4, ["april"] = 4, ["may"] = 5, ["jun"] = 6, ["june"] = 6, ["jul"] = 7, ["july"] = 7,
        ["aug"] = 8, ["august"] = 8, ["sep"] = 9, ["sept"] = 9, ["september"] = 9, ["oct"] = 10, ["october"] = 10,
        ["nov"] = 11, ["november"] = 11, ["dec"] = 12, ["december"] = 12
    };

    private static readonly Dictionary<string, DayOfWeek> Weekdays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday, ["weds"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday
    };

    private static readonly Regex NumericDate = new(@"(?<!\d)(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{4}|\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex YearOnlyRx = new(@"^\s*(19|20)\d{2}\s*$", RegexOptions.Compiled);
    private static readonly Regex RangeSplit = new(@"\s+(?:to|until|till|through)\s+|\s*-\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Token = new(@"[A-Za-z]+|\d+", RegexOptions.Compiled);
    private static readonly Regex Ordinal = new(@"^(\d{1,2})(st|nd|rd|th)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Normalises the dashes Word types (en, em, minus) to a hyphen and tidies spacing.</summary>
    public static string Normalise(string? text)
        => Regex.Replace((text ?? string.Empty).Replace((char)0x2013, '-').Replace((char)0x2014, '-').Replace((char)0x2212, '-').Replace((char)0x00A0, ' '), @"\s+", " ").Trim();

    /// <summary>True when the text contains something that reads as a calendar date (not just a year).</summary>
    public static bool ContainsDate(string? text, int? documentYear = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (NumericDate.IsMatch(text)) return true;
        foreach (Match m in Regex.Matches(Normalise(text), @"(\d{1,2})(st|nd|rd|th)?\s*(?:of\s+)?([A-Za-z]+)", RegexOptions.IgnoreCase))
            if (Months.ContainsKey(m.Groups[3].Value)) return true;
        return false;
    }

    /// <param name="text">The cell as written.</param>
    /// <param name="documentYear">The year the document is about (its title, its other dates) — fills a missing year and repairs a typo.</param>
    /// <param name="expectedStartWeekday">For week bands: the weekday every band starts on (Monday), used as the check digit when none is written.</param>
    public static SchoolDateResult Parse(string? text, int? documentYear = null, DayOfWeek? expectedStartWeekday = null)
    {
        var s = Normalise(text);
        if (s.Length == 0) return new SchoolDateResult { NotADate = true };
        if (YearOnlyRx.IsMatch(s))
            return new SchoolDateResult { YearOnly = true, Note = $"Only a year is written (\"{s}\") — choose the date." };

        // ---- Numeric, day-first.
        var numeric = NumericDate.Matches(s);
        if (numeric.Count > 0)
        {
            var first = FromNumeric(numeric[0]);
            var last = numeric.Count > 1 ? FromNumeric(numeric[1]) : first;
            if (first == null || last == null)
                return new SchoolDateResult { Note = $"\"{s}\" is not a real date (dates are read day first: 12/09/2026 is 12 September)." };
            if (last < first) (first, last) = (last, first);
            if (last.Value.DayNumber - first.Value.DayNumber > MaxRangeDays)
                return new SchoolDateResult { Note = $"\"{s}\" spans more than {MaxRangeDays} days — enter the two dates as separate rows." };
            return new SchoolDateResult { Start = first, End = last };
        }

        // ---- Words: split once into the two ends of a range.
        var halves = RangeSplit.Split(s, 2);
        var left = Parts(halves[0]);
        var right = halves.Length > 1 ? Parts(halves[1]) : null;
        if (left.Day == null && right?.Day == null)
            return new SchoolDateResult { NotADate = true, Note = left.Month != null ? $"\"{s}\" has no day of the month." : null };
        if (right != null && right.Day == null) right = null;
        if (left.Day == null && right != null) { left = right; right = null; }

        // Borrow what the left end leaves out from the right end ("2nd – 3rd Oct, 2026").
        var month = left.Month ?? right?.Month;
        var statedYear = left.Year ?? right?.Year;
        var yearInferred = statedYear == null;
        var year = statedYear ?? documentYear;
        if (month == null) return new SchoolDateResult { Note = $"\"{s}\" has no month." };
        if (year == null) return new SchoolDateResult { Note = $"\"{s}\" has no year, and the document does not say which year it is about." };

        var start = Make(year.Value, month.Value, left.Day!.Value);
        DateOnly? end = start;
        if (right != null)
        {
            var endMonth = right.Month ?? month.Value;
            var endYear = right.Year ?? year.Value;
            end = Make(endYear, endMonth, right.Day!.Value);
            // "30th - 4th December": the left end borrowed December, so it is 30 November.
            if (start != null && end != null && start > end && left.Month == null)
                start = Make(end.Value.AddMonths(-1).Year, end.Value.AddMonths(-1).Month, left.Day.Value);
            // "28th December - 3rd January 2027": the left end borrowed the year.
            if (start != null && end != null && start > end && left.Year == null)
                start = start.Value.AddYears(-1);
        }
        if (start == null || end == null) return new SchoolDateResult { Note = $"\"{s}\" is not a real date." };
        if (end < start) return new SchoolDateResult { Note = $"\"{s}\" ends before it starts." };
        if (end.Value.DayNumber - start.Value.DayNumber > MaxRangeDays)
            return new SchoolDateResult { Note = $"\"{s}\" spans more than {MaxRangeDays} days — enter the two dates as separate rows." };

        // ---- The weekday is a check digit.
        var weekday = left.Weekday;
        var checkedWeekday = weekday ?? expectedStartWeekday;
        if (checkedWeekday is { } wd && start.Value.DayOfWeek != wd)
        {
            if (!yearInferred && documentYear is { } dy && dy != statedYear)
            {
                var shift = dy - statedYear!.Value;
                var repaired = SafeAddYears(start.Value, shift);
                if (repaired is { } r && r.DayOfWeek == wd)
                {
                    var note = string.Create(CultureInfo.InvariantCulture,
                        $"{statedYear} read as {dy} — {start.Value:d MMM yyyy} is a {start.Value.DayOfWeek}.");
                    return new SchoolDateResult
                    {
                        Start = r, End = SafeAddYears(end.Value, shift) ?? r, YearCorrected = true, Note = note
                    };
                }
            }
            if (weekday != null)
            {
                return new SchoolDateResult
                {
                    Start = start, End = end, WeekdayMismatch = true, YearInferred = yearInferred,
                    Note = string.Create(CultureInfo.InvariantCulture,
                        $"\"{s}\": {start.Value:d MMM yyyy} is a {start.Value.DayOfWeek}, not a {wd}. Choose which is right.")
                };
            }
        }
        // The right end's own weekday is checked too ("Fri 25th – Sat 26th").
        if (right?.Weekday is { } rwd && end.Value.DayOfWeek != rwd)
        {
            return new SchoolDateResult
            {
                Start = start, End = end, WeekdayMismatch = true, YearInferred = yearInferred,
                Note = string.Create(CultureInfo.InvariantCulture, $"\"{s}\": {end.Value:d MMM yyyy} is a {end.Value.DayOfWeek}, not a {rwd}.")
            };
        }

        return new SchoolDateResult { Start = start, End = end, YearInferred = yearInferred };
    }

    /// <summary>A date for display in a note: "Wed 2 Dec".</summary>
    public static string Short(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>The year a title or file name is about: the most frequent four-digit year from 2000 to 2099 in the texts.</summary>
    public static int? YearOf(IEnumerable<string?> texts)
    {
        var years = texts.Where(t => !string.IsNullOrEmpty(t))
            .SelectMany(t => Regex.Matches(t!, @"(?<!\d)(20\d{2})(?!\d)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))
            .GroupBy(y => y).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).FirstOrDefault();
        return years?.Key;
    }

    // ---- Internals ------------------------------------------------------------------------------------

    private sealed class DateParts
    {
        public DayOfWeek? Weekday;
        public int? Day;
        public int? Month;
        public int? Year;
    }

    private static DateParts Parts(string text)
    {
        var parts = new DateParts();
        foreach (Match m in Token.Matches(text.Replace(",", " ").Replace(".", " ")))
        {
            var t = m.Value;
            if (char.IsDigit(t[0]))
            {
                // Glued ordinals ("12th") come through as digits then letters in one regex match only when adjacent.
                if (t.Length == 4 && parts.Year == null) parts.Year = int.Parse(t, CultureInfo.InvariantCulture);
                else if (t.Length <= 2 && parts.Day == null) parts.Day = int.Parse(t, CultureInfo.InvariantCulture);
                continue;
            }
            if (Months.TryGetValue(t, out var month) && parts.Month == null) parts.Month = month;
            else if (Weekdays.TryGetValue(t, out var wd) && parts.Weekday == null && parts.Day == null) parts.Weekday = wd;
        }
        return parts;
    }

    private static DateOnly? FromNumeric(Match m)
    {
        var day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (year < 100) year += 2000;
        return Make(year, month, day);
    }

    private static DateOnly? Make(int year, int month, int day)
        => year is >= 1900 and <= 2200 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day) : null;

    private static DateOnly? SafeAddYears(DateOnly d, int years)
    {
        try { return d.AddYears(years); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Unused-parameter guard for ordinals: "12th" → 12. Exposed for the time parser's "12th at 2pm" cases.</summary>
    public static int? OrdinalDay(string token)
    {
        var m = Ordinal.Match(token.Trim());
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}
