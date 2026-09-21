using System.Globalization;

namespace QMgr.Application.DTOs;

/// <summary>
/// The arithmetic of a timetable cycle and its bell schedule (duty rota plan §6.1), in one place for the API, the
/// integrity sweep, lesson materialisation and the Web grid. A "cycle day" is 1-based: in a one-week cycle over
/// Monday–Friday it is Mon=1 … Fri=5; in an A/B cycle Mon A=1 … Fri A=5, Mon B=6 … Fri B=10.
/// </summary>
public static class TimetableCycle
{
    public const int MaxDayTypes = 7;
    public const int MaxPeriodsPerDayType = 20;

    /// <summary>The Ugandan secondary default: Monday to Friday, ten 40-minute periods, a break and lunch (plan §15 decision 6).</summary>
    public static TimetableSettingsDto Defaults() => new()
    {
        CycleWeeks = 1,
        DefaultPeriodMinutes = 40,
        IsSaved = false,
        DayTypes = new()
        {
            new BellDayTypeDto
            {
                Key = "weekday",
                Name = "Monday to Friday",
                Days = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                Periods = new()
                {
                    P("ASM", "Assembly", "07:40", "08:00", BellPeriodKind.Assembly),
                    P("P1", "P1", "08:00", "08:40"), P("P2", "P2", "08:40", "09:20"), P("P3", "P3", "09:20", "10:00"),
                    P("BRK", "Break", "10:00", "10:20", BellPeriodKind.Break),
                    P("P4", "P4", "10:20", "11:00"), P("P5", "P5", "11:00", "11:40"), P("P6", "P6", "11:40", "12:20"), P("P7", "P7", "12:20", "13:00"),
                    P("LUN", "Lunch", "13:00", "14:00", BellPeriodKind.Break),
                    P("P8", "P8", "14:00", "14:40"), P("P9", "P9", "14:40", "15:20"), P("P10", "P10", "15:20", "16:00"),
                }
            }
        }
    };

    private static BellPeriodDto P(string key, string label, string start, string end, BellPeriodKind kind = BellPeriodKind.Lesson)
        => new() { Key = key, Label = label, Start = start, End = end, Kind = kind };

