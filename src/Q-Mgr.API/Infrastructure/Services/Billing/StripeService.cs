using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Organization;
using Stripe;
using Stripe.Checkout;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// Stripe payment integration service for card payments
/// </summary>
public class StripeService : IStripeService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<StripeService> _logger;
    private readonly string _webhookSecret;

    public StripeService(IConfiguration configuration, ILogger<StripeService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        // Configure Stripe
        StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];
        _webhookSecret = _configuration["Stripe:WebhookSecret"] ?? string.Empty;
    }

    public async Task<string> CreateCustomerAsync(Organization organization)
    {
        try
        {
            var options = new CustomerCreateOptions
            {
                Email = organization.EffectiveBillingEmail,
                Name = organization.Name,
                Phone = organization.ContactPhone,
                Metadata = new Dictionary<string, string>
                {
                    { "organization_id", organization.Id.ToString() },
                    { "slug", organization.Slug }
                }
            };

            var service = new CustomerService();
            var customer = await service.CreateAsync(options);

            _logger.LogInformation(
                "Created Stripe customer {CustomerId} for organization {OrganizationId}",
                customer.Id, organization.Id);

            return customer.Id;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to create Stripe customer for organization {OrganizationId}", organization.Id);
            throw;
        }
    }

    public async Task UpdateCustomerAsync(string customerId, Organization organization)
    {
        try
        {
            var options = new CustomerUpdateOptions
            {
                Email = organization.EffectiveBillingEmail,
                Name = organization.Name,
                Phone = organization.ContactPhone
            };

            var service = new CustomerService();
            await service.UpdateAsync(customerId, options);

            _logger.LogInformation("Updated Stripe customer {CustomerId}", customerId);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to update Stripe customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task<StripeSubscriptionResult> CreateSubscriptionAsync(
        string customerId,
        string priceId,
        int? trialDays = null)
    {
        try
        {
            var options = new SubscriptionCreateOptions
            {
                Customer = customerId,
                Items = new List<SubscriptionItemOptions>
                {
                    new() { Price = priceId }
                },
                PaymentBehavior = "default_incomplete",
                PaymentSettings = new SubscriptionPaymentSettingsOptions
                {
                    SaveDefaultPaymentMethod = "on_subscription"
                },
                Expand = new List<string> { "latest_invoice.payment_intent" }
            };

            if (trialDays.HasValue && trialDays > 0)
            {
                options.TrialPeriodDays = trialDays.Value;
            }

            var service = new SubscriptionService();
            var subscription = await service.CreateAsync(options);

            _logger.LogInformation(
                "Created Stripe subscription {SubscriptionId} for customer {CustomerId}",
                subscription.Id, customerId);

            return MapToResult(subscription);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to create Stripe subscription for customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task CancelSubscriptionAsync(string subscriptionId, bool immediately = false)
    {
        try
        {
            var service = new SubscriptionService();

            if (immediately)
            {
                await service.CancelAsync(subscriptionId);
                _logger.LogInformation("Immediately cancelled Stripe subscription {SubscriptionId}", subscriptionId);
            }
            else
            {
                var options = new SubscriptionUpdateOptions
                {
                    CancelAtPeriodEnd = true
                };
                await service.UpdateAsync(subscriptionId, options);
                _logger.LogInformation("Scheduled cancellation for Stripe subscription {SubscriptionId}", subscriptionId);
            }
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to cancel Stripe subscription {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    public async Task<StripeSubscriptionResult> UpdateSubscriptionAsync(
        string subscriptionId,
        string newPriceId)
    {
        try
        {
            var service = new SubscriptionService();
            var subscription = await service.GetAsync(subscriptionId);

            var options = new SubscriptionUpdateOptions
            {
                Items = new List<SubscriptionItemOptions>
                {
                    new()
                    {
                        Id = subscription.Items.Data[0].Id,
                        Price = newPriceId
                    }
                },
                ProrationBehavior = "create_prorations"
            };

            var updatedSubscription = await service.UpdateAsync(subscriptionId, options);

            _logger.LogInformation(
                "Updated Stripe subscription {SubscriptionId} to price {PriceId}",
                subscriptionId, newPriceId);

            return MapToResult(updatedSubscription);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to update Stripe subscription {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    public async Task<CheckoutSessionResult> CreateCheckoutSessionAsync(
        Guid organizationId,
        string priceId,
        string successUrl,
        string cancelUrl,
        string? customerId = null)
    {
        try
        {
            var options = new SessionCreateOptions
            {
                Mode = "subscription",
                LineItems = new List<SessionLineItemOptions>
                {
                    new()
                    {
                        Price = priceId,
                        Quantity = 1
                    }
                },
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                Metadata = new Dictionary<string, string>
                {
                    { "organization_id", organizationId.ToString() }
                },
                SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = new Dictionary<string, string>
                    {
                        { "organization_id", organizationId.ToString() }
                    }
                }
            };

            if (!string.IsNullOrEmpty(customerId))
            {
                options.Customer = customerId;
            }
            else
            {
                options.CustomerCreation = "always";
            }

            var service = new SessionService();
            var session = await service.CreateAsync(options);

            _logger.LogInformation(
                "Created Stripe checkout session {SessionId} for organization {OrganizationId}",
                session.Id, organizationId);

            return new CheckoutSessionResult(session.Id, session.Url);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to create checkout session for organization {OrganizationId}", organizationId);
            throw;
        }
    }

    public async Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl)
    {
        try
        {
            var options = new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = customerId,
                ReturnUrl = returnUrl
            };

            var service = new Stripe.BillingPortal.SessionService();
            var session = await service.CreateAsync(options);

            _logger.LogInformation("Created billing portal session for customer {CustomerId}", customerId);

            return session.Url;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to create billing portal session for customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task<WebhookProcessResult> HandleWebhookAsync(string payload, string signature)
    {
        try
        {
            var stripeEvent = EventUtility.ConstructEvent(payload, signature, _webhookSecret);

            _logger.LogInformation("Processing Stripe webhook event: {EventType}", stripeEvent.Type);

            // PREVIOUSLY: this returned bare (Success, EventType) with a comment claiming "events
            // are processed by the BillingService based on the return value" — but nothing ever
            // read anything from this result except EventType for a log line.
            // BillingController.StripeWebhook's own comment admitted it: "Handle specific events
            // — this would typically be handled by a separate service or background job — for
            // now, just acknowledge receipt." A real payment success or failure did nothing:
            // no subscription reactivation, no organization un-suspension, no payment/invoice
            // records. Extracting the actual subscription/customer/invoice/amount here is what
            // lets the controller act on it for real — see BillingController.StripeWebhook.
            if (stripeEvent.Type is "invoice.payment_succeeded" or "invoice.payment_failed")
            {
                var invoice = stripeEvent.Data.Object as Invoice;
                var subscriptionId = invoice?.Parent?.SubscriptionDetails?.SubscriptionId;
                var failureMessage = stripeEvent.Type == "invoice.payment_failed"
                    ? "Stripe reported the invoice payment attempt failed"
                    : null;

                return new WebhookProcessResult(
                    true,
                    stripeEvent.Type,
                    StripeSubscriptionId: subscriptionId,
                    StripeCustomerId: invoice?.CustomerId,
                    StripeInvoiceId: invoice?.Id,
                    AmountPaid: invoice != null ? invoice.AmountPaid / 100m : null,
                    Currency: invoice?.Currency,
                    FailureMessage: failureMessage);
            }

            return new WebhookProcessResult(true, stripeEvent.Type);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to process Stripe webhook");
            return new WebhookProcessResult(false, "unknown", ex.Message);
        }
    }

    public async Task<StripeSubscriptionResult?> GetSubscriptionAsync(string subscriptionId)
    {
        try
        {
            var service = new SubscriptionService();
            var subscription = await service.GetAsync(subscriptionId);
            return MapToResult(subscription);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to get Stripe subscription {SubscriptionId}", subscriptionId);
            return null;
        }
    }

    public async Task<IEnumerable<PaymentMethodInfo>> GetPaymentMethodsAsync(string customerId)
    {
        try
        {
            var options = new PaymentMethodListOptions
            {
                Customer = customerId,
                Type = "card"
            };

            var service = new PaymentMethodService();
            var paymentMethods = await service.ListAsync(options);

            // Get customer to check default payment method
            var customerService = new CustomerService();
            var customer = await customerService.GetAsync(customerId);
            var defaultPaymentMethodId = customer.InvoiceSettings?.DefaultPaymentMethodId;

            return paymentMethods.Data.Select(pm => new PaymentMethodInfo(
                pm.Id,
                pm.Type,
                pm.Card?.Brand,
                pm.Card?.Last4,
                (int?)pm.Card?.ExpMonth,
                (int?)pm.Card?.ExpYear,
                pm.Id == defaultPaymentMethodId
            ));
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to get payment methods for customer {CustomerId}", customerId);
            return Enumerable.Empty<PaymentMethodInfo>();
        }
    }

    public async Task<bool> SetDefaultPaymentMethodAsync(string customerId, string paymentMethodId)
    {
        try
        {
            var pmService = new PaymentMethodService();
            var paymentMethod = await pmService.GetAsync(paymentMethodId);

            // SECURITY: paymentMethodId comes from the client — a Stripe payment method ID is
            // a global identifier, not scoped to our own org data, so it must be confirmed to
            // actually belong to this customer before it's allowed to affect their account.
            if (paymentMethod.CustomerId != customerId)
            {
                _logger.LogWarning("Payment method {PaymentMethodId} does not belong to customer {CustomerId}", paymentMethodId, customerId);
                return false;
            }

            var customerService = new CustomerService();
            await customerService.UpdateAsync(customerId, new CustomerUpdateOptions
            {
                InvoiceSettings = new CustomerInvoiceSettingsOptions
                {
                    DefaultPaymentMethod = paymentMethodId
                }
            });

            return true;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to set default payment method {PaymentMethodId} for customer {CustomerId}", paymentMethodId, customerId);
            return false;
        }
    }

    public async Task<bool> RemovePaymentMethodAsync(string customerId, string paymentMethodId)
    {
        try
        {
            var pmService = new PaymentMethodService();
            var paymentMethod = await pmService.GetAsync(paymentMethodId);

            // SECURITY: same ownership check as SetDefaultPaymentMethodAsync.
            if (paymentMethod.CustomerId != customerId)
            {
                _logger.LogWarning("Payment method {PaymentMethodId} does not belong to customer {CustomerId}", paymentMethodId, customerId);
                return false;
            }

            await pmService.DetachAsync(paymentMethodId);
            return true;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to remove payment method {PaymentMethodId} for customer {CustomerId}", paymentMethodId, customerId);
            return false;
        }
    }

    public async Task<PaymentIntentResult> CreatePaymentIntentAsync(
        string customerId,
        decimal amount,
        string currency,
        string? description = null)
    {
        try
        {
            var options = new PaymentIntentCreateOptions
            {
                Customer = customerId,
                Amount = (long)(amount * 100), // Convert to cents
                Currency = currency.ToLower(),
                Description = description,
                AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
                {
                    Enabled = true
                }
            };

            var service = new PaymentIntentService();
            var paymentIntent = await service.CreateAsync(options);

            _logger.LogInformation(
                "Created payment intent {PaymentIntentId} for customer {CustomerId}",
                paymentIntent.Id, customerId);

            return new PaymentIntentResult(
                paymentIntent.Id,
                paymentIntent.ClientSecret,
                paymentIntent.Status);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to create payment intent for customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task<ChargeResult> ChargeCustomerAsync(
        string customerId,
        decimal amount,
        string currency,
        string description)
    {
        try
        {
            // Get customer's default payment method
            var customerService = new CustomerService();
            var customer = await customerService.GetAsync(customerId);

            if (string.IsNullOrEmpty(customer.InvoiceSettings?.DefaultPaymentMethodId))
            {
                return new ChargeResult(false, null, "NO_PAYMENT_METHOD", "Customer has no default payment method");
            }

            // Create and confirm a payment intent
            var options = new PaymentIntentCreateOptions
            {
                Customer = customerId,
                Amount = (long)(amount * 100), // Convert to cents
                Currency = currency.ToLower(),
                Description = description,
                PaymentMethod = customer.InvoiceSettings.DefaultPaymentMethodId,
                Confirm = true,
                OffSession = true
            };

            var service = new PaymentIntentService();
            var paymentIntent = await service.CreateAsync(options);

            if (paymentIntent.Status == "succeeded")
            {
                _logger.LogInformation(
                    "Successfully charged customer {CustomerId} for {Amount} {Currency}",
                    customerId, amount, currency);

                return new ChargeResult(true, paymentIntent.Id, null, null);
            }

            return new ChargeResult(false, null, "PAYMENT_FAILED", $"Payment status: {paymentIntent.Status}");
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to charge customer {CustomerId}", customerId);
            return new ChargeResult(false, null, ex.StripeError?.Code ?? "STRIPE_ERROR", ex.Message);
        }
    }

    private static StripeSubscriptionResult MapToResult(Stripe.Subscription subscription)
    {
        // Stripe API v48 moved CurrentPeriodStart/End from Subscription itself
        // down to each SubscriptionItem (to support multi-item subscriptions
        // with different billing periods per item) — confirmed via the
        // installed Stripe.net package's own XML docs. We only ever create
        // single-item subscriptions, so the first item's period is the
        // subscription's period. Falls back to now/+1 month only if Stripe
        // ever returns a subscription with no items at all, which shouldn't
        // happen for an active subscription but keeps this from throwing.
        var firstItem = subscription.Items?.Data?.FirstOrDefault();
        var startDate = firstItem?.CurrentPeriodStart ?? DateTime.UtcNow;
        var endDate = firstItem?.CurrentPeriodEnd ?? DateTime.UtcNow.AddMonths(1);

        return new StripeSubscriptionResult(
            subscription.Id,
            subscription.CustomerId,
            subscription.Status,
            startDate,
            endDate,
            subscription.TrialEnd,
            subscription.LatestInvoiceId,
            subscription.DefaultPaymentMethodId);
    }
}
