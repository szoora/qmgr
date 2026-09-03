using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Entities.Organization;

namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Service for Stripe payment integration (card payments)
/// </summary>
public interface IStripeService
{
    /// <summary>
    /// True when a usable secret key is present (Platform Settings row first, then configuration)
    /// and the Stripe provider is switched on in Platform Settings.
    /// </summary>
    Task<bool> IsConfiguredAsync();

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
    Task<IEnumerable<PaymentMethodDto>> GetPaymentMethodsAsync(string customerId);

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

    #region Modular subscription system — multi-item support

    /// <summary>
    /// Creates one Stripe subscription with one item per selected module's price — the modular
    /// subscription system's real "multi-item" billing object. Unlike CreateSubscriptionAsync
    /// (kept single-item, untouched, for the legacy Tier flow), this is the org's ONE shared
    /// subscription that every Stripe-paid module becomes an item on; later modules join it via
    /// AddSubscriptionItemAsync rather than creating a second subscription.
    /// </summary>
    Task<StripeSubscriptionResult> CreateMultiItemSubscriptionAsync(
        string customerId,
        IEnumerable<string> priceIds,
        int? trialDays = null);

    /// <summary>
    /// Adds one more item (module) to an org's existing multi-item subscription. Stripe prorates
    /// and bills the org's already-saved default payment method automatically — no checkout
    /// redirect needed for this path, only for the very first Stripe-paid module (see
    /// CreateCheckoutSessionAsync, reused unchanged for that first-item case). Returns the new
    /// subscription item's ID — store it on OrganizationModule.StripeSubscriptionItemId so
    /// RemoveSubscriptionItemAsync can target exactly this item later.
    /// </summary>
    Task<string> AddSubscriptionItemAsync(string subscriptionId, string priceId);

    /// <summary>
    /// Removes one item (module) from a multi-item subscription — the other items/modules on the
    /// same subscription are unaffected. Stripe prorates the removal automatically.
    /// </summary>
    Task RemoveSubscriptionItemAsync(string subscriptionItemId);

    /// <summary>
    /// Checkout Session for the *first* Stripe-paid module an organization buys — collecting a
    /// new card and creating the org's one shared multi-item subscription happen together on
    /// Stripe's own hosted page (never touching raw card data ourselves). Every module after the
    /// first joins that same subscription via AddSubscriptionItemAsync instead — no further
    /// redirect needed, since Stripe already has a saved default payment method to charge.
    /// Session metadata carries organization_id + module_code so the webhook handler below knows
    /// what to activate once Stripe confirms the checkout completed.
    /// </summary>
    Task<CheckoutSessionResult> CreateModuleCheckoutSessionAsync(
        Guid organizationId,
        string moduleCode,
        string priceId,
        string successUrl,
        string cancelUrl,
        string? customerId = null,
        string billingCycle = "Monthly");

    #endregion
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
    string? FailureMessage = null,
    // Populated only for checkout.session.completed on a module-purchase Checkout Session (see
    // CreateModuleCheckoutSessionAsync) — lets the webhook handler know which OrganizationModule
    // to activate and which Stripe subscription item ID to record for it.
    Guid? ModuleOrganizationId = null,
    string? ModuleCode = null,
    string? NewSubscriptionItemId = null,
    string? ModuleBillingCycle = null);

/// <summary>
/// Payment method information
/// </summary>
/// <summary>
/// Result of creating a payment intent
/// </summary>
public record PaymentIntentResult(
    string PaymentIntentId,
    string ClientSecret,
    string Status);
