using QMgr.Domain.Entities.Billing;

namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Hands out an organization's billing account, opening one the first time it is needed.
/// </summary>
/// <remarks>
/// Its own small service because two unrelated callers need it and neither can depend on the
/// other: <c>ModuleAccessService</c> opens an account the moment a module is activated, so the
/// customer has a billing page from their first purchase, and <c>BillingService</c> needs one to
/// hang an invoice off. Routing the second through <c>IBillingService</c> would close a cycle —
/// BillingService already depends on FeatureFlagService, which depends on ModuleAccessService.
/// </remarks>
public interface IBillingAccountProvider
{
    /// <summary>
    /// The organization's billing account, opened at <paramref name="asOf"/> if it has none.
    /// </summary>
    Task<Subscription> GetOrOpenAsync(Guid organizationId, DateTime asOf);
}
