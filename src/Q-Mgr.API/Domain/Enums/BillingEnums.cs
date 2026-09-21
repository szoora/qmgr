namespace QMgr.Domain.Enums;

/// <summary>
/// Subscription status for tenant billing
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>In trial period</summary>
    Trialing = 0,

    /// <summary>Active paid subscription</summary>
    Active = 1,

    /// <summary>Payment failed, grace period</summary>
    PastDue = 2,

    /// <summary>User cancelled subscription</summary>
    Cancelled = 3,

    /// <summary>Subscription period ended</summary>
    Expired = 4,

    /// <summary>Suspended by admin</summary>
    Suspended = 5
}

/// <summary>
/// Billing cycle for subscriptions
/// </summary>
public enum BillingCycle
{
    /// <summary>Monthly billing</summary>
    Monthly = 0,

    /// <summary>Annual billing (discount)</summary>
    Annual = 1
}

/// <summary>
/// Tenant/Organization status in SaaS platform
/// </summary>
public enum TenantStatus
{
    /// <summary>Awaiting email verification</summary>
    Pending = 0,

    /// <summary>In trial period</summary>
    Trialing = 1,

    /// <summary>Paid and active</summary>
    Active = 2,

    /// <summary>Payment failed or admin suspended</summary>
    Suspended = 3,

    /// <summary>User cancelled. Data is still here and can still be exported; nothing has been removed.</summary>
    Cancelled = 4,

    /// <summary>
    /// LEGACY. A soft-delete nothing has ever written: until 2026-09-20 this value was read in four
    /// places — the status middleware, the platform analytics count, the registration guard and a
    /// nightly purge job — and assigned in none, so the whole pipeline hung off a state the product
    /// could not reach. New code does not write it either, because a purged tenant has NO ROW AT
    /// ALL; what survives is a <c>TenantTombstone</c>. It is kept so that any row an old database
    /// still carries keeps being refused by the middleware rather than silently coming back to life.
    /// </summary>
    Deleted = 5,

    /// <summary>
    /// Scheduled for irreversible deletion, and still restorable by a platform administrator.
    ///
    /// A STATE OF ITS OWN rather than a flag on <see cref="Cancelled"/>, because the two are
    /// different promises: Cancelled says your data is here and you can have it, this says it is
    /// going. Collapsing them means either deleting data a customer still believes they can export,
    /// or never actually deleting anything.
    /// </summary>
    PendingDeletion = 6
}

/// <summary>
/// Invoice status for billing
/// </summary>
public enum InvoiceStatus
{
    /// <summary>Invoice being prepared</summary>
    Draft = 0,

    /// <summary>Invoice sent, awaiting payment</summary>
    Open = 1,

    /// <summary>Payment received</summary>
    Paid = 2,

    /// <summary>Invoice voided/cancelled</summary>
    Void = 3,

    /// <summary>Unable to collect payment</summary>
    Uncollectible = 4
}

/// <summary>
/// Payment method type
/// </summary>
public enum PaymentMethod
{
    /// <summary>Credit/Debit card via Stripe</summary>
    Card = 0,

    /// <summary>MTN Mobile Money</summary>
    MtnMobileMoney = 1,

    /// <summary>Airtel Money</summary>
    AirtelMoney = 2,

    /// <summary>Bank transfer</summary>
    BankTransfer = 3,

    /// <summary>Manual/offline payment</summary>
    Manual = 4
}

/// <summary>
/// Payment status
/// </summary>
public enum PaymentStatus
{
    /// <summary>Payment initiated, awaiting confirmation</summary>
    Pending = 0,

    /// <summary>Payment processing</summary>
    Processing = 1,

    /// <summary>Payment successful</summary>
    Succeeded = 2,

    /// <summary>Payment failed</summary>
    Failed = 3,

    /// <summary>Payment refunded</summary>
    Refunded = 4,

    /// <summary>Payment cancelled</summary>
    Cancelled = 5,

    /// <summary>Held for a human at the payment gateway (sacc.ug "Review"). NOT final: the money may
    /// still be applied or returned, so nothing is provisioned and dunning does not start.</summary>
    Review = 6,

    /// <summary>The payer never answered and the gateway gave up (sacc.ug "Abandoned", its webhook
    /// "payment.expired"), or the gateway never received the request. Final; nothing was collected.</summary>
    Abandoned = 7
}

/// <summary>Status of one organization's purchase of one module</summary>
public enum OrganizationModuleStatus
{
    /// <summary>In the module's free trial period, no charge collected yet</summary>
    Trialing = 0,

    /// <summary>Active, paid (or platform-admin granted)</summary>
    Active = 1,

    /// <summary>Payment collection failed, in the grace period</summary>
    PastDue = 2,

    /// <summary>Removed by the tenant or a platform admin</summary>
    Cancelled = 3
}
