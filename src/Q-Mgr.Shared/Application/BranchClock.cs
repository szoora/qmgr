namespace QMgr.Application;

/// <summary>
/// THE SCHOOL'S CLOCK (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING, B9, 2026-09-26). Every instant is stored in UTC; every
/// DAY a person reads is the branch's own. The Web used <c>DateTime.Today</c> and <c>ToLocalTime()</c>, which on Blazor
/// Server are the SERVER's clock — so "today", the default date of a new event and the day a duty was drawn on were
/// whatever zone the host happened to run in. Both the API and the Web resolve a branch's zone here.
/// </summary>
public static class BranchClock
{
    /// <summary><c>Branch.Timezone</c> is an IANA id ("Africa/Kampala"); an unknown or empty one falls back to UTC.</summary>
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>A stored instant as the school's wall clock. An Unspecified value is treated as UTC, which is how every column here is written.</summary>
    public static DateTime ToLocal(DateTime value, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeFromUtc(value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc), zone);

    public static DateTime Now(TimeZoneInfo zone) => ToLocal(DateTime.UtcNow, zone);

    public static DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(Now(zone));

    /// <summary>A branch-local wall-clock time as UTC; a time that does not exist locally is read as UTC rather than throwing.</summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone); }
        catch (ArgumentException) { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
    }

    /// <summary>True when <paramref name="hour"/> falls in [start, end), wrapping midnight (20 → 6).</summary>
    public static bool InQuietHours(int hour, int startHour, int endHour)
        => startHour == endHour ? false
         : startHour < endHour ? hour >= startHour && hour < endHour
         : hour >= startHour || hour < endHour;
}
