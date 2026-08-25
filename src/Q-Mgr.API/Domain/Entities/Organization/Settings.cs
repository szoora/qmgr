using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Organization;

public class BranchSettings : BaseEntity
{
    public Guid BranchId { get; set; }

    // Kiosk Settings
    public string? DefaultKioskPrinter { get; set; }
    public int KioskTimeBetweenSlides { get; set; } = 5000; // milliseconds
    public int KioskScrollerSpeed { get; set; } = 50;

    /// <summary>
    /// JSON-serialized kiosk settings for flexible configuration
    /// Contains: TicketDisplayTimeoutSeconds, IdleTimeoutSeconds, ShowCountdown, AutoPrintBeforeRedirect
    /// </summary>
    public string? KioskSettingsJson { get; set; }

    // Printer Settings
    public PrintMethod PreferredPrintMethod { get; set; } = PrintMethod.BrowserPrint;
    public PrinterType PrinterType { get; set; } = PrinterType.Thermal;
    public string? PrinterName { get; set; }
    public string? PrinterIpAddress { get; set; }
    public int PrinterPort { get; set; } = 9100;
    public int ThermalPaperWidth { get; set; } = 80; // 58mm or 80mm
    public bool PrintLogo { get; set; } = true;
    public string? PrintLogoUrl { get; set; }
    public bool PrintQrCode { get; set; } = true;
    public bool PrintFeedbackUrl { get; set; } = true;
    public string? PrintHeaderText { get; set; }
    public string? PrintFooterText { get; set; }
    public int PrintFontSize { get; set; } = 12;
    public bool AutoPrintOnTokenCreate { get; set; } = false;

    // Display Settings
    public int DisplayTimeBetweenSlides { get; set; } = 5000;
    public bool EnableVoiceAnnouncement { get; set; } = false;
    public string? VoiceLanguage { get; set; } = "en-US";

    /// <summary>
    /// JSON-serialized SystemSettingsDto for the general/queue/display-misc/security fields on
    /// the /admin/settings page that have no other real home (see SystemSettingsController.cs).
    /// Deliberately does NOT include: DisplayTheme (owned by Organization.DisplayTheme, edited
    /// in BrandingSettings.razor) or SMS/Email/Push notification toggles (owned by
    /// NotificationSettings, edited in NotificationSettings.razor) — those already have a real,
    /// correctly-wired home elsewhere, and duplicating them here would just recreate the
    /// two-disconnected-copies bug this was built to fix in the first place.
    /// </summary>
    public string? SystemSettingsJson { get; set; }

    // Display Banner (ticker/marquee shown on CustomerDisplay + SignageDisplay) — opt-in
    public bool DisplayBannerEnabled { get; set; } = false;

    /// <summary>
    /// JSON-serialized DisplayBannerSettingsDto: Position, Direction, SpeedSeconds,
    /// BackgroundColor, TextColor, Messages. Kept as a flexible blob (matching
    /// KioskSettingsJson's pattern) since Messages is a list, not a scalar column.
    /// </summary>
    public string? DisplayBannerSettingsJson { get; set; }

    // Queue Settings
    public bool EnableSmsNotification { get; set; } = false;
    public bool EnableEmailNotification { get; set; } = false;
    public int TokenExpiryHours { get; set; } = 24;
    public bool ResetTokenNumbersDaily { get; set; } = true;

    // Notification Templates
    public string? SmsTemplateTokenCreated { get; set; }
    public string? SmsTemplateTokenCalled { get; set; }
    public string? EmailTemplateTokenCreated { get; set; }

    // Navigation
    public virtual Branch? Branch { get; set; }
}
