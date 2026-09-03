using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using System.Text.Json;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// Service for checking feature availability. Resolves from two sources and ORs them together:
/// the legacy tier/plan path (<c>Organization.Tier</c> + <c>Subscription.Plan.Features</c>) and
/// the purchased-module path (<see cref="IModuleAccessService"/>). A module-only tenant (every
/// new registration since the modular subscription system) has no tier subscription at all and
/// used to fall through to free-tier flags — which permanently locked branding, exports, and API
/// access for them regardless of what they'd paid for.
/// </summary>
public class FeatureFlagService : IFeatureFlagService
{
    private readonly QMgrDbContext _dbContext;
    private readonly IDistributedCache _cache;
    private readonly IModuleAccessService _moduleAccess;
    private readonly ILogger<FeatureFlagService> _logger;
    private const string CachePrefix = "features:";
    private const int CacheExpirationMinutes = 5;

    public FeatureFlagService(
        QMgrDbContext dbContext,
        IDistributedCache cache,
        IModuleAccessService moduleAccess,
        ILogger<FeatureFlagService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _moduleAccess = moduleAccess;
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

        // OR-in whatever the org's purchased modules grant. Reads through IModuleAccessService's
        // own cache (invalidated on every grant/revoke/activate), so the only staleness window is
        // this service's 5-minute "features:" entry — see the note on InvalidateCacheAsync below.
        try
        {
            var activeModules = await _moduleAccess.GetActiveModuleCodesAsync(organizationId);
            features = ApplyModuleGrants(features, activeModules);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve active modules for organization {OrganizationId}; using tier/plan features only", organizationId);
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

    /// <summary>
    /// Module → feature-flag mapping. Purely additive: a module can only turn a flag ON, never
    /// off, so a legacy tier org keeps everything its tier already grants. The mapping follows
    /// what each module actually sells (see <c>ModuleCodes</c> doc comments):
    ///
    ///   core-queue (Live Queue Board, Counters, Tokens, Queue/Counter reports)
    ///       → ExportReports, CustomServiceTypes, MultipleDisplays
    ///   engagement-communications (Digital Signage, Campaign Marketing, Feedback &amp; Surveys)
    ///       → CustomBranding, WhiteLabel, AdvancedAnalytics, MultipleDisplays,
    ///         EmailNotifications, SmsNotifications, PushNotifications, ExportReports
    ///   integrations-api (API Clients, webhooks, partner adapters)
    ///       → ApiAccess, WebhookIntegration, ExportReports
    ///   visitor-safeguarding (Visitor Management, Roster, Welfare Ledger)
    ///       → ExportReports only (its own controllers are gated by [RequireModule], not flags;
    ///         the visitor-log CSV export is a ReportsExport permission + this flag)
    ///
    /// Any active or trialing module → ExportReports (a paying tenant can always export what it
    /// can see). Any active module also turns ShowAds off — ads are the free-tier trade-off, not
    /// something a paying module customer should see. PrioritySupport and DedicatedSchema stay
    /// tier/plan-only: neither is a purchasable module feature.
    /// </summary>
    private static FeatureFlags ApplyModuleGrants(FeatureFlags features, IReadOnlyCollection<string> activeModules)
    {
        if (activeModules.Count == 0)
            return features;

        var coreQueue = activeModules.Contains(ModuleCodes.CoreQueue);
        var engagement = activeModules.Contains(ModuleCodes.EngagementCommunications);
        var integrations = activeModules.Contains(ModuleCodes.IntegrationsApi);
        var anyModule = coreQueue || engagement || integrations || activeModules.Contains(ModuleCodes.VisitorSafeguarding);

        return features with
        {
            ApiAccess = features.ApiAccess || integrations,
            WebhookIntegration = features.WebhookIntegration || integrations,
            SmsNotifications = features.SmsNotifications || engagement,
            EmailNotifications = features.EmailNotifications || engagement,
            PushNotifications = features.PushNotifications || engagement,
            CustomBranding = features.CustomBranding || engagement,
            WhiteLabel = features.WhiteLabel || engagement,
            AdvancedAnalytics = features.AdvancedAnalytics || engagement,
            MultipleDisplays = features.MultipleDisplays || engagement || coreQueue,
            CustomServiceTypes = features.CustomServiceTypes || coreQueue,
            ExportReports = features.ExportReports || anyModule,
            ShowAds = features.ShowAds && !anyModule
        };
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
