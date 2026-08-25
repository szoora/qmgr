using System.Text.Json;

namespace QMgr.API.Domain.Entities;

/// <summary>
/// Unified platform configuration storing all platform-level settings as JSON
/// This consolidates PasswordPolicy, PlatformSettings, and other configurations
/// </summary>
public class PlatformConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Configuration category/group name (e.g., "Security", "Email", "Billing", "General")
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Settings stored as JSON string for flexibility
    /// </summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>
    /// Description of this configuration group
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this configuration is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Metadata
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>
    /// Deserialize settings from JSON
    /// </summary>
    public T GetSettings<T>() where T : class, new()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(SettingsJson) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    /// <summary>
    /// Serialize settings to JSON
    /// </summary>
    public void SetSettings<T>(T settings) where T : class
    {
        SettingsJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}

/// <summary>
/// Security settings including password policy, MFA, session timeout, etc.
/// </summary>
public class SecuritySettings
{
    // Password Policy
    public PasswordPolicySettings PasswordPolicy { get; set; } = new();

    // Session Settings
    public SessionSettings Session { get; set; } = new();

    // Multi-Factor Authentication
    public MfaSettings Mfa { get; set; } = new();
}

public class PasswordPolicySettings
{
    public int MinimumLength { get; set; } = 12;
    public int MaximumLength { get; set; } = 128;
    public bool RequireUppercase { get; set; } = true;
    public bool RequireLowercase { get; set; } = true;
    public bool RequireDigits { get; set; } = true;
    public bool RequireSpecialCharacters { get; set; } = true;
    public string AllowedSpecialCharacters { get; set; } = "!@#$%^&*()_+-=[]{}|;:,.<>?";
    public bool PreventCommonPasswords { get; set; } = true;
    public bool PreventUserInfoInPassword { get; set; } = true;
    public int MinimumUniqueCharacters { get; set; } = 4;
    public bool EnablePasswordHistory { get; set; } = true;
    public int PasswordHistoryCount { get; set; } = 5;
    public bool EnablePasswordExpiry { get; set; } = false;
    public int PasswordExpiryDays { get; set; } = 90;
    public bool EnableAccountLockout { get; set; } = true;
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 30;
}

public class SessionSettings
{
    public int AccessTokenExpiryMinutes { get; set; } = 60;
    public int RefreshTokenExpiryDays { get; set; } = 7;
    public int MaxActiveSessions { get; set; } = 5;
    public bool AllowConcurrentSessions { get; set; } = true;
    public int IdleTimeoutMinutes { get; set; } = 30;
}

public class MfaSettings
{
    public bool EnableMfa { get; set; } = false;
    public bool MfaRequired { get; set; } = false;
    public List<string> AllowedMfaMethods { get; set; } = new() { "totp", "email" };
}

/// <summary>
/// Email/SMTP settings
/// </summary>
public class EmailSettings
{
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = "Q-Mgr";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>
/// Billing and subscription settings
/// </summary>
public class BillingSettings
{
    public string Currency { get; set; } = "USD";
    public bool EnableTrials { get; set; } = true;
    public int TrialDurationDays { get; set; } = 14;
    public bool RequirePaymentMethod { get; set; } = false;
    public string StripePublicKey { get; set; } = string.Empty;
    public string StripeSecretKey { get; set; } = string.Empty;
}

/// <summary>
/// General platform settings
/// </summary>
public class GeneralSettings
{
    public string PlatformName { get; set; } = "Q-Mgr";
    public string SupportEmail { get; set; } = "support@qmgr.com";
    public string TermsOfServiceUrl { get; set; } = string.Empty;
    public string PrivacyPolicyUrl { get; set; } = string.Empty;
    public bool MaintenanceMode { get; set; } = false;
    public string? MaintenanceMessage { get; set; }
    public bool AllowSelfRegistration { get; set; } = true;
    public bool RequireEmailVerification { get; set; } = true;
}
