using System.Globalization;
using System.Text.RegularExpressions;

namespace QMgr.Application.Import.Programme;

/// <summary>What <see cref="TimeRangeText.Parse"/> read out of one time cell.</summary>
public sealed record TimeRangeResult
{
    public TimeOnly? Start { get; init; }
    /// <summary>Null with a start = a single moment ("11:00 pm") or an open end ("onwards").</summary>
    public TimeOnly? End { get; init; }
    /// <summary>"11:30 am onwards": the programme gives no end.</summary>
    public bool OpenEnded { get; init; }
    public bool HasTime => Start.HasValue;
    public string? Note { get; init; }

    public string? StartText => Start?.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string? EndText => End?.ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// Times of day as a school writes them (plan §5), the one home for reading them.
///
/// <para>The rule that matters: a programme writes am/pm ONCE, on the end — <c>8:00 – 5:00 pm</c> — so the
/// meridiem is carried BACK to the start unless that would put the start after the end: <c>6:30 – 7:30 pm</c>
/// is 18:30–19:30, <c>8:00 – 5:00 pm</c> is 08:00–17:00, <c>11:00 – 12:50pm</c> is 11:00–12:50, and
/// <c>8:00 –10:00 pm</c> is 20:00–22:00. <c>11:30 am onwards</c> has no end; <c>11:00 pm</c> is a moment.</para>
/// </summary>
public static class TimeRangeText
{
    private static readonly Regex TimeRx = new(
        @"(?<h>\d{1,2})(?:\s*[:.]\s*(?<m>\d{2}))?\s*(?<ap>a\.?\s?m\.?|p\.?\s?m\.?|noon|midnight)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Onwards = new(@"\b(onwards?|till late|until late|to late)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TimeRangeResult Parse(string? text)
    {
        var s = SchoolDateText.Normalise(text);
        if (s.Length == 0) return new TimeRangeResult();

        var matches = TimeRx.Matches(s)
            .Where(m => m.Groups["m"].Success || m.Groups["ap"].Success) // a bare "3" is not a time
            .Take(2).ToList();
        if (matches.Count == 0) return new TimeRangeResult { Note = $"\"{s}\" is not a time of day." };

        var first = Read(matches[0]);
        if (first == null) return new TimeRangeResult { Note = $"\"{s}\" is not a time of day." };
        var open = Onwards.IsMatch(s);

        if (matches.Count == 1)
        {
            var single = Resolve(first.Value.Hour, first.Value.Minute, first.Value.Meridiem);
            return single == null
                ? new TimeRangeResult { Note = $"\"{s}\" is not a time of day." }
                : new TimeRangeResult { Start = single, OpenEnded = open };
        }

        var second = Read(matches[1]);
        if (second == null) return new TimeRangeResult { Note = $"\"{s}\" is not a time of day." };

        var endMeridiem = second.Value.Meridiem;
        var startMeridiem = first.Value.Meridiem;
        var end = Resolve(second.Value.Hour, second.Value.Minute, endMeridiem);
        if (end == null) return new TimeRangeResult { Note = $"\"{s}\" is not a time of day." };

        TimeOnly? start;
        if (startMeridiem != null) start = Resolve(first.Value.Hour, first.Value.Minute, startMeridiem);
        else if (endMeridiem != null)
        {
            // Carry the end's meridiem back, unless that would start after the end.
            var carried = Resolve(first.Value.Hour, first.Value.Minute, endMeridiem);
            var other = Resolve(first.Value.Hour, first.Value.Minute, endMeridiem == "pm" ? "am" : "pm");
            start = carried != null && carried <= end ? carried : other;
        }
        else start = Resolve(first.Value.Hour, first.Value.Minute, null);

        if (start == null) return new TimeRangeResult { Note = $"\"{s}\" is not a time of day." };
        if (start > end)
            return new TimeRangeResult { Start = start, Note = $"\"{s}\" ends before it starts — only the start was kept." };
        return new TimeRangeResult { Start = start, End = end };
    }

    private static (int Hour, int Minute, string? Meridiem)? Read(Match m)
    {
        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (minute > 59) return null;
        string? meridiem = null;
        if (m.Groups["ap"].Success)
        {
            var ap = m.Groups["ap"].Value.ToLowerInvariant().Replace(".", string.Empty).Replace(" ", string.Empty);
            meridiem = ap switch { "noon" => "noon", "midnight" => "midnight", _ => ap.StartsWith('p') ? "pm" : "am" };
        }
        return (hour, minute, meridiem);
    }

    private static TimeOnly? Resolve(int hour, int minute, string? meridiem)
    {
        switch (meridiem)
        {
            case "noon": return new TimeOnly(12, 0);
            case "midnight": return new TimeOnly(0, 0);
            case "am":
                if (hour is < 1 or > 12) return null;
                return new TimeOnly(hour == 12 ? 0 : hour, minute);
            case "pm":
                if (hour is < 1 or > 12) return null;
                return new TimeOnly(hour == 12 ? 12 : hour + 12, minute);
            default:
                return hour is >= 0 and <= 23 ? new TimeOnly(hour, minute) : null;
        }
    }
}
