using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Entities.Platform;
using Stripe;
using Stripe.Checkout;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// Stripe payment integration service for card payments
/// </summary>
public class StripeService : IStripeService
{
    private readonly IConfiguration _configuration;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly ILogger<StripeService> _logger;
    private string _webhookSecret = string.Empty;
    private bool _configured;
    private bool _enabled;

    public StripeService(
        IConfiguration configuration,
        IPlatformSettingsService platformSettings,
        ILogger<StripeService> logger)
    {
        _configuration = configuration;
        _platformSettings = platformSettings;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the effective Stripe credentials. The Platform Settings admin UI (PlatformSetting
    /// row, Category="Stripe") is the primary source, so an edit there takes effect on the next
    /// request (the settings service caches per category and clears that key on save);
    /// appsettings / environment variables are the fallback for any field left blank in the UI.
    /// Previously the constructor read IConfiguration only, so the admin UI looked functional
    /// but changed nothing.
    /// </summary>
    private async Task EnsureConfiguredAsync()
    {
        if (_configured) return;

        StripeSettings? db = null;
        try
        {
            db = await _platformSettings.GetSettingsAsync<StripeSettings>("Stripe");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Stripe platform settings; falling back to configuration");
        }

        var secretKey = FirstNonEmpty(db?.SecretKey, _configuration["Stripe:SecretKey"]);
        _webhookSecret = FirstNonEmpty(db?.WebhookSecret, _configuration["Stripe:WebhookSecret"]) ?? string.Empty;
        _enabled = (db?.Enabled ?? true) && !string.IsNullOrWhiteSpace(secretKey);

        StripeConfiguration.ApiKey = secretKey;
        _configured = true;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public async Task<bool> IsConfiguredAsync()
    {
        await EnsureConfiguredAsync();
        return _enabled;
    }

    public async Task<string> CreateCustomerAsync(Organization organization)
    {
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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

    public async Task<CheckoutSessionResult> CreateModuleCheckoutSessionAsync(
        Guid organizationId,
        string moduleCode,
        string priceId,
        string successUrl,
        string cancelUrl,
        string? customerId = null,
        string billingCycle = "Monthly")
    {
        await EnsureConfiguredAsync();
        try
        {
            var metadata = new Dictionary<string, string>
            {
                { "organization_id", organizationId.ToString() },
                { "module_code", moduleCode },
                { "billing_cycle", billingCycle }
            };

            var options = new SessionCreateOptions
            {
                Mode = "subscription",
                LineItems = new List<SessionLineItemOptions>
                {
                    new() { Price = priceId, Quantity = 1 }
                },
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                Metadata = metadata,
                SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = metadata
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
                "Created module checkout session {SessionId} for organization {OrganizationId}, module {ModuleCode}",
                session.Id, organizationId, moduleCode);

            return new CheckoutSessionResult(session.Id, session.Url);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to create module checkout session for organization {OrganizationId}, module {ModuleCode}", organizationId, moduleCode);
            throw;
        }
    }

    public async Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl)
    {
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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

            // Module-purchase Checkout Sessions (CreateModuleCheckoutSessionAsync) tag their
            // session — and the subscription Stripe creates alongside it — with organization_id/
            // module_code metadata specifically so this event can activate the right
            // OrganizationModule without any other lookup. Ignores a checkout session that isn't
            // a module purchase (e.g. the legacy CreateCheckoutSessionAsync flow, which doesn't
            // set module_code) by simply finding no metadata to act on.
            if (stripeEvent.Type == "checkout.session.completed")
            {
                var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
                if (session?.Metadata != null
                    && session.Metadata.TryGetValue("organization_id", out var orgIdStr)
                    && session.Metadata.TryGetValue("module_code", out var moduleCode)
                    && Guid.TryParse(orgIdStr, out var orgId)
                    && !string.IsNullOrEmpty(session.SubscriptionId))
                {
                    // The Checkout Session created a brand-new single-item subscription for this
                    // first module — its one item is what every later AddSubscriptionItemAsync
                    // call joins.
                    var subscriptionService = new SubscriptionService();
                    var subscription = await subscriptionService.GetAsync(session.SubscriptionId);
                    var newItemId = subscription.Items?.Data?.FirstOrDefault()?.Id;
                    session.Metadata.TryGetValue("billing_cycle", out var billingCycle);

                    return new WebhookProcessResult(
                        true,
                        stripeEvent.Type,
                        StripeSubscriptionId: session.SubscriptionId,
                        StripeCustomerId: session.CustomerId,
                        ModuleOrganizationId: orgId,
                        ModuleCode: moduleCode,
                        NewSubscriptionItemId: newItemId,
                        ModuleBillingCycle: billingCycle);
                }
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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
        await EnsureConfiguredAsync();
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

    public async Task<StripeSubscriptionResult> CreateMultiItemSubscriptionAsync(
        string customerId,
        IEnumerable<string> priceIds,
        int? trialDays = null)
    {
        var items = priceIds.Select(priceId => new SubscriptionItemOptions { Price = priceId }).ToList();
        if (items.Count == 0)
            throw new ArgumentException("At least one price ID is required.", nameof(priceIds));

        try
        {
            var options = new SubscriptionCreateOptions
            {
                Customer = customerId,
                Items = items,
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
                "Created multi-item Stripe subscription {SubscriptionId} for customer {CustomerId} with {ItemCount} item(s)",
                subscription.Id, customerId, items.Count);

            return MapToResult(subscription);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to create multi-item Stripe subscription for customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task<string> AddSubscriptionItemAsync(string subscriptionId, string priceId)
    {
        await EnsureConfiguredAsync();
        try
        {
            var itemService = new SubscriptionItemService();
            var item = await itemService.CreateAsync(new SubscriptionItemCreateOptions
            {
                Subscription = subscriptionId,
                Price = priceId,
                ProrationBehavior = "create_prorations"
            });

            _logger.LogInformation(
                "Added subscription item {ItemId} (price {PriceId}) to Stripe subscription {SubscriptionId}",
                item.Id, priceId, subscriptionId);

            return item.Id;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to add price {PriceId} to Stripe subscription {SubscriptionId}", priceId, subscriptionId);
            throw;
        }
    }

    public async Task RemoveSubscriptionItemAsync(string subscriptionItemId)
    {
        await EnsureConfiguredAsync();
        try
        {
            var itemService = new SubscriptionItemService();
            await itemService.DeleteAsync(subscriptionItemId, new SubscriptionItemDeleteOptions
            {
                ProrationBehavior = "create_prorations"
            });

            _logger.LogInformation("Removed Stripe subscription item {ItemId}", subscriptionItemId);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to remove Stripe subscription item {ItemId}", subscriptionItemId);
            throw;
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
