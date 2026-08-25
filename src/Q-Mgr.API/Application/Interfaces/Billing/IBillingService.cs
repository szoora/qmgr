using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Enums;

namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Core billing service for subscription management
/// </summary>
public interface IBillingService
{
    #region Subscription Plans

    /// <summary>
    /// Get all available subscription plans
    /// </summary>
    Task<IEnumerable<SubscriptionPlan>> GetPlansAsync(bool includePrivate = false);

    /// <summary>
    /// Get a subscription plan by code
    /// </summary>
    Task<SubscriptionPlan?> GetPlanByCodeAsync(string planCode);

    /// <summary>
    /// Get a subscription plan by ID
    /// </summary>
    Task<SubscriptionPlan?> GetPlanByIdAsync(Guid planId);

    #endregion

    #region Subscriptions

    /// <summary>
    /// Create a new subscription for an organization
    /// </summary>
    Task<SubscriptionResult> CreateSubscriptionAsync(
        Guid organizationId,
        string planCode,
        BillingCycle billingCycle,
        PaymentMethod paymentMethod,
        string? stripePaymentMethodId = null,
        string? mobileMoneyPhone = null);

    /// <summary>
    /// Get the current subscription for an organization
    /// </summary>
    Task<Subscription?> GetSubscriptionAsync(Guid organizationId);

    /// <summary>
    /// Look up a subscription by its Stripe subscription ID — the identifier a Stripe webhook
    /// payload carries, as opposed to our own local Guid.
    /// </summary>
    Task<Subscription?> GetSubscriptionByStripeIdAsync(string stripeSubscriptionId);

    /// <summary>
    /// Change subscription plan (upgrade/downgrade)
    /// </summary>
    Task<SubscriptionResult> ChangePlanAsync(
        Guid subscriptionId,
        string newPlanCode,
        bool immediateChange = false);

    /// <summary>
    /// Cancel a subscription
    /// </summary>
    Task<SubscriptionResult> CancelSubscriptionAsync(
        Guid subscriptionId,
        string? reason = null,
        bool immediately = false);

    /// <summary>
    /// Reactivate a cancelled subscription
    /// </summary>
    Task<SubscriptionResult> ReactivateSubscriptionAsync(Guid subscriptionId);

    /// <summary>
    /// Process subscription renewal
    /// </summary>
    Task<SubscriptionResult> ProcessRenewalAsync(Guid subscriptionId);

    /// <summary>
    /// Handle subscription payment failure
    /// </summary>
    Task HandlePaymentFailureAsync(Guid subscriptionId, string? errorMessage = null);

    /// <summary>
    /// Handle a successful subscription payment (e.g. a Stripe invoice.payment_succeeded
    /// webhook) — records the payment, marks the matching invoice paid if one exists, and
    /// reactivates the subscription/organization if either had been suspended for non-payment.
    /// </summary>
    Task HandlePaymentSuccessAsync(Guid subscriptionId, decimal amount, string currency, string? externalReference = null);

    /// <summary>
    /// Get subscription with plan details
    /// </summary>
    Task<SubscriptionWithPlan?> GetSubscriptionWithPlanAsync(Guid organizationId);

    /// <summary>
    /// Get an organization's trial/status info directly from the Organization record. Needed
    /// because trial state (Status, TrialEndsAt) lives on Organization, not Subscription — a
    /// trialing org that hasn't picked a paid plan yet has no Subscription row at all, so
    /// callers that only look at GetSubscriptionWithPlanAsync have no way to tell "trialing
    /// with N days left" apart from "no org found".
    /// </summary>
    Task<OrganizationTrialInfo?> GetOrganizationTrialInfoAsync(Guid organizationId);

    #endregion

    #region Invoices

    /// <summary>
    /// Generate an invoice for a subscription period
    /// </summary>
    Task<Invoice> GenerateInvoiceAsync(Guid subscriptionId, DateTime periodStart, DateTime periodEnd);

    /// <summary>
    /// Generate invoice for current billing period
    /// </summary>
    Task<Invoice> GenerateInvoiceAsync(Guid subscriptionId);

    /// <summary>
    /// Attempt to collect payment for an invoice
    /// </summary>
    Task<PaymentCollectionResult> CollectPaymentAsync(Guid invoiceId);

    /// <summary>
    /// Get invoices for an organization
    /// </summary>
    Task<IEnumerable<Invoice>> GetInvoicesAsync(Guid organizationId, int page = 1, int pageSize = 20);

    /// <summary>
    /// Get an invoice by ID
    /// </summary>
    Task<Invoice?> GetInvoiceAsync(Guid invoiceId);

