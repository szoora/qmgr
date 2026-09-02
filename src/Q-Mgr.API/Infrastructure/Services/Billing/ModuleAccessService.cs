using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using System.Text.Json;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// See <see cref="IModuleAccessService"/>. Caching mirrors <c>FeatureFlagService</c>'s own
/// pattern (<see cref="IDistributedCache"/>, 5-minute TTL, invalidate on any write).
/// </summary>
public class ModuleAccessService : IModuleAccessService
{
    private readonly QMgrDbContext _dbContext;
    private readonly IDistributedCache _cache;
    private readonly IStripeService _stripeService;
    private readonly ILogger<ModuleAccessService> _logger;
    private const string CachePrefix = "org-modules:";
    private const int CacheExpirationMinutes = 5;
    private const string ModuleBillingSettingsKey = "ModuleBilling";

    public ModuleAccessService(QMgrDbContext dbContext, IDistributedCache cache, IStripeService stripeService, ILogger<ModuleAccessService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _stripeService = stripeService;
        _logger = logger;
    }

    public async Task<bool> IsModuleActiveAsync(Guid organizationId, string moduleCode)
    {
        var active = await GetActiveModuleCodesAsync(organizationId);
        return active.Contains(moduleCode);
    }

    public async Task<List<string>> GetActiveModuleCodesAsync(Guid organizationId)
    {
        var cacheKey = $"{CachePrefix}{organizationId}";
        try
        {
            var cached = await _cache.GetStringAsync(cacheKey);
            if (cached != null)
            {
                var deserialized = JsonSerializer.Deserialize<List<string>>(cached);
                if (deserialized != null) return deserialized;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read active modules from cache for organization {OrganizationId}", organizationId);
        }

        var active = await _dbContext.OrganizationModules
            .Include(om => om.Module)
            .AsNoTracking()
            .Where(om => om.OrganizationId == organizationId &&
                         (om.Status == OrganizationModuleStatus.Active || om.Status == OrganizationModuleStatus.Trialing))
            .Select(om => om.Module!.Code)
            .ToListAsync();

        try
        {
            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(active),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheExpirationMinutes) });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache active modules for organization {OrganizationId}", organizationId);
        }

        return active;
    }

    public async Task<List<ModuleCatalogItem>> GetCatalogAsync()
    {
        var modules = await _dbContext.SubscriptionPlans
            .AsNoTracking()
            .Where(m => ModuleCodes.All.Contains(m.Code) && m.IsActive)
            .OrderBy(m => m.SortOrder)
            .ToListAsync();

        return modules.Select(m => new ModuleCatalogItem(
            m.Id, m.Code, m.Name, m.Description,
            m.MonthlyPriceUsd, m.MonthlyPriceUgx, m.AnnualPriceUsd, m.AnnualPriceUgx,
            m.TrialDays, m.MaxBranches, m.MaxDisplays, m.MaxUsersPerBranch, m.MaxCountersPerBranch,
            m.MaxTokensPerMonth, m.MaxApiCallsPerMonth, m.MaxStorageMb)).ToList();
    }

    public async Task<List<OrganizationModuleStatusDto>> GetOrganizationModuleStatusAsync(Guid organizationId)
    {
        var catalog = await GetCatalogAsync();
        var owned = await _dbContext.OrganizationModules
            .AsNoTracking()
            .Where(om => om.OrganizationId == organizationId)
            .ToListAsync();

        return catalog.Select(m =>
        {
            var row = owned.Where(o => o.ModuleId == m.Id).OrderByDescending(o => o.CreatedAt).FirstOrDefault();
            var purchased = row != null && row.IsActiveOrTrialing;
            return new OrganizationModuleStatusDto(
                m.Code, m.Name, purchased,
                row?.Status.ToString(), row?.ActivatedAt, row?.TrialEndsAt, row?.GrantedByPlatformAdmin ?? false);
        }).ToList();
    }

    public async Task StartTrialAsync(Guid organizationId, string moduleCode)
    {
        var module = await GetModuleOrThrowAsync(moduleCode);
        var existing = await _dbContext.OrganizationModules
            .FirstOrDefaultAsync(om => om.OrganizationId == organizationId && om.ModuleId == module.Id);

        if (existing != null)
        {
            existing.Status = OrganizationModuleStatus.Trialing;
            existing.TrialEndsAt = DateTime.UtcNow.AddDays(module.TrialDays);
            existing.CancelledAt = null;
            _dbContext.OrganizationModules.Update(existing);
        }
        else
        {
            _dbContext.OrganizationModules.Add(new OrganizationModule
            {
                OrganizationId = organizationId,
                ModuleId = module.Id,
                Status = OrganizationModuleStatus.Trialing,
                ActivatedAt = DateTime.UtcNow,
                TrialEndsAt = DateTime.UtcNow.AddDays(module.TrialDays)
            });
        }

        await _dbContext.SaveChangesAsync();
        await InvalidateCacheAsync(organizationId);
    }

    public async Task ActivateAsync(Guid organizationId, string moduleCode, BillingCycle billingCycle, string? stripeSubscriptionItemId = null)
    {
        var module = await GetModuleOrThrowAsync(moduleCode);
        var existing = await _dbContext.OrganizationModules
            .FirstOrDefaultAsync(om => om.OrganizationId == organizationId && om.ModuleId == module.Id);

        var periodEnd = billingCycle == BillingCycle.Annual
            ? DateTime.UtcNow.AddYears(1)
            : DateTime.UtcNow.AddMonths(1);

        if (existing != null)
        {
            existing.Status = OrganizationModuleStatus.Active;
            existing.BillingCycle = billingCycle;
            existing.CurrentPeriodEnd = periodEnd;
            existing.TrialEndsAt = null;
            existing.CancelledAt = null;
            if (stripeSubscriptionItemId != null) existing.StripeSubscriptionItemId = stripeSubscriptionItemId;
            _dbContext.OrganizationModules.Update(existing);
        }
        else
        {
            _dbContext.OrganizationModules.Add(new OrganizationModule
            {
                OrganizationId = organizationId,
                ModuleId = module.Id,
                Status = OrganizationModuleStatus.Active,
                ActivatedAt = DateTime.UtcNow,
                BillingCycle = billingCycle,
                CurrentPeriodEnd = periodEnd,
                StripeSubscriptionItemId = stripeSubscriptionItemId
            });
        }

        await _dbContext.SaveChangesAsync();
        await InvalidateCacheAsync(organizationId);
    }

    public async Task GrantAsync(Guid organizationId, string moduleCode, Guid grantedByUserId, string? note)
    {
        var module = await GetModuleOrThrowAsync(moduleCode);
        var existing = await _dbContext.OrganizationModules
            .FirstOrDefaultAsync(om => om.OrganizationId == organizationId && om.ModuleId == module.Id);

        if (existing != null)
        {
            existing.Status = OrganizationModuleStatus.Active;
            existing.GrantedByPlatformAdmin = true;
            existing.AdminNote = note;
            existing.CancelledAt = null;
            existing.UpdatedBy = grantedByUserId;
            _dbContext.OrganizationModules.Update(existing);
        }
        else
        {
            _dbContext.OrganizationModules.Add(new OrganizationModule
            {
                OrganizationId = organizationId,
                ModuleId = module.Id,
                Status = OrganizationModuleStatus.Active,
                ActivatedAt = DateTime.UtcNow,
                GrantedByPlatformAdmin = true,
                AdminNote = note,
                CreatedBy = grantedByUserId
            });
        }

        await _dbContext.SaveChangesAsync();
        await InvalidateCacheAsync(organizationId);
    }

    public async Task RevokeAsync(Guid organizationId, string moduleCode, string? note)
    {
        var module = await GetModuleOrThrowAsync(moduleCode);
        var existing = await _dbContext.OrganizationModules
            .FirstOrDefaultAsync(om => om.OrganizationId == organizationId && om.ModuleId == module.Id);

        if (existing == null) return;

        // Tell Stripe first — if this fails, the module stays Active/billed rather than silently
        // locking the customer out of something they're still being charged for. Best-effort only
        // for Mobile Money-purchased or platform-admin-granted modules (StripeSubscriptionItemId
        // is null for both — nothing to remove there).
        if (!string.IsNullOrEmpty(existing.StripeSubscriptionItemId))
        {
            try
            {
                await _stripeService.RemoveSubscriptionItemAsync(existing.StripeSubscriptionItemId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to remove Stripe subscription item {ItemId} while revoking module {ModuleCode} for org {OrgId} — module removed locally anyway, but the customer may still be billed until this is resolved manually",
                    existing.StripeSubscriptionItemId, moduleCode, organizationId);
            }
        }

        existing.Status = OrganizationModuleStatus.Cancelled;
        existing.CancelledAt = DateTime.UtcNow;
        if (note != null) existing.AdminNote = note;
        _dbContext.OrganizationModules.Update(existing);

        await _dbContext.SaveChangesAsync();
        await InvalidateCacheAsync(organizationId);
    }

    public Task InvalidateCacheAsync(Guid organizationId) =>
        _cache.RemoveAsync($"{CachePrefix}{organizationId}");

    public async Task<(string? StripeCustomerId, string? StripeSubscriptionId)> GetStripeModuleBillingAsync(Guid organizationId)
    {
        var org = await _dbContext.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.StripeCustomerId, o.Settings })
            .FirstOrDefaultAsync();
        if (org == null) return (null, null);

        var billing = ReadModuleBillingSettings(org.Settings);
        return (org.StripeCustomerId, billing.StripeSubscriptionId);
    }

    public async Task SetStripeModuleBillingAsync(Guid organizationId, string? customerId, string? subscriptionId)
    {
        var org = await _dbContext.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);
        if (org == null) return;

        // Customer ID reuses the pre-existing Organization.StripeCustomerId column (shared with
        // the legacy tier billing flow — one org has exactly one Stripe customer either way).
        // Subscription ID still can't live on Subscription.PlanId (see ModuleBillingSettings doc
        // below), so it's the only thing left in the JSON blob.
        if (customerId != null) org.StripeCustomerId = customerId;

        if (subscriptionId != null)
        {
            var current = ReadModuleBillingSettings(org.Settings);
            org.Settings = WriteModuleBillingSettings(org.Settings, current with { StripeSubscriptionId = subscriptionId });
        }

        await _dbContext.SaveChangesAsync();
    }

    /// <summary>Holds only the module system's shared Stripe subscription ID — the customer ID
    /// lives on Organization.StripeCustomerId directly (shared with the legacy tier flow) once
    /// it's known, not duplicated here. Kept in Settings JSON rather than a new column because,
    /// unlike CustomerId, there's nowhere else on Organization/Subscription for it: Subscription
    /// entity's PlanId FK is required and points at a legacy tier plan, so a pure module-system
    /// org (no Subscription row) has no safe home for it there.</summary>
    private record ModuleBillingSettings(string? StripeSubscriptionId);

    private static ModuleBillingSettings ReadModuleBillingSettings(string? organizationSettingsJson)
    {
        if (string.IsNullOrEmpty(organizationSettingsJson)) return new ModuleBillingSettings(null);
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson);
            if (root != null && root.TryGetValue(ModuleBillingSettingsKey, out var element))
                return JsonSerializer.Deserialize<ModuleBillingSettings>(element.GetRawText()) ?? new ModuleBillingSettings(null);
        }
        catch (JsonException) { /* malformed settings blob — treat as not configured */ }
        return new ModuleBillingSettings(null);
    }

    private static string WriteModuleBillingSettings(string? organizationSettingsJson, ModuleBillingSettings settings)
    {
        var merged = string.IsNullOrEmpty(organizationSettingsJson)
            ? new Dictionary<string, object>()
            : (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson) ?? new())
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        merged[ModuleBillingSettingsKey] = settings;
        return JsonSerializer.Serialize(merged);
    }

    private async Task<SubscriptionPlan> GetModuleOrThrowAsync(string moduleCode)
    {
        var module = await _dbContext.SubscriptionPlans.FirstOrDefaultAsync(m => m.Code == moduleCode);
        if (module == null)
            throw new InvalidOperationException($"Unknown module code '{moduleCode}'.");
        return module;
    }
}
