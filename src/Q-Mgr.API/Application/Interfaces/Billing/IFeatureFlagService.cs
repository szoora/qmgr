namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Service for checking feature availability based on subscription plan
/// </summary>
public interface IFeatureFlagService
{
    /// <summary>
    /// Check if a specific feature is enabled for an organization
    /// </summary>
    Task<bool> IsFeatureEnabledAsync(Guid organizationId, string featureCode);

    /// <summary>
    /// Get all feature flags for an organization
    /// </summary>
    Task<FeatureFlags> GetFeaturesAsync(Guid organizationId);

    /// <summary>
    /// Check multiple features at once
    /// </summary>
    Task<Dictionary<string, bool>> CheckFeaturesAsync(Guid organizationId, params string[] featureCodes);

    /// <summary>
    /// Check if organization can access a specific tier feature
    /// </summary>
    Task<bool> HasMinimumTierAsync(Guid organizationId, string requiredTier);
}

/// <summary>
/// Feature flags for an organization
/// </summary>
public record FeatureFlags(
    Guid OrganizationId,
    string Tier,
    bool ApiAccess,
    bool SmsNotifications,
    bool EmailNotifications,
    bool PushNotifications,
    bool CustomBranding,
    bool WhiteLabel,
    bool AdvancedAnalytics,
    bool ExportReports,
    bool MultipleDisplays,
    bool CustomServiceTypes,
    bool PrioritySupport,
    bool DedicatedSchema,
    bool WebhookIntegration,
    bool ShowAds,
    Dictionary<string, bool> CustomFeatures);

/// <summary>
/// Feature codes used throughout the application
/// </summary>
public static class FeatureCodes
{
    public const string ApiAccess = "api_access";
    public const string SmsNotifications = "sms_notifications";
    public const string EmailNotifications = "email_notifications";
    public const string PushNotifications = "push_notifications";
    public const string CustomBranding = "custom_branding";
    public const string WhiteLabel = "white_label";
    public const string AdvancedAnalytics = "advanced_analytics";
    public const string ExportReports = "export_reports";
    public const string MultipleDisplays = "multiple_displays";
    public const string CustomServiceTypes = "custom_service_types";
    public const string PrioritySupport = "priority_support";
    public const string DedicatedSchema = "dedicated_schema";
    public const string WebhookIntegration = "webhook_integration";
}