    /// <summary>
    /// Mark an invoice as paid
    /// </summary>
    Task MarkInvoicePaidAsync(Guid invoiceId, Guid paymentId);

    /// <summary>
    /// Void an invoice
    /// </summary>
    Task VoidInvoiceAsync(Guid invoiceId, string reason);

    #endregion

    #region Payments

    /// <summary>
    /// Record a payment
    /// </summary>
    Task<Payment> RecordPaymentAsync(
        Guid organizationId,
        decimal amount,
        string currency,
        PaymentMethod method,
        Guid? subscriptionId = null,
        Guid? invoiceId = null,
        string? externalReference = null);

    /// <summary>
    /// Get payments for an organization
    /// </summary>
    Task<IEnumerable<Payment>> GetPaymentsAsync(Guid organizationId, int page = 1, int pageSize = 20);

    /// <summary>
    /// Process a refund
    /// </summary>
    Task<Payment> ProcessRefundAsync(Guid paymentId, decimal? amount = null, string? reason = null);

    #endregion

    #region Limits

    /// <summary>
    /// Check if an organization is within a specific limit
    /// </summary>
    Task<LimitCheckResult> CheckLimitAsync(Guid organizationId, string limitType);

    /// <summary>
    /// Get all limits and current usage for an organization
    /// </summary>
    Task<OrganizationLimits> GetLimitsAsync(Guid organizationId);

    /// <summary>
    /// Get effective limits for an organization (resolves its active subscription, then
    /// plan limits + overrides). Falls back to free-tier limits if there's no active
    /// subscription.
    /// </summary>
    Task<EffectiveLimits> GetEffectiveLimitsAsync(Guid organizationId);

    /// <summary>
    /// Get effective limits for a specific subscription by its own Id (plan limits +
    /// overrides). Use <see cref="GetEffectiveLimitsAsync"/> instead unless you already
    /// have the subscription loaded and specifically need to bypass the active-subscription
    /// resolution (e.g. displaying limits for a cancelled/past subscription).
    /// </summary>
    Task<EffectiveLimits> GetEffectiveLimitsBySubscriptionIdAsync(Guid subscriptionId);

    #endregion

    #region Trial

    /// <summary>
    /// Start a trial for an organization
    /// </summary>
    Task StartTrialAsync(Guid organizationId, string planCode, int trialDays);

    /// <summary>
    /// Check if trial has expired
    /// </summary>
    Task<bool> IsTrialExpiredAsync(Guid organizationId);

    /// <summary>
    /// Convert trial to paid subscription
    /// </summary>
    Task<SubscriptionResult> ConvertTrialAsync(
        Guid organizationId,
        PaymentMethod paymentMethod,
        string? stripePaymentMethodId = null,
        string? mobileMoneyPhone = null);

    #endregion
}

/// <summary>
/// Result of subscription operations
/// </summary>
public record SubscriptionResult(
    bool Success,
    Subscription? Subscription,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Subscription with plan details
/// </summary>
public record SubscriptionWithPlan(
    Subscription Subscription,
    SubscriptionPlan Plan,
    EffectiveLimits Limits);

/// <summary>
/// An organization's trial/status info, sourced from Organization rather than Subscription —
/// valid even before any Subscription row exists (e.g. during the free trial).
/// </summary>
public record OrganizationTrialInfo(
    TenantStatus Status,
    DateTime? TrialEndsAt);

/// <summary>
/// Result of limit check
/// </summary>
public record LimitCheckResult(
    bool IsWithinLimit,
    string LimitType,
    int CurrentUsage,
    int MaxAllowed,
    int Remaining,
    double PercentageUsed);

/// <summary>
/// Organization limits overview
/// </summary>
public record OrganizationLimits(
    Guid OrganizationId,
    TenantTier Tier,
    Dictionary<string, LimitCheckResult> Limits);

/// <summary>
/// Effective limits for a subscription
/// </summary>
public record EffectiveLimits(
    int MaxBranches,
    int MaxUsersPerBranch,
    int MaxCountersPerBranch,
    int MaxTokensPerMonth,
    int MaxApiCallsPerMonth,
    int MaxStorageMb,
    bool HasApiAccess,
    bool HasSmsNotifications,
    bool HasCustomBranding,
    bool HasAdvancedAnalytics,
    bool ShowAds);

/// <summary>
/// Result of payment collection attempt
/// </summary>
public record PaymentCollectionResult(
    bool Success,
    Payment? Payment,
    string? ErrorCode,
    string? ErrorMessage);
