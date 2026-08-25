using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Entities.Organization;

namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Service for Stripe payment integration (card payments)
/// </summary>
public interface IStripeService
{
    /// <summary>
    /// Create a Stripe customer for an organization
    /// </summary>
    Task<string> CreateCustomerAsync(Organization organization);

    /// <summary>
    /// Update Stripe customer details
    /// </summary>
    Task UpdateCustomerAsync(string customerId, Organization organization);

    /// <summary>
    /// Create a Stripe subscription
    /// </summary>
    Task<StripeSubscriptionResult> CreateSubscriptionAsync(
        string customerId,
        string priceId,
        int? trialDays = null);

    /// <summary>
    /// Cancel a Stripe subscription
    /// </summary>
    Task CancelSubscriptionAsync(string subscriptionId, bool immediately = false);

    /// <summary>
    /// Update subscription to a new price/plan
    /// </summary>
    Task<StripeSubscriptionResult> UpdateSubscriptionAsync(
        string subscriptionId,
        string newPriceId);

    /// <summary>
    /// Create a Stripe Checkout session for subscription signup
    /// </summary>
    Task<CheckoutSessionResult> CreateCheckoutSessionAsync(
        Guid organizationId,
        string priceId,
        string successUrl,
        string cancelUrl,
        string? customerId = null);

    /// <summary>
    /// Create a Stripe Customer Portal session for self-service billing management
    /// </summary>
    Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl);

    /// <summary>
    /// Process a Stripe webhook event
    /// </summary>
    Task<WebhookProcessResult> HandleWebhookAsync(string payload, string signature);

    /// <summary>
    /// Retrieve subscription details from Stripe
    /// </summary>
    Task<StripeSubscriptionResult?> GetSubscriptionAsync(string subscriptionId);

    /// <summary>
    /// Retrieve customer's payment methods
    /// </summary>
    Task<IEnumerable<PaymentMethodInfo>> GetPaymentMethodsAsync(string customerId);

    /// <summary>
    /// Sets a payment method as the customer's default. Returns false if the payment method
    /// doesn't belong to this customer (caller must not trust a bare ID from the client).
    /// </summary>
    Task<bool> SetDefaultPaymentMethodAsync(string customerId, string paymentMethodId);

    /// <summary>
    /// Detaches (removes) a payment method from a customer. Returns false if the payment
    /// method doesn't belong to this customer.
    /// </summary>
    Task<bool> RemovePaymentMethodAsync(string customerId, string paymentMethodId);

    /// <summary>
    /// Create a payment intent for one-time payment
    /// </summary>
    Task<PaymentIntentResult> CreatePaymentIntentAsync(
        string customerId,
        decimal amount,
        string currency,
        string? description = null);

    /// <summary>
    /// Charge a customer's default payment method
    /// </summary>
    Task<ChargeResult> ChargeCustomerAsync(
        string customerId,
        decimal amount,
        string currency,
        string description);
}

/// <summary>
/// Result of charging a customer
/// </summary>
public record ChargeResult(
    bool Success,
    string? ChargeId,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Result of Stripe subscription operations
/// </summary>
public record StripeSubscriptionResult(
    string SubscriptionId,
    string CustomerId,
    string Status,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    DateTime? TrialEnd,
    string? LatestInvoiceId,
    string? PaymentMethodId);

/// <summary>
/// Result of creating a checkout session
/// </summary>
public record CheckoutSessionResult(
    string SessionId,
    string Url);

/// <summary>
/// Result of processing a webhook
/// </summary>
public record WebhookProcessResult(
    bool Success,
    string EventType,
    string? ErrorMessage = null,
    string? StripeSubscriptionId = null,
    string? StripeCustomerId = null,
    string? StripeInvoiceId = null,
    decimal? AmountPaid = null,
    string? Currency = null,
    string? FailureMessage = null);

/// <summary>
/// Payment method information
/// </summary>
public record PaymentMethodInfo(
    string Id,
    string Type,
    string? CardBrand,
    string? CardLast4,
    int? CardExpMonth,
    int? CardExpYear,
    bool IsDefault);

/// <summary>
/// Result of creating a payment intent
/// </summary>
public record PaymentIntentResult(
    string PaymentIntentId,
    string ClientSecret,
    string Status);
