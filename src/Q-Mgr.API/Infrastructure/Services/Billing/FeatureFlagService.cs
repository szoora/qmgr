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
/// the modules an organization holds, which since 2026-09-04 is the only source of entitlement, and
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
        var organizationExists = await _dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(o => o.Id == organizationId);

        if (!organizationExists)
        {
            return NoEntitlements(organizationId);
        }

        // Everything an organization is entitled to comes from the modules it holds. Until
        // 2026-09-04 a tier plan supplied a base set that module grants were OR'd on top of; the
        // tier is gone, so the base is now "nothing" and every entitlement is earned by a purchase.
        var features = NoEntitlements(organizationId);

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
            _logger.LogWarning(ex, "Failed to resolve active modules for organization {OrganizationId}; leaving the organization with no entitlements", organizationId);
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

    /// <summary>The starting point for every organization: nothing. Entitlements are added only by
    /// the modules it holds — see <c>ApplyModuleGrants</c>.</summary>
    private static FeatureFlags NoEntitlements(Guid organizationId)
    {
        return new FeatureFlags(
            OrganizationId: organizationId,
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
        // Any purchased module counts, including the retired visitor-safeguarding code, which some
        // rows still carry until the split migration has run everywhere.
        var anyModule = activeModules.Count > 0;

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

}
