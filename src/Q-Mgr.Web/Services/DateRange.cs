namespace QMgr.Web.Services;

/// <summary>
/// A from/to pair that cannot be back to front.
///
/// Twelve pages in this app filter by a date range and, before this, every one of them held two
/// loose <c>DateOnly</c> fields and re-derived its own presets and its own caption. Three different
/// preset sets existed (Reports Overview had three buttons, Welfare Reports had none, the Visitor
/// Report had seven chips of its own), so the same period was described three different ways
/// depending on which page you were reading.
///
/// Construction normalizes the order, so a caller who binds From to a later date than To gets a
/// valid range rather than an empty result set and no explanation.
/// </summary>
public readonly record struct DateRange
{
    public DateRange(DateOnly from, DateOnly to)
    {
        if (from > to) (from, to) = (to, from);
        From = from;
        To = to;
    }

    public DateOnly From { get; init; }
    public DateOnly To { get; init; }

    /// <summary>Inclusive day count — a single-day range is 1, not 0.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>
    /// The same words the picker's caption, the print header and the export subtitle use.
    ///
    /// An all-time range reports itself as "the complete record" rather than spelling out its
    /// year-2000 floor: "1 Jan 2000 – 9 Sep 2026" is technically what was asked for and reads as a
    /// bug to anyone who did not choose that date.
    /// </summary>
    public string Caption => IsAllTime ? "the complete record" : QDateFormat.Range(From, To);

    public override string ToString() => Caption;

    public static DateRange Today()
    {
        var t = DateOnly.FromDateTime(DateTime.Now);
        return new DateRange(t, t);
    }

    public static DateRange LastDays(int days)
    {
        var t = DateOnly.FromDateTime(DateTime.Now);
        return new DateRange(t.AddDays(-(Math.Max(1, days) - 1)), t);
    }

    /// <summary>
    /// The widest range the app ever asks for. Not <c>DateOnly.MinValue</c>: a range starting in
    /// year 1 renders as "1 Jan 0001 – …" in every caption and print header, which reads as a bug.
    /// 1 January 2000 predates any record this product can hold and still prints sensibly.
    /// </summary>
    public static DateRange AllTime() =>
        new(new DateOnly(2000, 1, 1), DateOnly.FromDateTime(DateTime.Now));

    public bool IsAllTime => From <= new DateOnly(2000, 1, 1);
}

/// <summary>One named shortcut on the range picker.</summary>
public sealed record DateRangePreset(string Label, Func<DateRange> Resolve)
{
    public bool Matches(DateRange range)
    {
        var candidate = Resolve();
        return candidate.From == range.From && candidate.To == range.To;
    }
}

/// <summary>
/// The shortcuts every range picker in the app offers. One list, so "Last 30 days" means the same
/// thing on the visitor report as it does on the welfare timeline.
///
/// Ranges are computed against the machine's LOCAL date. Server-side reporting re-resolves them in
/// the branch's own timezone, so these are a convenience for typing rather than the authority on
/// where a day boundary falls.
/// </summary>
public static class DateRangePresets
{
    /// <summary>The everyday set: short windows a person picks without thinking.</summary>
    public static readonly IReadOnlyList<DateRangePreset> Standard = new List<DateRangePreset>
    {
        new("Today", DateRange.Today),
        new("Yesterday", () => { var y = DateOnly.FromDateTime(DateTime.Now).AddDays(-1); return new DateRange(y, y); }),
        new("Last 7 days", () => DateRange.LastDays(7)),
        new("Last 30 days", () => DateRange.LastDays(30)),
        new("This month", () =>
        {
            var now = DateTime.Now;
            return new DateRange(new DateOnly(now.Year, now.Month, 1), DateOnly.FromDateTime(now));
        }),
        new("Last month", () =>
        {
            var firstOfThis = new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1);
            return new DateRange(firstOfThis.AddMonths(-1), firstOfThis.AddDays(-1));
        }),
        new("Last 90 days", () => DateRange.LastDays(90)),
        new("This year", () =>
        {
            var now = DateTime.Now;
            return new DateRange(new DateOnly(now.Year, 1, 1), DateOnly.FromDateTime(now));
        })
    };

    /// <summary>
    /// Standard plus "All time" — for a record that is read as a whole history (a student's welfare
    /// timeline) rather than as a period, where defaulting to a window would hide things somebody
    /// opened the page specifically to find.
    /// </summary>
    public static readonly IReadOnlyList<DateRangePreset> WithAllTime =
        Standard.Concat(new[] { new DateRangePreset("All time", DateRange.AllTime) }).ToList();

    /// <summary>The label matching this range, or null when it is a hand-picked custom period.</summary>
    public static string? LabelFor(DateRange range, IReadOnlyList<DateRangePreset>? presets = null) =>
        (presets ?? Standard).FirstOrDefault(p => p.Matches(range))?.Label;
}
