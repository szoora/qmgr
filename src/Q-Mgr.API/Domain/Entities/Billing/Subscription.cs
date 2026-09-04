using QMgr.Domain.Common;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Billing;

/// <summary>
/// One organization's billing account: the period it is billed on, how it pays, and the invoices
/// and payments raised against it.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-09-04 this row also named a pricing tier, through a <c>PlanId</c> pointing at one of
/// four tier plans, and that tier was the only thing in the product that produced a recurring
/// invoice. Modules granted access but were charged once and never renewed. The tier is gone: this
/// row now says <em>when</em> and <em>how</em> an organization is billed, and the
/// <see cref="OrganizationModule"/> rows say <em>what for</em> and <em>how much</em>.
/// </para>
/// <para>
/// One account per organization, on one anniversary, so a customer receives a single invoice per
/// period with a line for each module they hold. A module added mid-period is prorated onto the
/// next invoice rather than billed on its own date.
/// </para>
/// </remarks>
public class Subscription : BaseAuditableEntity
{
    /// <summary>Organization that owns this billing account</summary>
    public Guid OrganizationId { get; set; }

    #region Status

    /// <summary>Current account status</summary>
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trialing;

    /// <summary>
    /// The cadence the organization is invoiced on. Individual modules carry their own cycle;
    /// this is the anniversary every one of them is collected onto.
    /// </summary>
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

    #endregion

    #region Dates

    /// <summary>When billing started</summary>
    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    /// <summary>When the account closed (null = open)</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Current billing period start</summary>
    public DateTime CurrentPeriodStart { get; set; } = DateTime.UtcNow;

    /// <summary>Current billing period end — the invoice job fires once this is in the past</summary>
    public DateTime CurrentPeriodEnd { get; set; }

    /// <summary>When the account was cancelled (null = not cancelled)</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>When the trial ends (null = no trial or trial ended)</summary>
    public DateTime? TrialEnd { get; set; }

    /// <summary>Next billing date</summary>
    public DateTime? NextBillingDate { get; set; }

    #endregion

    #region Stripe Integration

    /// <summary>Stripe Subscription ID — the multi-item subscription carrying one item per module</summary>
    public string? StripeSubscriptionId { get; set; }

    /// <summary>Stripe Customer ID</summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>Stripe Payment Method ID</summary>
    public string? StripePaymentMethodId { get; set; }

    #endregion

    #region Mobile Money

    /// <summary>Phone number for mobile money collection</summary>
    public string? MobileMoneyPhone { get; set; }

    /// <summary>Preferred payment method</summary>
    public PaymentMethod PreferredPaymentMethod { get; set; } = PaymentMethod.Card;

    #endregion

    #region Overrides (per-tenant limit grants)

    /// <summary>Override max branches (null = derive from the organization's modules)</summary>
    public int? MaxBranchesOverride { get; set; }

    /// <summary>Override max tokens per month (null = derive from modules)</summary>
    public int? MaxTokensOverride { get; set; }

    /// <summary>Override max API calls per month (null = derive from modules)</summary>
    public int? MaxApiCallsOverride { get; set; }

    /// <summary>Override max users per branch (null = derive from modules)</summary>
    public int? MaxUsersOverride { get; set; }

    /// <summary>Override max digital signage displays (null = derive from modules)</summary>
    public int? MaxDisplaysOverride { get; set; }

    /// <summary>Override max storage in MB (null = derive from modules). Set by a platform admin
    /// for a specific tenant, e.g. to grant more room without selling them another module.</summary>
    public int? MaxStorageOverride { get; set; }

    #endregion

    #region Cancellation

    /// <summary>Reason for cancellation</summary>
    public string? CancellationReason { get; set; }

    /// <summary>Whether to cancel at period end (vs immediately)</summary>
    public bool CancelAtPeriodEnd { get; set; }

    #endregion

    #region Navigation

    /// <summary>The organization</summary>
    public virtual QMgr.Domain.Entities.Organization.Organization? Organization { get; set; }

    /// <summary>Invoices raised against this account</summary>
    public virtual ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    /// <summary>Payments collected against this account</summary>
    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    #endregion

    #region Helper Methods

    /// <summary>Check if the account is in an active state</summary>
    public bool IsActiveOrTrialing => Status == SubscriptionStatus.Active || Status == SubscriptionStatus.Trialing;

    /// <summary>Check if the account has expired</summary>
    public bool IsExpired => Status == SubscriptionStatus.Expired || (EndDate.HasValue && EndDate.Value < DateTime.UtcNow);

    /// <summary>Get effective max branches (considering overrides)</summary>
    public int GetEffectiveMaxBranches(int moduleDerived) => MaxBranchesOverride ?? moduleDerived;

    /// <summary>Get effective max tokens (considering overrides)</summary>
    public int GetEffectiveMaxTokens(int moduleDerived) => MaxTokensOverride ?? moduleDerived;

    /// <summary>Get effective max storage in MB (considering overrides)</summary>
    public int GetEffectiveMaxStorage(int moduleDerived) => MaxStorageOverride ?? moduleDerived;

    #endregion
}