    /// <summary>The match form of a class or room name everywhere in this codebase.</summary>
    public static string Normalize(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    public static TimeOnly? ParseTime(string? value)
        => TimeOnly.TryParseExact((value ?? string.Empty).Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>The teaching weekdays, in week order (Monday first).</summary>
    public static List<DayOfWeek> TeachingDays(TimetableSettingsDto settings)
        => settings.DayTypes.SelectMany(d => d.Days).Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();

    public static int CycleDayCount(TimetableSettingsDto settings)
        => TeachingDays(settings).Count * Math.Clamp(settings.CycleWeeks, 1, 2);

    /// <summary>Weekday and week (0 = A, 1 = B) of a cycle day, or null when out of range.</summary>
    public static (DayOfWeek Day, int Week)? WeekdayOf(TimetableSettingsDto settings, int cycleDays, int cycleDay)
    {
        var days = TeachingDays(settings);
        if (days.Count == 0 || cycleDay < 1 || cycleDay > cycleDays) return null;
        return (days[(cycleDay - 1) % days.Count], (cycleDay - 1) / days.Count);
    }

    public static BellDayTypeDto? DayTypeOf(TimetableSettingsDto settings, int cycleDays, int cycleDay)
        => WeekdayOf(settings, cycleDays, cycleDay) is { } w ? settings.DayTypes.FirstOrDefault(t => t.Days.Contains(w.Day)) : null;

    /// <summary>"Mon" in a one-week cycle, "Mon A" / "Mon B" in two.</summary>
    public static string CycleDayLabel(TimetableSettingsDto settings, int cycleDays, int cycleDay)
    {
        if (WeekdayOf(settings, cycleDays, cycleDay) is not { } w) return $"Day {cycleDay}";
        var name = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(w.Day);
        var weeks = TeachingDays(settings).Count == 0 ? 1 : cycleDays / TeachingDays(settings).Count;
        return weeks > 1 ? $"{name} {(char)('A' + w.Week)}" : name;
    }

    /// <summary>A lesson period on that cycle day, or null when the day's bell schedule has no such teaching period.</summary>
    public static BellPeriodDto? LessonPeriodOn(TimetableSettingsDto settings, int cycleDays, int cycleDay, string periodKey)
        => DayTypeOf(settings, cycleDays, cycleDay)?.Periods.FirstOrDefault(p => p.Kind == BellPeriodKind.Lesson && string.Equals(p.Key, periodKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The grid's rows: every period key any day type uses, ordered by the time it starts on the first day type that
    /// has it. A key missing from one day type (Saturday's short morning) is a slot that day cannot hold.
    /// </summary>
    public static List<BellPeriodDto> Rows(TimetableSettingsDto settings)
    {
        var seen = new Dictionary<string, BellPeriodDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in settings.DayTypes)
            foreach (var period in type.Periods)
                seen.TryAdd(period.Key, period);
        return seen.Values.OrderBy(p => ParseTime(p.Start) ?? TimeOnly.MaxValue).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The ordered teaching periods of one cycle day.</summary>
    public static List<BellPeriodDto> LessonPeriodsOf(TimetableSettingsDto settings, int cycleDays, int cycleDay)
        => (DayTypeOf(settings, cycleDays, cycleDay)?.Periods ?? new())
            .Where(p => p.Kind == BellPeriodKind.Lesson)
            .OrderBy(p => ParseTime(p.Start) ?? TimeOnly.MaxValue)
            .ToList();

    /// <summary>
    /// The cycle day a branch-local date falls on, or null for a non-teaching weekday. Week A is the week (Monday
    /// start) containing <paramref name="effectiveFrom"/>.
    /// </summary>
    public static int? CycleDayOn(TimetableSettingsDto settings, int cycleDays, DateOnly effectiveFrom, DateOnly date)
    {
        var days = TeachingDays(settings);
        var index = days.IndexOf(date.DayOfWeek);
        if (index < 0 || days.Count == 0) return null;
        var weeks = Math.Max(1, cycleDays / days.Count);
        var anchor = effectiveFrom.AddDays(-(((int)effectiveFrom.DayOfWeek + 6) % 7));
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        var weekNumber = (int)Math.Floor((monday.DayNumber - anchor.DayNumber) / 7.0);
        var week = ((weekNumber % weeks) + weeks) % weeks;
        return week * days.Count + index + 1;
    }

    /// <summary>Checks a settings document; null when it is valid.</summary>
    public static string? Validate(TimetableSettingsDto s)
    {
        if (s.CycleWeeks is not (1 or 2)) return "A cycle is one week or two (A/B).";
        if (s.DefaultPeriodMinutes is < 10 or > 180) return "The default period length must be between 10 and 180 minutes.";
        if (s.DayTypes.Count == 0) return "Add at least one day type with its periods.";
        if (s.DayTypes.Count > MaxDayTypes) return $"No more than {MaxDayTypes} day types.";
        var claimed = new HashSet<DayOfWeek>();
        var typeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in s.DayTypes)
        {
            type.Name = (type.Name ?? string.Empty).Trim();
            type.Key = string.IsNullOrWhiteSpace(type.Key) ? Normalize(type.Name).Replace(' ', '-') : type.Key.Trim();
            if (type.Name.Length == 0) return "Every day type needs a name.";
            if (!typeKeys.Add(type.Key)) return $"Two day types share the key '{type.Key}'.";
            if (type.Days.Count == 0) return $"'{type.Name}' covers no days.";
            foreach (var day in type.Days.Distinct())
                if (!claimed.Add(day)) return $"{day} belongs to more than one day type.";
            type.Days = type.Days.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();
            if (type.Periods.Count > MaxPeriodsPerDayType) return $"'{type.Name}' has more than {MaxPeriodsPerDayType} periods.";
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TimeOnly? lastEnd = null;
            foreach (var period in type.Periods.OrderBy(p => ParseTime(p.Start) ?? TimeOnly.MinValue))
            {
                period.Key = (period.Key ?? string.Empty).Trim();
                period.Label = string.IsNullOrWhiteSpace(period.Label) ? period.Key : period.Label.Trim();
                if (period.Key.Length is 0 or > 20) return $"Every period in '{type.Name}' needs a key of up to 20 characters.";
                if (period.Key.IndexOfAny(new[] { ':', ';', ',', ' ' }) >= 0) return $"Period key '{period.Key}' cannot contain spaces or : ; ,";
                if (!keys.Add(period.Key)) return $"'{type.Name}' uses the period key '{period.Key}' twice.";
                if (ParseTime(period.Start) is not { } start || ParseTime(period.End) is not { } end) return $"{period.Label} in '{type.Name}' needs a start and end time (HH:mm).";
                if (end <= start) return $"{period.Label} in '{type.Name}' ends before it starts.";
                if (lastEnd is { } le && start < le) return $"{period.Label} in '{type.Name}' overlaps the period before it.";
                lastEnd = end;
            }
            type.Periods = type.Periods.OrderBy(p => ParseTime(p.Start)).ToList();
        }
        if (!s.DayTypes.Any(t => t.Periods.Any(p => p.Kind == BellPeriodKind.Lesson))) return "The bell schedule has no teaching periods.";

        var cycleDays = CycleDayCount(s);
        foreach (var u in s.Unavailability)
        {
            if (u.UserId == Guid.Empty) return "An unavailability line has no teacher.";
            if (u.CycleDay < 1 || u.CycleDay > cycleDays) return $"An unavailability line names cycle day {u.CycleDay}; the cycle has {cycleDays}.";
            u.PeriodKey = string.IsNullOrWhiteSpace(u.PeriodKey) ? null : u.PeriodKey.Trim();
            if (u.PeriodKey != null && LessonPeriodOn(s, cycleDays, u.CycleDay, u.PeriodKey) == null)
                return $"{CycleDayLabel(s, cycleDays, u.CycleDay)} has no teaching period '{u.PeriodKey}'.";
            u.Note = string.IsNullOrWhiteSpace(u.Note) ? null : u.Note.Trim();
        }
        if (s.Unavailability.Count > 2000) return "Too many unavailability lines.";

        // Preferences are SOFT and never refuse a placement, but they still have to be well formed:
        // the checker reads them on every diagnosis, and a preference naming a cycle day the cycle
        // does not have would raise an issue nobody could ever clear.
        foreach (var p in s.Preferences)
        {
            if (p.UserId == Guid.Empty) return "A teaching preference has no teacher.";
            if (p.MaxConsecutivePeriods is < 1 or > 12) return "Maximum consecutive periods must be between 1 and 12.";
            if (p.PreferredLightCycleDay is { } d && (d < 1 || d > cycleDays))
                return $"A teaching preference names cycle day {d}; the cycle has {cycleDays}.";
            p.PreferredRooms = p.PreferredRooms
                .Select(r => r?.Trim() ?? string.Empty)
                .Where(r => r.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }
        if (s.Preferences.Count > 500) return "Too many teaching preferences.";
        if (s.Preferences.GroupBy(p => p.UserId).Any(g => g.Count() > 1))
            return "A teacher has more than one set of teaching preferences.";

        return null;
    }
}
