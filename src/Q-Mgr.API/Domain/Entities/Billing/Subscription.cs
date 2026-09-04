using QMgr.Domain.Common;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Billing;

/// <summary>
/// Represents an organization's subscription to a plan
/// </summary>
public class Subscription : BaseAuditableEntity
{
    /// <summary>Organization that owns this subscription</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The subscription plan</summary>
    public Guid PlanId { get; set; }

    #region Status

    /// <summary>Current subscription status</summary>
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trialing;

    /// <summary>Billing cycle (monthly/annual)</summary>
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

    #endregion

    #region Dates

    /// <summary>When the subscription started</summary>
    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    /// <summary>When the subscription ends (null = ongoing)</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Current billing period start</summary>
    public DateTime CurrentPeriodStart { get; set; } = DateTime.UtcNow;

    /// <summary>Current billing period end</summary>
    public DateTime CurrentPeriodEnd { get; set; }

    /// <summary>When the subscription was cancelled (null = not cancelled)</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>When the trial ends (null = no trial or trial ended)</summary>
    public DateTime? TrialEnd { get; set; }

    /// <summary>Next billing date</summary>
    public DateTime? NextBillingDate { get; set; }

    #endregion

    #region Stripe Integration

    /// <summary>Stripe Subscription ID</summary>
    public string? StripeSubscriptionId { get; set; }

    /// <summary>Stripe Customer ID</summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>Stripe Payment Method ID</summary>
    public string? StripePaymentMethodId { get; set; }

    #endregion

    #region Mobile Money

    /// <summary>Phone number for mobile money payments</summary>
    public string? MobileMoneyPhone { get; set; }

    /// <summary>Preferred payment method</summary>
    public PaymentMethod PreferredPaymentMethod { get; set; } = PaymentMethod.Card;

    #endregion

    #region Agreed price (grandfathering)

    /// <summary>
    /// The price this organization agreed to for one billing period, captured when the
    /// subscription is created and again whenever its plan changes. Null means "track the plan's
    /// current list price", which is how every row behaved before this column existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what stops an edit to a plan's price from silently repricing everybody already on
    /// it. <see cref="AgreedCurrency"/> records which currency the number is in, and
    /// <see cref="GetEffectiveUnitPrice"/> refuses to use it in any other one — a UGX figure
    /// charged as USD would be a three-order-of-magnitude error.
    /// </para>
    /// <para>
    /// The number is the price for this row's <see cref="BillingCycle"/>. Nothing changes a
    /// subscription's cycle today; if that ever becomes possible, the agreed price has to be
    /// recaptured in the same operation, or a customer switching to annual would be charged a
    /// monthly price for a year.
    /// </para>
    /// </remarks>
    public decimal? AgreedUnitPrice { get; set; }

    /// <summary>ISO code of the currency <see cref="AgreedUnitPrice"/> is denominated in ("USD"
    /// or "UGX"), taken from the organization's preferred currency at the time it was agreed.</summary>
    public string? AgreedCurrency { get; set; }

    #endregion

    #region Overrides (for Enterprise custom limits)

    /// <summary>Override max branches (null = use plan default)</summary>
    public int? MaxBranchesOverride { get; set; }

    /// <summary>Override max tokens per month (null = use plan default)</summary>
    public int? MaxTokensOverride { get; set; }

    /// <summary>Override max API calls per month (null = use plan default)</summary>
    public int? MaxApiCallsOverride { get; set; }

    /// <summary>Override max users per branch (null = use plan default)</summary>
    public int? MaxUsersOverride { get; set; }

    /// <summary>Override max digital signage displays (null = use plan default)</summary>
    public int? MaxDisplaysOverride { get; set; }

    /// <summary>Override max storage in MB (null = use plan default). Set by a platform admin
    /// for a specific tenant, e.g. to grant more room without changing their whole plan.</summary>
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

    /// <summary>The subscription plan</summary>
    public virtual SubscriptionPlan? Plan { get; set; }

    /// <summary>Invoices for this subscription</summary>
    public virtual ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    /// <summary>Payments for this subscription</summary>
    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    #endregion

    #region Helper Methods

    /// <summary>Check if subscription is in an active state</summary>
    public bool IsActiveOrTrialing => Status == SubscriptionStatus.Active || Status == SubscriptionStatus.Trialing;

    /// <summary>Check if subscription has expired</summary>
    public bool IsExpired => Status == SubscriptionStatus.Expired || (EndDate.HasValue && EndDate.Value < DateTime.UtcNow);

    /// <summary>Get effective max branches (considering overrides)</summary>
    public int GetEffectiveMaxBranches(int planDefault) => MaxBranchesOverride ?? planDefault;

    /// <summary>Get effective max tokens (considering overrides)</summary>
    public int GetEffectiveMaxTokens(int planDefault) => MaxTokensOverride ?? planDefault;

    /// <summary>Get effective max storage in MB (considering overrides)</summary>
    public int GetEffectiveMaxStorage(int planDefault) => MaxStorageOverride ?? planDefault;

    /// <summary>
    /// The price to charge for one billing period: the agreed price when one was captured in
    /// <paramref name="currency"/>, otherwise the plan's current list price.
    /// </summary>
    /// <remarks>An agreed price held in a different currency is deliberately ignored rather than
    /// converted — this system holds no exchange rate, and charging a UGX number as USD is far
    /// worse than falling back to today's list price.</remarks>
    public decimal GetEffectiveUnitPrice(decimal listPrice, string currency) =>
        AgreedUnitPrice.HasValue && string.Equals(AgreedCurrency, currency, StringComparison.OrdinalIgnoreCase)
            ? AgreedUnitPrice.Value
            : listPrice;

    /// <summary>
    /// What this subscription bills per period in USD, honouring any agreed price. Needs
    /// <see cref="Plan"/> loaded; returns 0 when it is not.
    /// </summary>
    public decimal GetPeriodPriceUsd()
    {
        var listPrice = BillingCycle == BillingCycle.Annual
            ? Plan?.AnnualPriceUsd ?? 0
            : Plan?.MonthlyPriceUsd ?? 0;

        return GetEffectiveUnitPrice(listPrice, "USD");
    }

    /// <summary>
    /// Monthly recurring revenue in USD, honouring any agreed price. Revenue reporting has to go
    /// through this rather than read the plan's list price directly, or it misstates revenue by
    /// exactly the grandfathered difference.
    /// </summary>
    public decimal GetMonthlyRecurringRevenueUsd() =>
        BillingCycle == BillingCycle.Annual ? GetPeriodPriceUsd() / 12 : GetPeriodPriceUsd();

    #endregion
}
