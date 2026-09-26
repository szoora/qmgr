namespace QMgr.Application.DTOs;

/// <summary>
/// How one person likes the app to behave, stored in <c>Users.UiPreferences</c> and read and written only through
/// <c>IUserPreferencesService</c> (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E8, 2026-09-26). It follows the PERSON
/// to their phone; "mute on this device" is the one choice that belongs to the device and stays in the browser.
///
/// Every property has a default that is right for somebody who has never opened a settings page, and a save copies
/// the record whole (<c>with</c>), so a property added later survives a save made by an older page.
/// </summary>
public record UserUiPreferencesDto
{
    public CalendarViewPreferencesDto Calendar { get; set; } = new();
    public NotificationSoundPreferencesDto Sound { get; set; } = new();
}

public record CalendarViewPreferencesDto
{
    /// <summary>"term" | "month" | "agenda".</summary>
    public string View { get; set; } = "term";

    /// <summary>"mine" (events for me, mine to run, my duties) | "all" (the whole school's calendar). Mine by default (E4).</summary>
    public string Scope { get; set; } = CalendarScopes.Mine;

    /// <summary>Past events in the Term and Agenda lists. Off by default (E1): a list's job is "what is coming".</summary>
    public bool ShowPast { get; set; }

    public bool ShowDuties { get; set; } = true;
}

public static class CalendarScopes
{
    public const string Mine = "mine";
    public const string All = "all";
    public static string Normalize(string? scope) => string.Equals(scope, All, StringComparison.OrdinalIgnoreCase) ? All : Mine;
}

public record NotificationSoundPreferencesDto
{
    /// <summary>A short chime when a new notification arrives. On by default (E7); always with a visible toast.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Chime only for High and Urgent notifications.</summary>
    public bool ImportantOnly { get; set; }
}

/// <summary>
/// What the Web needs to decide, in the browser, whether to chime right now: the person's choice, and the school's quiet
/// hours in the branch's own clock. Served with the preferences so the chime needs no second call.
/// </summary>
public record UserUiPreferencesEnvelopeDto
{
    public UserUiPreferencesDto Preferences { get; init; } = new();
    public QuietHoursDto? QuietHours { get; init; }
    /// <summary>The branch's time zone id, so quiet hours are read on the school's clock, not the server's.</summary>
    public string? TimeZone { get; init; }
}
