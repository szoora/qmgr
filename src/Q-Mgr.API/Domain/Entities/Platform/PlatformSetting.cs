using QMgr.Domain.Common;
using System.Text.Json;

namespace QMgr.Domain.Entities.Platform;

/// <summary>
/// Stores platform-wide configuration settings grouped by category
/// Settings are stored as JSON to avoid creating multiple tables
/// </summary>
public class PlatformSetting : BaseAuditableEntity
{
    /// <summary>
    /// Category of settings (e.g., "JWT", "SaaS", "RateLimiting", "Billing")
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name for this settings group
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Description of what these settings control
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// JSON object containing all settings for this category
    /// Example: {"Secret": "...", "Issuer": "...", "ExpiryMinutes": 60}
    /// </summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>
    /// Whether these settings are currently active/enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether these settings can be edited (some may be locked for security)
    /// </summary>
    public bool IsEditable { get; set; } = true;

    /// <summary>
    /// Display order in admin UI
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Icon name for UI (Bootstrap Icons)
    /// </summary>
    public string? Icon { get; set; }

    #region Helper Methods

    /// <summary>
    /// Deserialize settings JSON to typed object
    /// </summary>
    public T? GetSettings<T>() where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(SettingsJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Serialize typed object to settings JSON
    /// </summary>
    public void SetSettings<T>(T settings) where T : class
    {
        SettingsJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    #endregion
}

#region Setting Models

/// <summary>
/// JWT Authentication Settings
/// </summary>
public class JwtSettings
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 60;
}

/// <summary>
/// CORS Settings
/// </summary>
public class CorsSettings
{
    public List<string> AllowedOrigins { get; set; } = new();
    public bool AllowCredentials { get; set; } = true;
}

/// <summary>
/// Rate Limiting Settings
/// </summary>
public class RateLimitSettings
{
    public bool EnableEndpointRateLimiting { get; set; } = true;
    public bool StackBlockedRequests { get; set; } = false;
    public string RealIpHeader { get; set; } = "X-Real-IP";
    public string ClientIdHeader { get; set; } = "X-ClientId";
    public int HttpStatusCode { get; set; } = 429;
    public List<RateLimitRule> GeneralRules { get; set; } = new();
}

public class RateLimitRule
{
    public string Endpoint { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public int Limit { get; set; }
}

/// <summary>
/// SaaS Platform Settings
/// </summary>
public class SaasSettings
{
    public string BaseDomain { get; set; } = "qmgr.app";
    public string BaseUrl { get; set; } = "https://qmgr.app";
    public int TrialDays { get; set; } = 14;
    public string DefaultPlanCode { get; set; } = "free";
    public bool AllowCustomDomains { get; set; } = true;
    public bool RequireEmailVerification { get; set; } = true;
    public int MaxOrganizationsPerUser { get; set; } = 5;
}

/// <summary>
/// Stripe Billing Settings
/// </summary>
public class StripeSettings
{
    public string SecretKey { get; set; } = string.Empty;
    public string PublishableKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public bool TestMode { get; set; } = false;
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Mobile Money Integration Settings
/// </summary>
public class MobileMoneySettings
{
    public string CrmApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Advertising Settings
/// </summary>
public class AdsSettings
{
    public string Provider { get; set; } = "internal";
    public string GoogleAdSenseClientId { get; set; } = string.Empty;
    public string InternalAdsApiUrl { get; set; } = "/api/v1/ads";
    public bool ShowAdsOnFreePlan { get; set; } = true;
}

/// <summary>
/// Email/SMTP Settings
/// </summary>
public class EmailSettings
{
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Q-Mgr";
    public bool UseSsl { get; set; } = true;
}

// The "Security" PlatformSetting category (a SecuritySettings class) used to live here but had
// zero real consumers — nothing ever enforced it. The canonical, actually-enforced security
// config is QMgr.API.Domain.Entities.SecuritySettings (PlatformConfiguration category "Security",
// via IPasswordValidationService); PlatformSettingsController special-cases the "Security"
// category to read/write that system instead, so the existing admin UI (same flat field shape)
// now edits real, enforced settings.

#endregion
