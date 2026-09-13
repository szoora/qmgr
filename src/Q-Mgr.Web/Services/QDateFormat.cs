using System.Globalization;

namespace QMgr.Web.Services;

/// <summary>
/// How a date is written in this product. One place, because there were seventeen.
///
/// An audit of the Razor files found <c>MMM d, yyyy</c>, <c>MMM dd, yyyy</c>, <c>d MMM yyyy</c>,
/// <c>MMMM dd, yyyy</c>, <c>dddd, MMMM dd, yyyy</c> and twelve more spellings hand-typed across the
/// app, so the same timestamp read differently on two adjacent pages.
///
/// <para>
/// <b>Day-first, month abbreviated, four-digit year.</b> "09 Sep 2026" cannot be misread;
/// "09/09/2026" happens to be safe and "03/09/2026" is genuinely ambiguous — it is 3 September to
/// a Ugandan reader and 9 March to an American one, and a native
/// <c>&lt;input type="date"&gt;</c> picks which of those to show from the *browser's* locale, which
/// this application does not control. That is the whole reason QDatePicker stopped using it.
/// </para>
///
/// <para>
/// Everything here is InvariantCulture on purpose. The month abbreviation stays English across the
/// app's three UI languages rather than following the request culture, so a printed sheet, a CSV
/// and the screen it was read from always agree — a report whose dates changed language between
/// the preview and the download would be worse than one that is only ever English.
/// </para>
/// </summary>
public static class QDateFormat
{
    /// <summary>09 Sep 2026 — the default for any date shown to a person.</summary>
    public const string Date = "dd MMM yyyy";

    /// <summary>09 Sep 2026, 14:30</summary>
    public const string DateTime = "dd MMM yyyy, HH:mm";

    /// <summary>Wed 09 Sep 2026 — where the weekday carries meaning (schedules, visiting days).</summary>
    public const string DayDate = "ddd dd MMM yyyy";

    /// <summary>Wednesday 9 September 2026 — document headers, where there is room to be formal.</summary>
    public const string LongDate = "dddd d MMMM yyyy";

    /// <summary>Wednesday 9 September 2026, 14:30 — a formal header that also states the time it was produced.</summary>
    public const string LongDateTime = "dddd d MMMM yyyy, HH:mm";

    /// <summary>Wednesday 9 September — a day header inside a view whose year is already established (the appointments day view, a booking picker).</summary>
    public const string LongDayDate = "dddd d MMMM";

    /// <summary>Wednesday. On its own only where the weekday IS the fact ("we are closed on a Wednesday").</summary>
    public const string Weekday = "dddd";

    /// <summary>14:30. 24-hour throughout: no am/pm, no ambiguity, and it sorts.</summary>
    public const string Time = "HH:mm";

    /// <summary>
    /// 14:30:05 — a live clock, and the evacuation roll call's "as at", where the second is part of
    /// the claim. Note that unlike the date formats, "HH:mm" and "HH:mm:ss" were the ONLY two time
    /// spellings in the app, and neither can be misread — so time was never the ambiguity this
    /// class exists to fix, and the remaining hand-typed <c>ToString("HH:mm")</c> calls on live
    /// clocks were deliberately left rather than churned.
    /// </summary>
    public const string TimeWithSeconds = "HH:mm:ss";

    /// <summary>09 Sep — inside a period whose year is already stated (chart axes, table rows).</summary>
    public const string Compact = "dd MMM";

    /// <summary>09 Sep, 14:30 — a timestamp inside a period whose year is already stated (activity feeds, "last seen").</summary>
    public const string CompactDateTime = "dd MMM, HH:mm";

    /// <summary>Sep 2026</summary>
    public const string MonthYear = "MMM yyyy";

    /// <summary>September 2026 — a month heading with room for the full name.</summary>
    public const string MonthYearLong = "MMMM yyyy";

    /// <summary>Sep — a bare month label on a chart axis, where the surrounding period gives the year.</summary>
    public const string MonthAbbrev = "MMM";

    /// <summary>September — a bare month name in prose.</summary>
    public const string MonthName = "MMMM";

    /// <summary>2026-09-09 — machine-facing only: filenames, query strings, CSV sort keys.</summary>
    public const string Iso = "yyyy-MM-dd";

    /// <summary>2026-09-09 14:30 — machine-facing timestamp for exports.</summary>
    public const string IsoDateTime = "yyyy-MM-dd HH:mm";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string D(DateTime value) => value.ToString(Date, Inv);
    public static string D(DateOnly value) => value.ToString(Date, Inv);
    public static string D(DateTime? value) => value.HasValue ? D(value.Value) : "—";
    public static string D(DateOnly? value) => value.HasValue ? D(value.Value) : "—";

    public static string DT(DateTime value) => value.ToString(DateTime, Inv);
    public static string DT(DateTime? value) => value.HasValue ? DT(value.Value) : "—";

    public static string T(DateTime value) => value.ToString(Time, Inv);
    public static string T(DateTime? value) => value.HasValue ? T(value.Value) : "—";

    /// <summary>14:30:05.</summary>
    public static string TS(DateTime value) => value.ToString(TimeWithSeconds, Inv);

