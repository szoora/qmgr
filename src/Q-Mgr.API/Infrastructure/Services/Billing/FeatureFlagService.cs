using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using System.Text.Json;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// Service for checking feature availability based on subscription plan
/// </summary>
public class FeatureFlagService : IFeatureFlagService
{
    private readonly QMgrDbContext _dbContext;
    private readonly IDistributedCache _cache;
    private readonly ILogger<FeatureFlagService> _logger;
    private const string CachePrefix = "features:";
    private const int CacheExpirationMinutes = 5;

    public FeatureFlagService(
        QMgrDbContext dbContext,
        IDistributedCache cache,
        ILogger<FeatureFlagService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool> IsFeatureEnabledAsync(Guid organizationId, string featureCode)
    {
        var features = await GetFeaturesAsync(organizationId);
        return GetFeatureValue(features, featureCode);
    }

    public async Task<FeatureFlags> GetFeaturesAsync(Guid organizationId)
    {
        // Try cache first
        var cacheKey = $"{CachePrefix}{organizationId}";
        try
        {
            var cached = await _cache.GetStringAsync(cacheKey);
            if (cached != null)
            {
                var cachedFeatures = JsonSerializer.Deserialize<FeatureFlags>(cached);
                if (cachedFeatures != null)
                    return cachedFeatures;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read features from cache for organization {OrganizationId}", organizationId);
        }

        // Get from database
        var organization = await _dbContext.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == organizationId);

        if (organization == null)
        {
            return GetFreeTierFeatures(organizationId);
        }

        var subscription = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      (s.Status == SubscriptionStatus.Active ||
                                       s.Status == SubscriptionStatus.Trialing));

        FeatureFlags features;
        if (subscription?.Plan != null)
        {
            features = BuildFeaturesFromPlan(organizationId, organization.Tier, subscription.Plan);
        }
        else
        {
            features = GetFreeTierFeatures(organizationId);
        }

        // Cache the result
        try
        {
            await _cache.SetStringAsync(
                cacheKey,
                JsonSerializer.Serialize(features),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheExpirationMinutes)
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache features for organization {OrganizationId}", organizationId);
        }

        return features;
    }

    public async Task<Dictionary<string, bool>> CheckFeaturesAsync(Guid organizationId, params string[] featureCodes)
    {
        var features = await GetFeaturesAsync(organizationId);
        return featureCodes.ToDictionary(code => code, code => GetFeatureValue(features, code));
    }

    public async Task<bool> HasMinimumTierAsync(Guid organizationId, string requiredTier)
    {
        var organization = await _dbContext.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == organizationId);

        if (organization == null)
            return false;

        var currentTierOrder = GetTierOrder(organization.Tier);
        var requiredTierOrder = GetTierOrder(ParseTier(requiredTier));

