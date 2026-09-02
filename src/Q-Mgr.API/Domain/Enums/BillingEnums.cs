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

    /// <summary>User cancelled</summary>
    Cancelled = 4,

    /// <summary>Soft-deleted</summary>
    Deleted = 5
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
    Cancelled = 5
}

/// <summary>
/// Tenant tier for feature gating.
/// SUPERSEDED 2026-09-02 by the modular subscription system (see <see cref="OrganizationModuleStatus"/>
/// / <see cref="QMgr.Domain.Entities.Billing.OrganizationModule"/>) — a tenant's access is now
/// determined by which modules they've purchased, not one flat tier. Left in place rather than
/// deleted so the existing single-plan billing code paths (`BillingController.Subscribe`,
/// `Tenants.razor`'s legacy tier display, etc.) keep compiling; new code should never read this.
/// </summary>
public enum TenantTier
{
    /// <summary>Free tier with ads</summary>
    Free = 0,

    /// <summary>Starter paid tier</summary>
    Starter = 1,

    /// <summary>Professional tier</summary>
    Professional = 2,

    /// <summary>Enterprise tier with dedicated schema</summary>
    Enterprise = 3
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
