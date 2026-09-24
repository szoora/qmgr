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
    /// Drop an organization's cached entitlement set. The cache is five minutes, so a platform
    /// override set on the Tenant Details dialog would otherwise look broken to the very person
    /// who just set it. Every write that changes what an organization is entitled to calls this.
    /// </summary>
    Task InvalidateCacheAsync(Guid organizationId);

}

/// <summary>
/// Feature flags for an organization
/// </summary>
public record FeatureFlags(
    Guid OrganizationId,
    bool ApiAccess,
    bool WhiteLabel,
    bool ExportReports,
    bool RemoveAttribution,
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

    /// <summary>
    /// Puts the TENANT's name on the copyright line (<c>QCopyright</c>) of its sign-in pages, the shell
    /// footer and every outbound email, in place of ours. The line itself is never hidden.
    ///
    /// A CODE OF ITS OWN, and that is the whole point. The obvious move was to hang this off
    /// <see cref="WhiteLabel"/> — but <c>engagement-communications</c> already grants WhiteLabel,
    /// and every tenant running signage buys that module, so attribution removal would have been
    /// free for most of the customer base on day one. It is granted by its own add-on
    /// (<c>ModuleCodes.WhiteLabelPlus</c>) or by a platform override on the organization.
    ///
    /// Note it cannot ride <c>FeatureFlags.CustomFeatures</c> instead: that dictionary is read by
    /// <c>GetFeatureValue</c> and populated by nothing, anywhere. A real entitlement needs a real
    /// member on the record, a line in <c>ApplyModuleGrants</c> and a case in <c>GetFeatureValue</c>.
    /// </summary>
    public const string RemoveAttribution = "remove_attribution";
}
