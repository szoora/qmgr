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

}

/// <summary>
/// Feature flags for an organization
/// </summary>
public record FeatureFlags(
    Guid OrganizationId,
    bool ApiAccess,
    bool WhiteLabel,
    bool ExportReports,
    bool ShowAds,
    Dictionary<string, bool> CustomFeatures);

/// <summary>
/// Feature codes used throughout the application
/// </summary>
public static class FeatureCodes
{
    public const string ApiAccess = "api_access";
    public const string WhiteLabel = "white_label";
    public const string ExportReports = "export_reports";
}
