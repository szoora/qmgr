using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Billing;

/// <summary>
/// One organization's subscription to one purchasable functional module (Core Queue Management,
/// Engagement &amp; Communications, Visitor &amp; Safeguarding, Integrations &amp; API Access).
/// An organization can hold many of these at once — this is what replaces the old single
/// <c>Subscription.PlanId</c> "one tier per org" model. <see cref="ModuleId"/> points at a
/// <see cref="SubscriptionPlan"/> row that now represents a module's pricing/limits definition
/// rather than a whole tier.
/// </summary>
public class OrganizationModule : BaseAuditableEntity
{
    /// <summary>Organization that owns this module grant/purchase</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The module (a <see cref="SubscriptionPlan"/> row keyed by a module code)</summary>
    public Guid ModuleId { get; set; }

    /// <summary>Current status of this module for this organization</summary>
    public OrganizationModuleStatus Status { get; set; } = OrganizationModuleStatus.Trialing;

    /// <summary>When this module was first activated for the organization</summary>
    public DateTime ActivatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the trial for this module ends (null once converted to a real subscription)</summary>
    public DateTime? TrialEndsAt { get; set; }

    /// <summary>When this module was cancelled/removed (null while active)</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Billing cycle for this module's charge</summary>
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

    /// <summary>Current billing period end for this module — drives invoice generation, mirrors
    /// the pattern already used by <see cref="Subscription.CurrentPeriodEnd"/></summary>
    public DateTime? CurrentPeriodEnd { get; set; }

    /// <summary>Stripe subscription item ID, once the Stripe multi-item rework ships (phase 7)</summary>
    public string? StripeSubscriptionItemId { get; set; }

    /// <summary>True if this grant was made directly by a platform admin (no payment collected) —
    /// distinguishes a comp/support grant from a real self-service or trial purchase</summary>
    public bool GrantedByPlatformAdmin { get; set; }

    /// <summary>Reason a platform admin granted or revoked this module, for audit purposes</summary>
    public string? AdminNote { get; set; }

    #region Navigation

    public virtual QMgr.Domain.Entities.Organization.Organization? Organization { get; set; }
    public virtual SubscriptionPlan? Module { get; set; }

    #endregion

    #region Helper properties

    public bool IsActiveOrTrialing => Status == OrganizationModuleStatus.Active || Status == OrganizationModuleStatus.Trialing;

    #endregion
}