        return currentTierOrder >= requiredTierOrder;
    }

    private static FeatureFlags GetFreeTierFeatures(Guid organizationId)
    {
        return new FeatureFlags(
            OrganizationId: organizationId,
            Tier: TenantTier.Free.ToString(),
            ApiAccess: false,
            SmsNotifications: false,
            EmailNotifications: false,
            PushNotifications: false,
            CustomBranding: false,
            WhiteLabel: false,
            AdvancedAnalytics: false,
            ExportReports: false,
            MultipleDisplays: false,
            CustomServiceTypes: false,
            PrioritySupport: false,
            DedicatedSchema: false,
            WebhookIntegration: false,
            ShowAds: true,
            CustomFeatures: new Dictionary<string, bool>());
    }

    private static FeatureFlags BuildFeaturesFromPlan(
        Guid organizationId,
        TenantTier tier,
        Domain.Entities.Billing.SubscriptionPlan plan)
    {
        // Parse custom features from plan JSON
        var customFeatures = new Dictionary<string, bool>();
        if (!string.IsNullOrEmpty(plan.Features))
        {
            try
            {
                customFeatures = JsonSerializer.Deserialize<Dictionary<string, bool>>(plan.Features)
                                 ?? new Dictionary<string, bool>();
            }
            catch
            {
                // Ignore parse errors
            }
        }

        // Base features by tier
        var (apiAccess, sms, email, push, branding, whiteLabel, analytics, export, displays, serviceTypes, support, dedicated, webhook) = tier switch
        {
            TenantTier.Free => (false, false, false, false, false, false, false, false, false, false, false, false, false),
            TenantTier.Starter => (true, false, true, false, false, false, false, true, false, true, false, false, true),
            TenantTier.Professional => (true, true, true, true, true, false, true, true, true, true, false, false, true),
            TenantTier.Enterprise => (true, true, true, true, true, true, true, true, true, true, true, true, true),
            _ => (false, false, false, false, false, false, false, false, false, false, false, false, false)
        };

        // Override with custom features from plan if specified
        return new FeatureFlags(
            OrganizationId: organizationId,
            Tier: tier.ToString(),
            ApiAccess: customFeatures.GetValueOrDefault(FeatureCodes.ApiAccess, apiAccess),
            SmsNotifications: customFeatures.GetValueOrDefault(FeatureCodes.SmsNotifications, sms),
            EmailNotifications: customFeatures.GetValueOrDefault(FeatureCodes.EmailNotifications, email),
            PushNotifications: customFeatures.GetValueOrDefault(FeatureCodes.PushNotifications, push),
            CustomBranding: customFeatures.GetValueOrDefault(FeatureCodes.CustomBranding, branding),
            WhiteLabel: customFeatures.GetValueOrDefault(FeatureCodes.WhiteLabel, whiteLabel),
            AdvancedAnalytics: customFeatures.GetValueOrDefault(FeatureCodes.AdvancedAnalytics, analytics),
            ExportReports: customFeatures.GetValueOrDefault(FeatureCodes.ExportReports, export),
            MultipleDisplays: customFeatures.GetValueOrDefault(FeatureCodes.MultipleDisplays, displays),
            CustomServiceTypes: customFeatures.GetValueOrDefault(FeatureCodes.CustomServiceTypes, serviceTypes),
            PrioritySupport: customFeatures.GetValueOrDefault(FeatureCodes.PrioritySupport, support),
            DedicatedSchema: customFeatures.GetValueOrDefault(FeatureCodes.DedicatedSchema, dedicated) || plan.RequiresDedicatedSchema,
            WebhookIntegration: customFeatures.GetValueOrDefault(FeatureCodes.WebhookIntegration, webhook),
            ShowAds: plan.ShowAds,
            CustomFeatures: customFeatures);
    }

    private static bool GetFeatureValue(FeatureFlags features, string featureCode)
    {
        return featureCode.ToLowerInvariant() switch
        {
            "api_access" => features.ApiAccess,
            "sms_notifications" => features.SmsNotifications,
            "email_notifications" => features.EmailNotifications,
            "push_notifications" => features.PushNotifications,
            "custom_branding" => features.CustomBranding,
            "white_label" => features.WhiteLabel,
            "advanced_analytics" => features.AdvancedAnalytics,
            "export_reports" => features.ExportReports,
            "multiple_displays" => features.MultipleDisplays,
            "custom_service_types" => features.CustomServiceTypes,
            "priority_support" => features.PrioritySupport,
            "dedicated_schema" => features.DedicatedSchema,
            "webhook_integration" => features.WebhookIntegration,
            _ => features.CustomFeatures.GetValueOrDefault(featureCode, false)
        };
    }

    private static int GetTierOrder(TenantTier tier)
    {
        return tier switch
        {
            TenantTier.Free => 0,
            TenantTier.Starter => 1,
            TenantTier.Professional => 2,
            TenantTier.Enterprise => 3,
            _ => 0
        };
    }

    private static TenantTier ParseTier(string tier)
    {
        return tier.ToLowerInvariant() switch
        {
            "free" => TenantTier.Free,
            "starter" => TenantTier.Starter,
            "professional" or "pro" => TenantTier.Professional,
            "enterprise" => TenantTier.Enterprise,
            _ => TenantTier.Free
        };
    }
}
