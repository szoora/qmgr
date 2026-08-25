using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.API.Authorization;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace QMgr.Controllers.v1;

/// <summary>
/// Billing and subscription management endpoints
/// </summary>
[ApiController]
[Route("api/v1/billing")]
[Authorize]
public class BillingController : ControllerBase
{
    private readonly IBillingService _billingService;
    private readonly IStripeService _stripeService;
    private readonly IMobileMoneyService _mobileMoneyService;
    private readonly IUsageTrackingService _usageTrackingService;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        IBillingService billingService,
        IStripeService stripeService,
        IMobileMoneyService mobileMoneyService,
        IUsageTrackingService usageTrackingService,
        ITenantContextAccessor tenantContextAccessor,
        IConfiguration configuration,
        IPlatformSettingsService platformSettingsService,
        ILogger<BillingController> logger)
    {
        _billingService = billingService;
        _stripeService = stripeService;
        _mobileMoneyService = mobileMoneyService;
        _usageTrackingService = usageTrackingService;
        _tenantContextAccessor = tenantContextAccessor;
        _configuration = configuration;
        _platformSettingsService = platformSettingsService;
        _logger = logger;
    }

    // Was IConfiguration-only, completely disconnected from the "SaaS" PlatformSetting row
    // the admin UI actually edits. GetSettingsAsync is memory-cached (30 min, invalidated on
    // save), so this is cheap to call per-request.
    private async Task<string> GetBaseUrlAsync()
    {
        var saas = await _platformSettingsService.GetSettingsAsync<SaasSettings>("SaaS");
        return saas?.BaseUrl ?? _configuration["SaaS:BaseUrl"] ?? "https://qmgr.app";
    }

    private Guid OrganizationId => _tenantContextAccessor.TenantContext?.OrganizationId ?? Guid.Empty;

    #region Overview

    /// <summary>
    /// Get billing overview (subscription, usage, limits)
    /// </summary>
    [HttpGet("overview")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetOverview()
    {
        var subscription = await _billingService.GetSubscriptionWithPlanAsync(OrganizationId);
        var usage = await _usageTrackingService.GetCurrentUsageAsync(OrganizationId);
        var limits = await _billingService.GetLimitsAsync(OrganizationId);

        if (subscription == null)
        {
            var trialInfo = await _billingService.GetOrganizationTrialInfoAsync(OrganizationId);
            return Ok(new
            {
                hasSubscription = false,
                message = "No active subscription",
                status = trialInfo?.Status.ToString(),
                trialEndsAt = trialInfo?.TrialEndsAt
            });
        }

        return Ok(new BillingOverviewDto
        {
            // Subscription Info
            PlanName = subscription.Plan.Name,
            PlanCode = subscription.Plan.Code,
            BillingCycle = subscription.Subscription.BillingCycle.ToString(),
            MonthlyAmount = subscription.Subscription.BillingCycle == BillingCycle.Monthly
                ? subscription.Plan.MonthlyPriceUsd
                : subscription.Plan.AnnualPriceUsd / 12,
            Currency = "USD",
            Status = subscription.Subscription.Status.ToString(),
            NextBillingDate = subscription.Subscription.CurrentPeriodEnd,
            TrialEndsAt = subscription.Subscription.TrialEnd,
            CancelAtPeriodEnd = subscription.Subscription.CancelAtPeriodEnd,

            // Usage Info
            ActiveUsers = usage.ActiveUsers,
            MaxUsers = limits.Limits.ContainsKey("users") ? limits.Limits["users"].MaxAllowed : 0,
            ActiveBranches = usage.ActiveBranches,
            MaxBranches = limits.Limits.ContainsKey("branches") ? limits.Limits["branches"].MaxAllowed : 0,
            TokensThisMonth = usage.TokensCreated,
            MaxTokensPerMonth = limits.Limits.ContainsKey("tokens") ? limits.Limits["tokens"].MaxAllowed : 0,
            ApiCallsThisMonth = usage.ApiCalls,
            MaxApiCallsPerMonth = limits.Limits.ContainsKey("apiCalls") ? limits.Limits["apiCalls"].MaxAllowed : 0,
            StorageUsedBytes = usage.StorageUsedBytes,
            MaxStorageBytes = limits.Limits.ContainsKey("storage") ? limits.Limits["storage"].MaxAllowed : 0
        });
    }

    #endregion

    #region Subscription Plans

    /// <summary>
    /// Get all available subscription plans
    /// </summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPlans()
    {
        var plans = await _billingService.GetPlansAsync();

        var response = plans.Select(p => new PlanDto
        {
            Id = p.Id,
            Name = p.Name,
            Code = p.Code,
            Description = p.Description,
            Tier = p.Tier.ToString(),
            MonthlyPriceUsd = p.MonthlyPriceUsd,
            AnnualPriceUsd = p.AnnualPriceUsd,
            MonthlyPriceUgx = p.MonthlyPriceUgx,
            AnnualPriceUgx = p.AnnualPriceUgx,
            MaxBranches = p.MaxBranches,
            MaxUsersPerBranch = p.MaxUsersPerBranch,
            MaxTokensPerMonth = p.MaxTokensPerMonth,
            MaxApiCallsPerMonth = p.MaxApiCallsPerMonth,
            ShowAds = p.ShowAds,
            TrialDays = p.TrialDays,
            Badge = p.Badge,
            Features = p.Features
        });

        return Ok(response);
    }

    /// <summary>
    /// Get a specific plan by code
    /// </summary>
    [HttpGet("plans/{code}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPlan(string code)
    {
        var plan = await _billingService.GetPlanByCodeAsync(code);
        if (plan == null)
            return NotFound(new { message = "Plan not found" });

        return Ok(new PlanDto
        {
            Id = plan.Id,
            Name = plan.Name,
            Code = plan.Code,
            Description = plan.Description,
            Tier = plan.Tier.ToString(),
            MonthlyPriceUsd = plan.MonthlyPriceUsd,
            AnnualPriceUsd = plan.AnnualPriceUsd,
            MonthlyPriceUgx = plan.MonthlyPriceUgx,
            AnnualPriceUgx = plan.AnnualPriceUgx,
            MaxBranches = plan.MaxBranches,
            MaxUsersPerBranch = plan.MaxUsersPerBranch,
            MaxTokensPerMonth = plan.MaxTokensPerMonth,
            MaxApiCallsPerMonth = plan.MaxApiCallsPerMonth,
            ShowAds = plan.ShowAds,
            TrialDays = plan.TrialDays,
            Badge = plan.Badge,
            Features = plan.Features
        });
    }

    #endregion

    #region Subscriptions

    /// <summary>
    /// Get current subscription for the organization
    /// </summary>
    [HttpGet("subscription")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetSubscription()
    {
        var subscription = await _billingService.GetSubscriptionWithPlanAsync(OrganizationId);
        if (subscription == null)
        {
            var trialInfo = await _billingService.GetOrganizationTrialInfoAsync(OrganizationId);
            return Ok(new
            {
                hasSubscription = false,
                status = trialInfo?.Status.ToString(),
                trialEndsAt = trialInfo?.TrialEndsAt
            });
        }

        return Ok(new SubscriptionDto
        {
            Id = subscription.Subscription.Id,
            PlanCode = subscription.Plan.Code,
            PlanName = subscription.Plan.Name,
            Status = subscription.Subscription.Status.ToString(),
            BillingCycle = subscription.Subscription.BillingCycle.ToString(),
            CurrentPeriodStart = subscription.Subscription.CurrentPeriodStart,
            CurrentPeriodEnd = subscription.Subscription.CurrentPeriodEnd,
            TrialEnd = subscription.Subscription.TrialEnd,
            CancelAtPeriodEnd = subscription.Subscription.CancelAtPeriodEnd,
            Limits = subscription.Limits
        });
    }

    /// <summary>
    /// Create a new subscription
    /// </summary>
    [HttpPost("subscribe")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest request)
    {
        var result = await _billingService.CreateSubscriptionAsync(
            OrganizationId,
            request.PlanCode,
            request.BillingCycle,
            request.PaymentMethod,
            request.StripePaymentMethodId,
            request.MobileMoneyPhone);

        if (!result.Success)
            return BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });

        return Ok(new { subscriptionId = result.Subscription?.Id, message = "Subscription created successfully" });
    }

    /// <summary>
    /// Change subscription plan
    /// </summary>
    [HttpPost("change-plan")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> ChangePlan([FromBody] ChangePlanRequest request)
    {
        var subscription = await _billingService.GetSubscriptionAsync(OrganizationId);
        if (subscription == null)
            return NotFound(new { message = "No active subscription found" });

        var result = await _billingService.ChangePlanAsync(
            subscription.Id,
            request.NewPlanCode,
            request.ImmediateChange);

        if (!result.Success)
            return BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });

        return Ok(new { message = "Plan changed successfully" });
    }

    /// <summary>
    /// Cancel subscription
    /// </summary>
    [HttpPost("cancel")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> CancelSubscription([FromBody] CancelRequest? request)
    {
        var subscription = await _billingService.GetSubscriptionAsync(OrganizationId);
        if (subscription == null)
            return NotFound(new { message = "No active subscription found" });

        var result = await _billingService.CancelSubscriptionAsync(
            subscription.Id,
            request?.Reason,
            request?.Immediately ?? false);

        if (!result.Success)
            return BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });

        return Ok(new
        {
            message = request?.Immediately == true
                ? "Subscription cancelled immediately"
                : "Subscription will be cancelled at the end of the billing period"
        });
    }

    /// <summary>
    /// Reactivate a cancelled subscription
    /// </summary>
    [HttpPost("reactivate")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> Reactivate()
    {
        var subscription = await _billingService.GetSubscriptionAsync(OrganizationId);
        if (subscription == null)
            return NotFound(new { message = "No subscription found" });

        var result = await _billingService.ReactivateSubscriptionAsync(subscription.Id);

        if (!result.Success)
            return BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });

        return Ok(new { message = "Subscription reactivated successfully" });
    }

    #endregion

    #region Stripe Integration

    /// <summary>
    /// Create Stripe checkout session for subscription
    /// </summary>
    [HttpPost("checkout-session")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CheckoutRequest request)
    {
        var plan = await _billingService.GetPlanByCodeAsync(request.PlanCode);
        if (plan == null)
            return NotFound(new { message = "Plan not found" });

        var priceId = request.BillingCycle == BillingCycle.Annual
            ? plan.StripePriceIdAnnual
            : plan.StripePriceIdMonthly;

        if (string.IsNullOrEmpty(priceId))
            return BadRequest(new { message = "Stripe pricing not configured for this plan" });

        var baseUrl = await GetBaseUrlAsync();
        var successUrl = $"{baseUrl}/billing/success?session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{baseUrl}/billing/cancelled";

        var result = await _stripeService.CreateCheckoutSessionAsync(
            OrganizationId,
            priceId,
            successUrl,
            cancelUrl);

        return Ok(new { sessionId = result.SessionId, url = result.Url });
    }

    /// <summary>
    /// Create Stripe billing portal session
    /// </summary>
    [HttpPost("portal-session")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> CreatePortalSession([FromBody] PortalRequest? request)
    {
        var subscription = await _billingService.GetSubscriptionAsync(OrganizationId);
        if (subscription == null || string.IsNullOrEmpty(subscription.StripeCustomerId))
            return BadRequest(new { message = "No Stripe customer found" });

        var returnUrl = request?.ReturnUrl ?? $"{await GetBaseUrlAsync()}/billing";

        var portalUrl = await _stripeService.CreateBillingPortalSessionAsync(
            subscription.StripeCustomerId,
            returnUrl);

        return Ok(new { url = portalUrl });
    }

    /// <summary>
    /// Get payment methods on file
    /// </summary>
    [HttpGet("payment-methods")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetPaymentMethods()
    {
        var subscription = await _billingService.GetSubscriptionAsync(OrganizationId);
        if (subscription == null || string.IsNullOrEmpty(subscription.StripeCustomerId))
            return Ok(new { paymentMethods = Array.Empty<object>() });

        var methods = await _stripeService.GetPaymentMethodsAsync(subscription.StripeCustomerId);

        return Ok(new { paymentMethods = methods });
    }

    /// <summary>
    /// Sets a payment method on file as the default
    /// </summary>
    [HttpPost("payment-methods/{paymentMethodId}/set-default")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> SetDefaultPaymentMethod(string paymentMethodId)
    {
        var subscription = await _billingService.GetSubscriptionAsync(OrganizationId);
        if (subscription == null || string.IsNullOrEmpty(subscription.StripeCustomerId))
            return BadRequest(new { message = "No Stripe customer found" });

        var success = await _stripeService.SetDefaultPaymentMethodAsync(subscription.StripeCustomerId, paymentMethodId);
        if (!success)
            return NotFound(new { message = "Payment method not found" });

        return Ok(new { message = "Default payment method updated" });
    }

    /// <summary>
    /// Removes a payment method on file
    /// </summary>
    [HttpDelete("payment-methods/{paymentMethodId}")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> RemovePaymentMethod(string paymentMethodId)
    {
        var subscription = await _billingService.GetSubscriptionAsync(OrganizationId);
        if (subscription == null || string.IsNullOrEmpty(subscription.StripeCustomerId))
            return BadRequest(new { message = "No Stripe customer found" });

        var success = await _stripeService.RemovePaymentMethodAsync(subscription.StripeCustomerId, paymentMethodId);
        if (!success)
            return NotFound(new { message = "Payment method not found" });

        return NoContent();
    }

    /// <summary>
    /// Which payment providers are currently enabled platform-wide. Deliberately returns only
    /// booleans, not the underlying settings — the real Platform Settings data (secret keys
    /// included) is SuperAdmin-only (RequirePermission("platform.settings.view")); any
    /// authenticated tenant user needs to know whether to show "Add Payment Method" at all.
    /// </summary>
    [HttpGet("payment-providers")]
    public async Task<IActionResult> GetPaymentProviders()
    {
        var stripe = await _platformSettingsService.GetSettingsAsync<StripeSettings>("Stripe");
        var mobileMoney = await _platformSettingsService.GetSettingsAsync<MobileMoneySettings>("MobileMoney");

        return Ok(new
        {
            stripeEnabled = stripe?.Enabled ?? false,
            mobileMoneyEnabled = mobileMoney?.Enabled ?? false
        });
    }

    #endregion

    #region Mobile Money

    /// <summary>
    /// Get supported mobile money channels
    /// </summary>
    [HttpGet("mobile-money/channels")]
    [AllowAnonymous]
    public IActionResult GetMobileMoneyChannels()
    {
        var channels = _mobileMoneyService.GetSupportedChannels();
        return Ok(channels);
    }

    /// <summary>
    /// Validate mobile money phone number
    /// </summary>
    [HttpPost("mobile-money/validate")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> ValidatePhone([FromBody] ValidatePhoneRequest request)
    {
        var result = await _mobileMoneyService.ValidatePhoneAsync(request.PhoneNumber);

        return Ok(new
        {
            isValid = result.IsValid,
            normalizedPhone = result.NormalizedPhone,
            channel = result.Channel,
            carrier = result.Carrier,
            error = result.ErrorMessage
        });
    }

    /// <summary>
    /// Initiate mobile money payment
    /// </summary>
    [HttpPost("mobile-money/pay")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> InitiateMobileMoneyPayment([FromBody] MobileMoneyPayRequest request)
    {
        var plan = await _billingService.GetPlanByCodeAsync(request.PlanCode);
        if (plan == null)
            return NotFound(new { message = "Plan not found" });

        var amount = request.BillingCycle == BillingCycle.Annual
            ? plan.AnnualPriceUgx
            : plan.MonthlyPriceUgx;

        var narrative = $"Q-Mgr {plan.Name} subscription ({request.BillingCycle})";

        var result = await _mobileMoneyService.CollectPaymentAsync(
            OrganizationId,
            request.PhoneNumber,
            amount,
            "UGX",
            narrative);

        if (!result.Success)
            return BadRequest(new
            {
                error = result.ErrorCode,
                message = result.ErrorMessage,
                customerMessage = result.CustomerMessage
            });

        return Ok(new
        {
            transactionId = result.TransactionId,
            status = result.Status.ToString(),
            message = "Payment initiated. Please check your phone to confirm."
        });
    }

    /// <summary>
    /// Check mobile money payment status
    /// </summary>
    [HttpGet("mobile-money/status/{transactionId}")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> CheckMobileMoneyStatus(string transactionId)
    {
        var result = await _mobileMoneyService.CheckPaymentStatusAsync(transactionId);

        return Ok(new
        {
            transactionId = result.TransactionId,
            status = result.Status.ToString(),
            amount = result.Amount,
            currency = result.Currency,
            completedAt = result.CompletedAt,
            error = result.ErrorMessage
        });
    }

    #endregion

    #region Invoices

    /// <summary>
    /// Get invoices for the organization
    /// </summary>
    [HttpGet("invoices")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetInvoices([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var invoices = await _billingService.GetInvoicesAsync(OrganizationId, page, pageSize);

        var response = invoices.Select(i => new InvoiceDto
        {
            Id = i.Id,
            InvoiceNumber = i.InvoiceNumber,
            Status = i.Status.ToString(),
            Currency = i.Currency,
            Subtotal = i.Subtotal,
            TaxAmount = i.TaxAmount,
            Total = i.Total,
            AmountPaid = i.AmountPaid,
            InvoiceDate = i.InvoiceDate,
            DueDate = i.DueDate,
            PaidAt = i.PaidAt,
            PdfUrl = i.StripePdfUrl
        });

        return Ok(response);
    }

    /// <summary>
    /// Get a specific invoice
    /// </summary>
    [HttpGet("invoices/{id}")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetInvoice(Guid id)
    {
        var invoice = await _billingService.GetInvoiceAsync(id);
        if (invoice == null || invoice.OrganizationId != OrganizationId)
            return NotFound(new { message = "Invoice not found" });

        return Ok(new InvoiceDto
        {
            Id = invoice.Id,
            InvoiceNumber = invoice.InvoiceNumber,
            Status = invoice.Status.ToString(),
            Currency = invoice.Currency,
            Subtotal = invoice.Subtotal,
            TaxAmount = invoice.TaxAmount,
            Total = invoice.Total,
            AmountPaid = invoice.AmountPaid,
            InvoiceDate = invoice.InvoiceDate,
            DueDate = invoice.DueDate,
            PaidAt = invoice.PaidAt,
            PdfUrl = invoice.StripePdfUrl,
            LineItems = invoice.LineItems,
            BillingName = invoice.BillingName,
            BillingEmail = invoice.BillingEmail,
            OrganizationAddress = invoice.Organization?.Address,
            OrganizationPhone = invoice.Organization?.ContactPhone ?? invoice.Organization?.BillingPhone
        });
    }

    #endregion

    #region History

    /// <summary>
    /// Get a chronological billing event timeline for the organization — subscription start/
    /// cancellation, invoices issued/paid, and completed payments — built from the existing
    /// Subscription/Invoice/Payment records (no separate "billing events" table; this endpoint
    /// didn't exist at all before, Subscription.razor's "Billing History" section called it and
    /// got a silent 404).
    /// </summary>
    [HttpGet("history")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetBillingHistory([FromQuery] int limit = 20)
    {
        var events = new List<BillingEventDto>();

        var subscription = await _billingService.GetSubscriptionAsync(OrganizationId);
        if (subscription != null)
        {
            events.Add(new BillingEventDto
            {
                Type = "upgrade",
                Title = "Subscription started",
                Description = $"{subscription.BillingCycle} billing",
                Date = subscription.StartDate
            });

            if (subscription.CancelledAt.HasValue)
            {
                events.Add(new BillingEventDto
                {
                    Type = "subscription",
                    Title = "Subscription cancelled",
                    Description = subscription.CancellationReason ?? "No reason given",
                    Date = subscription.CancelledAt.Value
                });
            }
        }

        var invoices = await _billingService.GetInvoicesAsync(OrganizationId, 1, limit);
        foreach (var invoice in invoices)
        {
            events.Add(new BillingEventDto
            {
                Type = "invoice",
                Title = $"Invoice {invoice.InvoiceNumber} issued",
                Description = $"{invoice.Currency} {invoice.Total:N2} due {invoice.DueDate:MMM dd, yyyy}",
                Date = invoice.InvoiceDate
            });

            if (invoice.PaidAt.HasValue)
            {
                events.Add(new BillingEventDto
                {
                    Type = "payment",
                    Title = $"Invoice {invoice.InvoiceNumber} paid",
                    Description = $"{invoice.Currency} {invoice.AmountPaid:N2}",
                    Date = invoice.PaidAt.Value
                });
            }
        }

        var payments = await _billingService.GetPaymentsAsync(OrganizationId, 1, limit);
        foreach (var payment in payments.Where(p => p.Status == PaymentStatus.Succeeded && p.CompletedAt.HasValue))
        {
            events.Add(new BillingEventDto
            {
                Type = "payment",
                Title = "Payment received",
                Description = $"{payment.Currency} {payment.Amount:N2} via {payment.PaymentMethod}",
                Date = payment.CompletedAt!.Value
            });
        }

        var response = events
            .OrderByDescending(e => e.Date)
            .Take(limit)
            .ToList();

        return Ok(response);
    }

    #endregion

    #region Usage

    /// <summary>
    /// Get current usage metrics
    /// </summary>
    [HttpGet("usage")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetUsage()
    {
        var usage = await _usageTrackingService.GetCurrentUsageAsync(OrganizationId);

        return Ok(new UsageDto
        {
            Year = usage.Year,
            Month = usage.Month,
            TokensCreated = usage.TokensCreated,
            TokensServed = usage.TokensServed,
            ApiCalls = usage.ApiCalls,
            ActiveUsers = usage.ActiveUsers,
            ActiveBranches = usage.ActiveBranches,
            StorageUsedMb = usage.StorageUsedBytes / 1024 / 1024,
            SmsSent = usage.SmsMessagesSent,
            EmailsSent = usage.EmailsSent,
            AdImpressions = usage.AdImpressions
        });
    }

    /// <summary>
    /// Get usage history
    /// </summary>
    [HttpGet("usage/history")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetUsageHistory([FromQuery] int months = 12)
    {
        var history = await _usageTrackingService.GetUsageHistoryAsync(OrganizationId, months);

        return Ok(history.Select(u => new UsageDto
        {
            Year = u.Year,
            Month = u.Month,
            TokensCreated = u.TokensCreated,
            TokensServed = u.TokensServed,
            ApiCalls = u.ApiCalls,
            ActiveUsers = u.ActiveUsers,
            ActiveBranches = u.ActiveBranches,
            StorageUsedMb = u.StorageUsedBytes / 1024 / 1024,
            SmsSent = u.SmsMessagesSent,
            EmailsSent = u.EmailsSent,
            AdImpressions = u.AdImpressions
        }));
    }

    /// <summary>
    /// Get usage limits and current status
    /// </summary>
    [HttpGet("limits")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> GetLimits()
    {
        var limits = await _billingService.GetLimitsAsync(OrganizationId);

        return Ok(new
        {
            organizationId = limits.OrganizationId,
            tier = limits.Tier.ToString(),
            limits = limits.Limits.ToDictionary(
                kv => kv.Key,
                kv => new
                {
                    current = kv.Value.CurrentUsage,
                    max = kv.Value.MaxAllowed,
                    remaining = kv.Value.Remaining,
                    percentUsed = kv.Value.PercentageUsed,
                    withinLimit = kv.Value.IsWithinLimit
                })
        });
    }

    #endregion

    #region Stripe Webhook

    /// <summary>
    /// Stripe webhook endpoint
    /// </summary>
    [HttpPost("webhook/stripe")]
    [AllowAnonymous]
    public async Task<IActionResult> StripeWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault() ?? string.Empty;

        var result = await _stripeService.HandleWebhookAsync(json, signature);

        if (!result.Success)
        {
            _logger.LogWarning("Stripe webhook failed: {Error}", result.ErrorMessage);
            return BadRequest(new { error = result.ErrorMessage });
        }

        _logger.LogInformation("Processed Stripe webhook: {EventType}", result.EventType);

        // PREVIOUSLY a no-op — see StripeService.HandleWebhookAsync's comment for what that
        // actually meant in practice (a real payment succeeding or failing changed nothing).
        if (result.EventType is "invoice.payment_succeeded" or "invoice.payment_failed" &&
            !string.IsNullOrEmpty(result.StripeSubscriptionId))
        {
            var subscription = await _billingService.GetSubscriptionByStripeIdAsync(result.StripeSubscriptionId);
            if (subscription == null)
            {
                _logger.LogWarning(
                    "Stripe webhook {EventType} referenced unknown subscription {StripeSubscriptionId}",
                    result.EventType, result.StripeSubscriptionId);
            }
            else if (result.EventType == "invoice.payment_succeeded")
            {
                await _billingService.HandlePaymentSuccessAsync(
                    subscription.Id,
                    result.AmountPaid ?? 0,
                    result.Currency ?? "usd",
                    result.StripeInvoiceId);
            }
            else
            {
                await _billingService.HandlePaymentFailureAsync(subscription.Id, result.FailureMessage);
            }
        }

        return Ok();
    }

    #endregion
}

#region DTOs

public class BillingOverviewDto
{
    public string PlanName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string BillingCycle { get; set; } = string.Empty;
    public decimal MonthlyAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime NextBillingDate { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public bool CancelAtPeriodEnd { get; set; }

    public int ActiveUsers { get; set; }
    public int MaxUsers { get; set; }
    public int ActiveBranches { get; set; }
    public int MaxBranches { get; set; }
    public int TokensThisMonth { get; set; }
    public int MaxTokensPerMonth { get; set; }
    public int ApiCallsThisMonth { get; set; }
    public int MaxApiCallsPerMonth { get; set; }
    public long StorageUsedBytes { get; set; }
    public long MaxStorageBytes { get; set; }
}

public class PlanDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Tier { get; set; } = string.Empty;
    public decimal MonthlyPriceUsd { get; set; }
    public decimal AnnualPriceUsd { get; set; }
    public decimal MonthlyPriceUgx { get; set; }
    public decimal AnnualPriceUgx { get; set; }
    public int MaxBranches { get; set; }
    public int MaxUsersPerBranch { get; set; }
    public int MaxTokensPerMonth { get; set; }
    public int MaxApiCallsPerMonth { get; set; }
    public bool ShowAds { get; set; }
    public int TrialDays { get; set; }
    public string? Badge { get; set; }
    public string? Features { get; set; }
}

public class SubscriptionDto
{
    public Guid Id { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string BillingCycle { get; set; } = string.Empty;
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public DateTime? TrialEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public EffectiveLimits? Limits { get; set; }
}

public class SubscribeRequest
{
    [Required]
    public string PlanCode { get; set; } = string.Empty;
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Card;
    public string? StripePaymentMethodId { get; set; }
    public string? MobileMoneyPhone { get; set; }
}

public class ChangePlanRequest
{
    [Required]
    public string NewPlanCode { get; set; } = string.Empty;
    public bool ImmediateChange { get; set; }
}

public class CancelRequest
{
    public string? Reason { get; set; }
    public bool Immediately { get; set; }
}

public class CheckoutRequest
{
    [Required]
    public string PlanCode { get; set; } = string.Empty;
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;
}

public class PortalRequest
{
    public string? ReturnUrl { get; set; }
}

public class ValidatePhoneRequest
{
    [Required]
    public string PhoneNumber { get; set; } = string.Empty;
}

public class MobileMoneyPayRequest
{
    [Required]
    public string PlanCode { get; set; } = string.Empty;
    [Required]
    public string PhoneNumber { get; set; } = string.Empty;
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;
}

public class InvoiceDto
{
    public Guid Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public DateTime InvoiceDate { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PdfUrl { get; set; }
    public string? LineItems { get; set; }

    // "Bill To" block — only populated on the single-invoice detail endpoint, not the list.
    public string? BillingName { get; set; }
    public string? BillingEmail { get; set; }
    public string? OrganizationAddress { get; set; }
    public string? OrganizationPhone { get; set; }
}

/// <summary>
/// One entry in the billing history timeline. Type drives the Web page's timeline-marker color
/// ("payment" = success/green, "upgrade" = info/blue, anything else = default wine) — see
/// Subscription.razor's .timeline-marker CSS. Field names match the Web project's local
/// BillingEvent record exactly (Type/Title/Description/Date) so the existing deserialization
/// works with no further mapping needed.
/// </summary>
public class BillingEventDto
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Date { get; set; }
}

public class UsageDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int TokensCreated { get; set; }
    public int TokensServed { get; set; }
    public int ApiCalls { get; set; }
    public int ActiveUsers { get; set; }
    public int ActiveBranches { get; set; }
    public long StorageUsedMb { get; set; }
    public int SmsSent { get; set; }
    public int EmailsSent { get; set; }
    public int AdImpressions { get; set; }
}

#endregion
