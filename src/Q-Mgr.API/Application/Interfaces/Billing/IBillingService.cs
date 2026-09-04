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

    #region Billing account

    /// <summary>
    /// Open the organization's billing account — when it is invoiced and how it pays. What it
    /// is billed for comes from the modules it holds.
    /// </summary>
    Task<SubscriptionResult> CreateSubscriptionAsync(
        Guid organizationId,
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
    /// Raises one invoice covering every module whose billing period has run out.
    /// Returns null when nothing is due. This is the recurring-billing entry point.
    /// </summary>
    Task<Invoice?> GenerateInvoiceForDueModulesAsync(Guid organizationId, DateTime asOf);

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

    /// <summary>
    /// Monthly recurring revenue across the platform in USD, summed from the modules
    /// organizations hold at the price each of them agreed to.
    /// </summary>
    Task<decimal> GetPlatformMonthlyRecurringRevenueUsdAsync();

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
    /// modules held + overrides). Falls back to a minimal floor when the organization holds none
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
// The plan is gone with the tier system; this now pairs the billing account with the limits its
// modules grant. Kept under the old name so every call site did not have to churn in one pass.
public record SubscriptionWithPlan(
    Subscription Subscription,
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
    bool ShowAds);

/// <summary>
/// Result of payment collection attempt
/// </summary>
public record PaymentCollectionResult(
    bool Success,
    Payment? Payment,
    string? ErrorCode,
    string? ErrorMessage);
