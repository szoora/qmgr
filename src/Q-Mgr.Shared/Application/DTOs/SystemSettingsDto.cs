namespace QMgr.Application.DTOs;

/// <summary>
/// Backs the /admin/settings page. Deliberately excludes display theme (owned by
/// OrganizationBrandingDto/BrandingSettings.razor) and SMS/Email/Push notification toggles
/// (owned by NotificationSettings/NotificationSettings.razor) — those already have a real home
/// elsewhere; duplicating them here would recreate the disconnected-copies problem this DTO
/// exists to fix. Uses `set` (not `init`) throughout — the Web project two-way-binds these
/// directly to form controls via @bind, which requires settable properties.
/// </summary>
public class SystemSettingsDto
{
    // General
    public string OrganizationName { get; set; } = string.Empty;
    public string Language { get; set; } = "English";
    public string TimeZone { get; set; } = "UTC";
    public string DateFormat { get; set; } = "MM/DD/YYYY";

    // Queue
    public string TokenFormat { get; set; } = "A001";
    public DateTime? ResetTime { get; set; }
    public int MaxQueueSize { get; set; }
    public bool AutoCallNext { get; set; }
    public bool AllowTransfer { get; set; } = true;

    // Display (VoiceLanguage lives on BranchSettings itself, not in this blob)
    public bool PlaySound { get; set; } = true;
    public bool ShowWaitTime { get; set; } = true;
    public int TokensToDisplay { get; set; } = 5;

    // Security — persisted for real, but NOT currently enforced anywhere in the auth
    // pipeline (no 2FA challenge, no password-expiry check, no audit log writer exist yet).
    // Saving these no longer lies about taking effect, but they don't do anything downstream
    // until that enforcement is actually built — same disclosed-gap pattern as Platform
    // Settings' Stripe/MobileMoney categories.
    public int SessionTimeout { get; set; } = 30;
    public bool TwoFactorEnabled { get; set; }
    public int PasswordExpiry { get; set; } = 90;
    public bool AuditLogging { get; set; } = true;
}

public class SystemSettingsResponseDto
{
    public SystemSettingsDto Settings { get; set; } = new();
    public string VoiceLanguage { get; set; } = "en-US";
}

public class UpdateSystemSettingsRequest
{
    public SystemSettingsDto Settings { get; set; } = new();
    public string VoiceLanguage { get; set; } = "en-US";
}
