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
/// used to fall through to a zero-entitlement set — which permanently locked branding, exports, and API
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
        var row = await _dbContext.Organizations
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.Settings })
            .FirstOrDefaultAsync();

        if (row == null)
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

        // A platform override, for a negotiated deal. It lives in Organization.Settings rather
        // than a column of its own (the standing enhance-before-add rule) and is applied AFTER the
        // module grants, because its whole purpose is to give somebody something they have not
        // bought. It can only ever turn a flag ON: taking away an entitlement a tenant is paying a
        // module for is a refund question, not a switch.
        features = ApplyPlatformOverrides(features, row.Settings, _logger);

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
            WhiteLabel: false,
            ExportReports: false,
            RemoveAttribution: false,
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
    /// can see). Any active module also turns ShowAds off — ads are what an organization holding no
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
        // Any purchased PRODUCT module counts, including the retired visitor-safeguarding code,
        // which some rows still carry until the split migration has run everywhere. White-Label
        // Plus is deliberately excluded: it is an add-on that removes a line of attribution, and
        // buying it is not buying report exports or an ad-free kiosk.
        var anyModule = activeModules.Any(c => c != ModuleCodes.WhiteLabelPlus);

        return features with
        {
            ApiAccess = features.ApiAccess || integrations,
            WhiteLabel = features.WhiteLabel || engagement,
            // Its own add-on, never WhiteLabel: engagement-communications grants WhiteLabel and
            // every signage tenant buys it, so sharing the flag would give attribution removal
            // away to most of the customer base. See FeatureCodes.RemoveAttribution.
            RemoveAttribution = features.RemoveAttribution || activeModules.Contains(ModuleCodes.WhiteLabelPlus),
            ExportReports = features.ExportReports || anyModule,
            ShowAds = features.ShowAds && !anyModule
        };
    }

    /// <summary>The Organization.Settings key a platform override lives under. One home, read and written.</summary>
    public const string OverridesKey = "FeatureOverrides";

    /// <summary>
    /// Reads <c>Organization.Settings["FeatureOverrides"]</c> — a flat map of feature code to bool.
    /// A malformed blob is read as "no overrides" and logged: an entitlement resolver that throws
    /// takes every gated page in the app down with it.
    /// </summary>
    internal static FeatureFlags ApplyPlatformOverrides(FeatureFlags features, string? organizationSettingsJson, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(organizationSettingsJson)) return features;

        try
        {
            using var doc = JsonDocument.Parse(organizationSettingsJson);
            if (!doc.RootElement.TryGetProperty(OverridesKey, out var overrides) || overrides.ValueKind != JsonValueKind.Object)
                return features;

            bool On(string code) => overrides.TryGetProperty(code, out var v) && v.ValueKind == JsonValueKind.True;

            return features with
            {
                ApiAccess = features.ApiAccess || On(FeatureCodes.ApiAccess),
                WhiteLabel = features.WhiteLabel || On(FeatureCodes.WhiteLabel),
                ExportReports = features.ExportReports || On(FeatureCodes.ExportReports),
                RemoveAttribution = features.RemoveAttribution || On(FeatureCodes.RemoveAttribution)
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Organization {OrganizationId} has a malformed Settings blob; platform feature overrides ignored", features.OrganizationId);
            return features;
        }
    }

    /// <summary>
    /// Drop the cached set. Five minutes is a long time to stare at a switch you just flipped and
    /// watch nothing happen, so every write that changes an entitlement calls this.
    /// </summary>
    public async Task InvalidateCacheAsync(Guid organizationId)
    {
        try
        {
            await _cache.RemoveAsync($"{CachePrefix}{organizationId}");
        }
        catch (Exception ex)
        {
            // A cache that will not forget is a stale answer for five minutes, not a failed write.
            _logger.LogWarning(ex, "Failed to invalidate the feature cache for organization {OrganizationId}", organizationId);
        }
    }

    private static bool GetFeatureValue(FeatureFlags features, string featureCode)
    {
        return featureCode.ToLowerInvariant() switch
        {
            "api_access" => features.ApiAccess,
            "white_label" => features.WhiteLabel,
            "export_reports" => features.ExportReports,
            "remove_attribution" => features.RemoveAttribution,
            _ => features.CustomFeatures.GetValueOrDefault(featureCode, false)
        };
    }

}
