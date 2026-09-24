using QMgr.Application.Branding;
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

/// <summary>
/// What a password must be. EVERY field here is editable by a platform administrator in
/// Platform Settings > Security; these are only the values a NEW install starts with.
///
/// <para><b>FOUR CHARACTERS IS A DELIBERATE PRODUCT DECISION (user, 2026-09-22), and it is well
/// below what any standard recommends</b> — NIST SP 800-63B puts the floor at 8. It is recorded
/// here rather than argued: the people typing these are school staff, many of whom have no email
/// address and are handed a temporary password on a printed slip, and the product owner judged the
/// friction of a long password to be the larger real-world risk. What protects the account in
/// practice is the lockout below (five attempts, thirty minutes), which stops online guessing; the
/// exposure a short password leaves is OFFLINE cracking if the password hashes ever leak, and
/// BCrypt is the only thing standing in the way of that.</para>
///
/// <para><b>Composition rules follow NIST SP 800-63B, which is why they are off.</b> That
/// guidance is explicit that composition rules — one capital, one digit, one symbol — should NOT be
/// imposed: they push people towards predictable substitutions (Password1!), towards writing the
/// result down, and towards reusing the one string that satisfied every site. Length plus a
/// blocklist of known-breached and obvious passwords is what actually helps, and both are kept.</para>
///
/// <para>So: <b>4 characters, no composition rules, and the two checks that REFUSE a bad password
/// rather than shaping a good one</b> — common passwords and the user's own name. Before 2026-09-22
/// this was 12 characters plus all four composition rules, which is four more hoops for a member of
/// staff with no email address who is being handed a temporary password on a printed slip.</para>
///
/// <para><b>An existing install keeps whatever it has.</b> These defaults are read only when the
/// PlatformSettings row is first created — the seeder returns early once any row exists — so a
/// server already running is unchanged and its administrator decides. That is deliberate: silently
/// relaxing a live security policy on deploy would be the wrong thing to do even when the new value
/// is better.</para>
/// </summary>
public class PasswordPolicySettings
{
    public int MinimumLength { get; set; } = 4;
    public int MaximumLength { get; set; } = 128;

    // Off by default, per NIST SP 800-63B. Still available to any administrator whose own policy or
    // insurer requires them.
    public bool RequireUppercase { get; set; } = false;
    public bool RequireLowercase { get; set; } = false;
    public bool RequireDigits { get; set; } = false;
    public bool RequireSpecialCharacters { get; set; } = false;
    public string AllowedSpecialCharacters { get; set; } = "!@#$%^&*()_+-=[]{}|;:,.<>?";
    public bool PreventCommonPasswords { get; set; } = true;
    public bool PreventUserInfoInPassword { get; set; } = true;
    // MUST NOT exceed MinimumLength, or the two rules contradict each other: at 4 and 4, every
    // character of a 4-character password has to be distinct, so "1234" passes and "1122" does not
    // — and the form would say "at least 4 characters" while the server refused one. That is the
    // lying-form bug this file's siblings were just fixed for. 1 means "no such rule".
    public int MinimumUniqueCharacters { get; set; } = 1;
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
    public string SenderName { get; set; } = ProductBrand.Name;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>
/// Billing and subscription settings
/// </summary>
public class BillingSettings
{
    public string Currency { get; set; } = "UGX";
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
    public string PlatformName { get; set; } = ProductBrand.Name;
    public string SupportEmail { get; set; } = "support@qmgr.com";
    public string TermsOfServiceUrl { get; set; } = string.Empty;
    public string PrivacyPolicyUrl { get; set; } = string.Empty;
    public bool MaintenanceMode { get; set; } = false;
    public string? MaintenanceMessage { get; set; }
    public bool AllowSelfRegistration { get; set; } = true;
    public bool RequireEmailVerification { get; set; } = true;
}
