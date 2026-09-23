using System.Globalization;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;
using QMgr.Web.Services;

namespace QMgr.Web.Components.Pages.Calendar;

/// <summary>
/// How a school event reads, in one place: the Calendar page, the printed term programme, My School Day,
/// the portal's "Coming up" and the signage zone all show the same event, and each writing its own "08:30
/// onwards" is how two of them come to disagree. Dates go through <see cref="QDateFormat"/>, like every
/// date in the app.
/// </summary>
public static class CalendarDisplay
{
    /// <summary>"All day", "08:30–10:00", or "08:30 onwards" when the document gave no end.</summary>
    public static string When(SchoolEventDto e)
    {
        if (string.IsNullOrWhiteSpace(e.StartTime)) return "All day";
        if (string.IsNullOrWhiteSpace(e.EndTime)) return $"{e.StartTime} onwards";
        return $"{e.StartTime}–{e.EndTime}";
    }

    /// <summary>The time column of a table: blank for an all-day event rather than the words "All day" on every row.</summary>
    public static string TimeCell(SchoolEventDto e)
    {
        if (string.IsNullOrWhiteSpace(e.StartTime)) return "All day";
        return When(e);
    }

    /// <summary>"Wed 23 Sep 2026, 08:30 onwards" or "23 Sep – 25 Sep 2026" for a multi-day event.</summary>
    public static string DatesAndTime(SchoolEventDto e)
    {
        var dates = e.EndsOn > e.StartsOn
            ? QDateFormat.Range(e.StartsOn, e.EndsOn)
            : $"{QDateFormat.Dy(e.StartsOn)} {QDateFormat.D(e.StartsOn)}";
        return string.IsNullOrWhiteSpace(e.StartTime) ? dates : $"{dates}, {When(e)}";
    }

    /// <summary>Who is responsible, as the document wrote it, else the resolved names. Null when nobody is named.</summary>
    public static string? Responsible(SchoolEventDto e)
    {
        if (!string.IsNullOrWhiteSpace(e.ResponsibleText)) return e.ResponsibleText.Trim();
        if (e.ResponsibleNames is { Count: > 0 }) return string.Join(", ", e.ResponsibleNames);
        return null;
    }

    /// <summary>"S.4, S.5" or null for the whole school.</summary>
    public static string? Classes(SchoolEventDto e) => e.ClassNames is { Count: > 0 } ? string.Join(", ", e.ClassNames) : null;

    /// <summary>The audience as words, for the detail dialog.</summary>
    public static string Audience(EventAudience audience)
    {
        var parts = new List<string>();
        if (audience.HasFlag(EventAudience.Staff)) parts.Add("Staff");
        if (audience.HasFlag(EventAudience.Students)) parts.Add("Students");
        if (audience.HasFlag(EventAudience.Guardians)) parts.Add("Guardians");
        if (audience.HasFlag(EventAudience.Public)) parts.Add("Public (signage)");
        return parts.Count == 0 ? "Nobody yet" : string.Join(" · ", parts);
    }

    /// <summary>Does this event touch the given day? The end is inclusive.</summary>
    public static bool IsOn(SchoolEventDto e, DateOnly day) => e.StartsOn <= day && e.EndsOn >= day;

    /// <summary>All-day first, then timed in start order — the order every day list uses.</summary>
    public static IEnumerable<SchoolEventDto> InDayOrder(IEnumerable<SchoolEventDto> events)
        => events.OrderBy(e => e.StartTime != null).ThenBy(e => e.StartTime, StringComparer.Ordinal).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase);

    /// <summary>The Monday of the week containing <paramref name="day"/>. Every calendar in this app is Monday-first.</summary>
    public static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>yyyy-MM-dd, invariant — the wire format of the calendar's query strings.</summary>
    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Parse a yyyy-MM-dd query value; null when absent or malformed.</summary>
    public static DateOnly? ParseIso(string? value)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
