namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Works out what an organization is allowed, from the modules it holds.
/// </summary>
/// <remarks>
/// Its own service rather than a method on <see cref="IBillingService"/> because
/// <c>UsageTrackingService</c> needs the same answer and <c>BillingService</c> already depends on
/// <c>UsageTrackingService</c> — putting it on the billing service would close a dependency cycle.
/// Both resolve through here instead, so the rule has one home.
/// </remarks>
public interface IModuleLimitResolver
{
    /// <summary>
    /// The effective ceiling for one organization: the highest value each limit takes across the
    /// modules it holds, with any per-tenant override on its billing account winning.
    /// </summary>
    Task<EffectiveLimits> ResolveAsync(Guid organizationId);
}
