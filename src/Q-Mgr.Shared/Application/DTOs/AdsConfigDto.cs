namespace QMgr.Application.DTOs;

/// <summary>
/// Safe-to-expose-publicly subset of PlatformSetting's Ads category, served anonymously to
/// public kiosk/display screens — same shape as OrganizationBrandingDto's split from the
/// SuperAdmin-only PlatformSettingsController. ShouldShowAds already folds in both the org's
/// entitlement (IFeatureFlagService.ShowAds, tier/plan-based) and the platform-wide
/// ShowAdsOnFreePlan toggle, so callers don't need to know either rule.
/// </summary>
public record AdsConfigDto
{
    public bool ShouldShowAds { get; set; }
    public string Provider { get; set; } = "internal";
    public string GoogleAdSenseClientId { get; set; } = string.Empty;
}
