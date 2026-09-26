using System.Globalization;
using QMgr.Application;
using QMgr.Domain.Entities.Calendar;
using QMgr.Domain.Enums;

namespace QMgr.API.Application.Services;

/// <summary>
/// One school event, written into an iCalendar document — the ONE place that decides how, used by the private feed and
/// by "Add to my calendar" (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING, B16/B17, 2026-09-26).
///
/// <para><b>A timed event over several days runs at those times EACH day</b>, which is what the calendar page shows
/// ("08:00–17:00" on every day of a retreat). The feed used to write one continuous block from the first morning to the
/// last evening — a three-day retreat became a 57-hour appointment. It is written as one VEVENT per day, each with its
/// own UID (<c>{id}-{yyyyMMdd}</c>), so every client agrees with the page.</para>
/// </summary>
public static class IcsEvents
{
    public static void Write(IcsWriter ics, SchoolEvent e, TimeZoneInfo zone)
    {
        var modified = e.UpdatedAt ?? e.CreatedAt;
        var cancelled = e.Status == SchoolEventStatus.Cancelled;
        var description = Description(e);
        var summary = cancelled ? $"Cancelled: {e.Title}" : e.Title;

        if (e.StartTime is { } st)
        {
            var days = Math.Max(0, e.EndsOn.DayNumber - e.StartsOn.DayNumber);
            for (var i = 0; i <= days; i++)
            {
                var day = e.StartsOn.AddDays(i);
                var start = BranchClock.ToUtc(day.ToDateTime(st), zone);
                DateTime? end = e.EndTime is { } et ? BranchClock.ToUtc(day.ToDateTime(et), zone) : null;
                var uid = days == 0 ? $"{e.Id}@qmgr" : $"{e.Id}-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}@qmgr";
                ics.TimedEvent(uid, modified, start, end, summary, e.Location, description, e.Category, modified, e.Version, cancelled);
            }
        }
        else
        {
            ics.AllDayEvent($"{e.Id}@qmgr", modified, e.StartsOn, e.EndsOn, summary, e.Location, description, e.Category, modified, e.Version, cancelled);
        }
    }

    private static string? Description(SchoolEvent e)
    {
        var parts = new List<string>();
        if (e.Status == SchoolEventStatus.Cancelled && !string.IsNullOrWhiteSpace(e.CancelReason)) parts.Add("Cancelled: " + e.CancelReason);
        if (e.AttendanceRequired) parts.Add("Attendance required.");
        if (!string.IsNullOrWhiteSpace(e.Description)) parts.Add(e.Description!);
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    /// <summary>A file name a phone will open: letters, digits and dashes, ".ics".</summary>
    public static string FileName(SchoolEvent e)
    {
        var slug = new string(e.Title.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 50) slug = slug[..50].Trim('-');
        return (slug.Length == 0 ? "event" : slug) + ".ics";
    }
}
