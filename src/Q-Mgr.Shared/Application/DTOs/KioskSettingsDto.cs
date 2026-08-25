namespace QMgr.API.Application.DTOs;

/// <summary>
/// Kiosk-specific settings stored as JSON in BranchSettings
/// </summary>
public class KioskSettingsDto
{
    /// <summary>
    /// Time in seconds before the ticket modal auto-closes and returns to kiosk home
    /// </summary>
    public int TicketDisplayTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Time in seconds of inactivity before kiosk resets to home (future use)
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Whether to show a countdown timer on the ticket modal
    /// </summary>
    public bool ShowCountdown { get; set; } = true;

    /// <summary>
    /// Whether to auto-print the ticket before redirecting (if not already printed)
    /// </summary>
    public bool AutoPrintBeforeRedirect { get; set; } = false;
}
