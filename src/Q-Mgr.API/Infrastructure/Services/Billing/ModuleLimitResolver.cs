using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>See <see cref="IModuleLimitResolver"/>.</summary>
public class ModuleLimitResolver : IModuleLimitResolver
{
    private readonly QMgrDbContext _dbContext;
    private readonly IFeatureFlagService _featureFlags;

    public ModuleLimitResolver(QMgrDbContext dbContext, IFeatureFlagService featureFlags)
    {
        _dbContext = dbContext;
        _featureFlags = featureFlags;
    }

    public Task<EffectiveLimits> ResolveAsync(Guid organizationId) => ResolveLimitsAsync(organizationId);

    /// <summary>
    /// What an organization is actually allowed, derived from the modules it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each numeric limit is the <em>highest</em> value among the modules the organization holds,
    /// so buying a module can raise a limit and can never lower one. Before 2026-09-04 these came
    /// from a single tier plan; every module row has carried its own seven limits since the catalog
    /// was built, and nothing read them until now.
    /// </para>
    /// <para>
    /// A per-tenant override on the billing account still wins over the derived figure — that is
    /// how a platform administrator grants one customer more room without selling them a module.
    /// Feature booleans come from <see cref="IFeatureFlagService"/> rather than being re-derived
    /// here, so the module-to-feature mapping keeps exactly one home.
    /// </para>
    /// </remarks>
    private async Task<EffectiveLimits> ResolveLimitsAsync(Guid organizationId)
    {
        var held = await _dbContext.OrganizationModules
            .AsNoTracking()
            .Where(om => om.OrganizationId == organizationId &&
                         (om.Status == OrganizationModuleStatus.Active ||
                          om.Status == OrganizationModuleStatus.Trialing))
            .Select(om => new
            {
                om.Module!.MaxBranches,
                om.Module.MaxUsersPerBranch,
                om.Module.MaxCountersPerBranch,
                om.Module.MaxTokensPerMonth,
                om.Module.MaxApiCallsPerMonth,
                om.Module.MaxStorageMb
            })
            .ToListAsync();

        var features = await _featureFlags.GetFeaturesAsync(organizationId);

        if (held.Count == 0)
        {
            // Holding nothing is not the same as holding a free plan: route gating already keeps
            // such an organization out of every module's pages. These floors only exist so the
            // handful of ungated screens have finite numbers to render rather than zeroes.
            return NoModulesHeld with
            {
                HasApiAccess = features.ApiAccess,
                HasSmsNotifications = features.SmsNotifications,
                HasCustomBranding = features.CustomBranding,
                HasAdvancedAnalytics = features.AdvancedAnalytics,
                ShowAds = features.ShowAds
            };
        }

        var account = await _dbContext.Subscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      (s.Status == SubscriptionStatus.Active ||
                                       s.Status == SubscriptionStatus.Trialing ||
                                       s.Status == SubscriptionStatus.PastDue));

        var branches = held.Max(h => h.MaxBranches);
        var users = held.Max(h => h.MaxUsersPerBranch);
        var counters = held.Max(h => h.MaxCountersPerBranch);
        var tokens = held.Max(h => h.MaxTokensPerMonth);
        var apiCalls = held.Max(h => h.MaxApiCallsPerMonth);
        var storage = held.Max(h => h.MaxStorageMb);

        return new EffectiveLimits(
            MaxBranches: account?.MaxBranchesOverride ?? branches,
            MaxUsersPerBranch: account?.MaxUsersOverride ?? users,
            MaxCountersPerBranch: counters,
            MaxTokensPerMonth: account?.MaxTokensOverride ?? tokens,
            MaxApiCallsPerMonth: account?.MaxApiCallsOverride ?? apiCalls,
            MaxStorageMb: account?.MaxStorageOverride ?? storage,
            HasApiAccess: features.ApiAccess,
            HasSmsNotifications: features.SmsNotifications,
            HasCustomBranding: features.CustomBranding,
            HasAdvancedAnalytics: features.AdvancedAnalytics,
            ShowAds: features.ShowAds);
    }

    /// <summary>The floor applied to an organization holding no module at all.</summary>
    private static readonly EffectiveLimits NoModulesHeld = new(
        MaxBranches: 1,
        MaxUsersPerBranch: 2,
        MaxCountersPerBranch: 2,
        MaxTokensPerMonth: 0,
        MaxApiCallsPerMonth: 0,
        MaxStorageMb: 100,
        HasApiAccess: false,
        HasSmsNotifications: false,
        HasCustomBranding: false,
        HasAdvancedAnalytics: false,
        ShowAds: true);
}