    public static string Long(DateTime value) => value.ToString(LongDate, Inv);
    public static string Long(DateTime? value) => value.HasValue ? Long(value.Value) : "—";

    /// <summary>Wednesday 9 September 2026, 14:30.</summary>
    public static string LongDT(DateTime value) => value.ToString(LongDateTime, Inv);
    public static string LongDT(DateTime? value) => value.HasValue ? LongDT(value.Value) : "—";

    /// <summary>Wednesday 9 September — a day header with no year.</summary>
    public static string LongDay(DateTime value) => value.ToString(LongDayDate, Inv);
    public static string LongDay(DateOnly value) => value.ToString(LongDayDate, Inv);

    /// <summary>Wed 09 Sep 2026.</summary>
    public static string DD(DateTime value) => value.ToString(DayDate, Inv);
    public static string DD(DateTime? value) => value.HasValue ? DD(value.Value) : "—";

    /// <summary>09 Sep — no year.</summary>
    public static string C(DateTime value) => value.ToString(Compact, Inv);
    public static string C(DateOnly value) => value.ToString(Compact, Inv);
    public static string C(DateTime? value) => value.HasValue ? C(value.Value) : "—";

    /// <summary>09 Sep, 14:30 — no year.</summary>
    public static string CDT(DateTime value) => value.ToString(CompactDateTime, Inv);
    public static string CDT(DateTime? value) => value.HasValue ? CDT(value.Value) : "—";

    /// <summary>Sep 2026.</summary>
    public static string MY(DateTime value) => value.ToString(MonthYear, Inv);
    public static string MY(DateTime? value) => value.HasValue ? MY(value.Value) : "—";

    /// <summary>September 2026.</summary>
    public static string MYLong(DateTime value) => value.ToString(MonthYearLong, Inv);

    /// <summary>Sep — a bare month label.</summary>
    public static string Mon(DateTime value) => value.ToString(MonthAbbrev, Inv);
    public static string Mon(DateOnly value) => value.ToString(MonthAbbrev, Inv);

    /// <summary>September — a bare month name.</summary>
    public static string MonLong(DateTime value) => value.ToString(MonthName, Inv);

    /// <summary>Wednesday.</summary>
    public static string Day(DateTime value) => value.ToString(Weekday, Inv);
    public static string Day(DateOnly value) => value.ToString(Weekday, Inv);

    public static string Iso8601(DateOnly value) => value.ToString(Iso, Inv);
    public static string Iso8601(DateTime value) => value.ToString(Iso, Inv);

    /// <summary>
    /// A UTC timestamp shown in the reader's own time. Every timestamp this system stores is UTC;
    /// rendering one raw is how a 21:00 check-in ends up displayed as tomorrow.
    /// </summary>
    public static string LocalDT(DateTime utc) => DT(utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc);
    public static string LocalDT(DateTime? utc) => utc.HasValue ? LocalDT(utc.Value) : "—";

    /// <summary>
    /// A range, written the shortest way that stays unambiguous:
    /// one day → "9 Sep 2026"; inside a month → "1–30 Sep 2026"; inside a year → "1 Sep – 3 Oct 2026";
    /// otherwise → "28 Dec 2025 – 3 Jan 2026".
    ///
    /// Used by the range picker's caption, the print header and the export subtitle, so all three
    /// describe the same period in the same words.
    /// </summary>
    public static string Range(DateOnly from, DateOnly to)
    {
        if (from > to) (from, to) = (to, from);

        if (from == to) return from.ToString("d MMM yyyy", Inv);
        if (from.Year == to.Year && from.Month == to.Month)
            return $"{from.Day}–{to.Day} {to.ToString("MMM yyyy", Inv)}";
        if (from.Year == to.Year)
            return $"{from.ToString("d MMM", Inv)} – {to.ToString("d MMM yyyy", Inv)}";
        return $"{from.ToString("d MMM yyyy", Inv)} – {to.ToString("d MMM yyyy", Inv)}";
    }

    public static string Range(DateTime from, DateTime to) =>
        Range(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to));

    /// <summary>
    /// "3 days ago", "in 2 hours", "just now". For anything where the gap matters more than the
    /// instant — a last-seen, a due date. Never used alone on a record that must be auditable:
    /// there, the exact timestamp is the fact and this is at most a hint beside it.
    /// </summary>
    public static string Relative(DateTime utc)
    {
        var delta = System.DateTime.UtcNow - (utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime());
        var past = delta.Ticks >= 0;
        var abs = delta.Duration();

        if (abs.TotalSeconds < 45) return "just now";
        if (abs.TotalMinutes < 60) return Phrase((int)abs.TotalMinutes, "minute", past);
        if (abs.TotalHours < 24) return Phrase((int)abs.TotalHours, "hour", past);
        if (abs.TotalDays < 31) return Phrase((int)abs.TotalDays, "day", past);
        if (abs.TotalDays < 365) return Phrase((int)(abs.TotalDays / 30), "month", past);
        return Phrase((int)(abs.TotalDays / 365), "year", past);
    }

    private static string Phrase(int count, string unit, bool past)
    {
        var n = Math.Max(1, count);
        var noun = n == 1 ? unit : unit + "s";
        return past ? $"{n} {noun} ago" : $"in {n} {noun}";
    }
}
