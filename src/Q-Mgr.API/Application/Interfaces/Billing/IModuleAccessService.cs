using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Resolves and manages which of the four purchasable modules (see <c>ModuleCodes</c>) an
/// organization has active. Sibling to <see cref="IFeatureFlagService"/>, not a merge into it —
/// that service is a pure read-side boolean resolver; granting/revoking/purchasing a module is a
/// mutation-bearing commerce concept and deserves its own service.
/// </summary>
public interface IModuleAccessService
{
    /// <summary>Is this module currently Active or Trialing for this organization?</summary>
    Task<bool> IsModuleActiveAsync(Guid organizationId, string moduleCode);

    /// <summary>All module codes currently Active or Trialing for this organization.</summary>
    Task<List<string>> GetActiveModuleCodesAsync(Guid organizationId);

    /// <summary>The full 4-module catalog with pricing — same shape regardless of organization.</summary>
    Task<List<ModuleCatalogItem>> GetCatalogAsync();

    /// <summary>Per-module purchase status for one organization, for the marketplace/admin UI.</summary>
    Task<List<OrganizationModuleStatusDto>> GetOrganizationModuleStatusAsync(Guid organizationId);

    /// <summary>Registration: start a no-card trial for a module the new tenant selected.</summary>
    Task StartTrialAsync(Guid organizationId, string moduleCode);

    /// <summary>Self-service or job-driven activation after a successful payment collection.
    /// stripeSubscriptionItemId is set only for a card/Stripe purchase — null for Mobile Money,
    /// which has no per-module subscription-item concept at all.</summary>
    Task ActivateAsync(Guid organizationId, string moduleCode, BillingCycle billingCycle, string? stripeSubscriptionItemId = null);

    /// <summary>Platform admin direct grant — no payment collected, immediately Active.</summary>
    Task GrantAsync(Guid organizationId, string moduleCode, Guid grantedByUserId, string? note);

    /// <summary>Removes a module — used by both self-service cancel and platform admin revoke.</summary>
    Task RevokeAsync(Guid organizationId, string moduleCode, string? note);

    /// <summary>Clears the cached active-module set for an organization after any change above.</summary>
    Task InvalidateCacheAsync(Guid organizationId);

    #region Stripe module billing (multi-item subscription tracking)

    /// <summary>The org's shared multi-item Stripe subscription/customer IDs, if any Stripe-paid
    /// module has ever been purchased. CustomerId reuses the pre-existing
    /// Organization.StripeCustomerId column (shared with the legacy tier billing flow — one org
    /// has exactly one Stripe customer either way). SubscriptionId is stored in Organization.
    /// Settings (JSON), same pattern already used for ClassColorSettings/VisitingDaySettings,
    /// because it has nowhere else to live: Subscription.PlanId is a required FK to a (now-legacy)
    /// tier plan, so a pure module-system org with no Subscription row at all has nowhere on that
    /// entity to safely hang a Stripe subscription ID without also faking a PlanId that
    /// BillingService/Subscription.razor would then misread as real tier data.</summary>
    Task<(string? StripeCustomerId, string? StripeSubscriptionId)> GetStripeModuleBillingAsync(Guid organizationId);

    /// <summary>Persists whichever of customerId/subscriptionId is non-null, leaving the other
    /// unchanged — call with just the one that's newly known at each step of the purchase flow.</summary>
    Task SetStripeModuleBillingAsync(Guid organizationId, string? customerId, string? subscriptionId);

    #endregion
}
