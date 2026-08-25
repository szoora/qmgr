using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using System.Text.Json;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// Core billing service for subscription management
/// </summary>
public class BillingService : IBillingService
{
    private readonly QMgrDbContext _dbContext;
    private readonly IStripeService _stripeService;
    private readonly IMobileMoneyService _mobileMoneyService;
    private readonly IUsageTrackingService _usageTrackingService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        QMgrDbContext dbContext,
        IStripeService stripeService,
        IMobileMoneyService mobileMoneyService,
        IUsageTrackingService usageTrackingService,
        IConfiguration configuration,
        ILogger<BillingService> logger)
    {
        _dbContext = dbContext;
        _stripeService = stripeService;
        _mobileMoneyService = mobileMoneyService;
        _usageTrackingService = usageTrackingService;
        _configuration = configuration;
        _logger = logger;
    }

    #region Subscription Plans

    public async Task<IEnumerable<SubscriptionPlan>> GetPlansAsync(bool includePrivate = false)
    {
        var query = _dbContext.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.IsActive);

        if (!includePrivate)
        {
            query = query.Where(p => p.IsPublic);
        }

        return await query
            .OrderBy(p => p.SortOrder)
            .ToListAsync();
    }

    public async Task<SubscriptionPlan?> GetPlanByCodeAsync(string planCode)
    {
        return await _dbContext.SubscriptionPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == planCode && p.IsActive);
    }

    public async Task<SubscriptionPlan?> GetPlanByIdAsync(Guid planId)
    {
        return await _dbContext.SubscriptionPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId);
    }

    #endregion

    #region Subscriptions

    public async Task<SubscriptionResult> CreateSubscriptionAsync(
        Guid organizationId,
        string planCode,
        BillingCycle billingCycle,
        PaymentMethod paymentMethod,
        string? stripePaymentMethodId = null,
        string? mobileMoneyPhone = null)
    {
        try
        {
            var organization = await _dbContext.Organizations
                .FirstOrDefaultAsync(o => o.Id == organizationId);

            if (organization == null)
            {
                return new SubscriptionResult(false, null, "ORG_NOT_FOUND", "Organization not found");
            }

            var plan = await GetPlanByCodeAsync(planCode);
            if (plan == null)
            {
                return new SubscriptionResult(false, null, "PLAN_NOT_FOUND", "Subscription plan not found");
            }

            // Check if organization already has an active subscription
            var existingSubscription = await _dbContext.Subscriptions
                .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                         (s.Status == SubscriptionStatus.Active ||
                                          s.Status == SubscriptionStatus.Trialing));

            if (existingSubscription != null)
            {
                return new SubscriptionResult(false, null, "ALREADY_SUBSCRIBED",
                    "Organization already has an active subscription");
            }

            var now = DateTime.UtcNow;
            var subscription = new Subscription
            {
                OrganizationId = organizationId,
                PlanId = plan.Id,
                Status = SubscriptionStatus.Active,
                BillingCycle = billingCycle,
                PreferredPaymentMethod = paymentMethod,
                StartDate = now,
                CurrentPeriodStart = now,
                CurrentPeriodEnd = billingCycle == BillingCycle.Annual
                    ? now.AddYears(1)
                    : now.AddMonths(1),
                MobileMoneyPhone = mobileMoneyPhone,
                CreatedAt = now
            };

            // Handle Stripe subscription if card payment
            if (paymentMethod == PaymentMethod.Card && !string.IsNullOrEmpty(stripePaymentMethodId))
            {
                // Ensure organization has Stripe customer
                if (string.IsNullOrEmpty(organization.StripeCustomerId))
                {
                    organization.StripeCustomerId = await _stripeService.CreateCustomerAsync(organization);
                }

                var priceId = billingCycle == BillingCycle.Annual
                    ? plan.StripePriceIdAnnual
                    : plan.StripePriceIdMonthly;

                if (!string.IsNullOrEmpty(priceId))
                {
                    var stripeResult = await _stripeService.CreateSubscriptionAsync(
                        organization.StripeCustomerId,
                        priceId,
                        plan.TrialDays > 0 ? plan.TrialDays : null);

                    subscription.StripeSubscriptionId = stripeResult.SubscriptionId;
                    subscription.StripeCustomerId = stripeResult.CustomerId;
                    subscription.StripePaymentMethodId = stripePaymentMethodId;

                    if (stripeResult.TrialEnd.HasValue)
                    {
                        subscription.Status = SubscriptionStatus.Trialing;
                        subscription.TrialEnd = stripeResult.TrialEnd;
                    }
                }
            }

            _dbContext.Subscriptions.Add(subscription);

            // Update organization
            organization.SubscriptionId = subscription.Id;
            organization.Tier = plan.Tier;
            organization.Status = subscription.Status == SubscriptionStatus.Trialing
                ? TenantStatus.Trialing
                : TenantStatus.Active;

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Created subscription {SubscriptionId} for organization {OrganizationId} on plan {PlanCode}",
                subscription.Id, organizationId, planCode);

            return new SubscriptionResult(true, subscription, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create subscription for organization {OrganizationId}", organizationId);
            return new SubscriptionResult(false, null, "CREATE_FAILED", ex.Message);
        }
    }

    public async Task<Subscription?> GetSubscriptionAsync(Guid organizationId)
    {
        return await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      (s.Status == SubscriptionStatus.Active ||
                                       s.Status == SubscriptionStatus.Trialing ||
                                       s.Status == SubscriptionStatus.PastDue));
    }

    public async Task<SubscriptionResult> ChangePlanAsync(
        Guid subscriptionId,
        string newPlanCode,
        bool immediateChange = false)
    {
        try
        {
            var subscription = await _dbContext.Subscriptions
                .Include(s => s.Plan)
                .Include(s => s.Organization)
                .FirstOrDefaultAsync(s => s.Id == subscriptionId);

            if (subscription == null)
            {
                return new SubscriptionResult(false, null, "NOT_FOUND", "Subscription not found");
            }

            var newPlan = await GetPlanByCodeAsync(newPlanCode);
            if (newPlan == null)
            {
                return new SubscriptionResult(false, null, "PLAN_NOT_FOUND", "New plan not found");
            }

            // Update Stripe subscription if exists
            if (!string.IsNullOrEmpty(subscription.StripeSubscriptionId))
            {
                var priceId = subscription.BillingCycle == BillingCycle.Annual
                    ? newPlan.StripePriceIdAnnual
                    : newPlan.StripePriceIdMonthly;

                if (!string.IsNullOrEmpty(priceId))
                {
                    await _stripeService.UpdateSubscriptionAsync(
                        subscription.StripeSubscriptionId, priceId);
                }
            }

            subscription.PlanId = newPlan.Id;
            subscription.UpdatedAt = DateTime.UtcNow;

            // Update organization tier
            if (subscription.Organization != null)
            {
                subscription.Organization.Tier = newPlan.Tier;
            }

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Changed subscription {SubscriptionId} from plan {OldPlan} to {NewPlan}",
                subscriptionId, subscription.Plan.Code, newPlanCode);

            return new SubscriptionResult(true, subscription, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change plan for subscription {SubscriptionId}", subscriptionId);
            return new SubscriptionResult(false, null, "CHANGE_FAILED", ex.Message);
        }
    }

    public async Task<SubscriptionResult> CancelSubscriptionAsync(
        Guid subscriptionId,
        string? reason = null,
        bool immediately = false)
    {
        try
        {
            var subscription = await _dbContext.Subscriptions
                .Include(s => s.Organization)
                .FirstOrDefaultAsync(s => s.Id == subscriptionId);

            if (subscription == null)
            {
                return new SubscriptionResult(false, null, "NOT_FOUND", "Subscription not found");
            }

            // Cancel Stripe subscription if exists
            if (!string.IsNullOrEmpty(subscription.StripeSubscriptionId))
            {
                await _stripeService.CancelSubscriptionAsync(
                    subscription.StripeSubscriptionId, immediately);
            }

            subscription.CancellationReason = reason;
            subscription.CancelledAt = DateTime.UtcNow;
            subscription.CancelAtPeriodEnd = !immediately;

            if (immediately)
            {
                subscription.Status = SubscriptionStatus.Cancelled;
                subscription.EndDate = DateTime.UtcNow;

                // Update organization status
                if (subscription.Organization != null)
                {
                    subscription.Organization.Status = TenantStatus.Cancelled;
                    subscription.Organization.Tier = TenantTier.Free;
                    subscription.Organization.SubscriptionId = null;
                }
            }

            subscription.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Cancelled subscription {SubscriptionId}, immediately={Immediately}",
                subscriptionId, immediately);

            return new SubscriptionResult(true, subscription, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel subscription {SubscriptionId}", subscriptionId);
            return new SubscriptionResult(false, null, "CANCEL_FAILED", ex.Message);
        }
    }

    public async Task<SubscriptionResult> ReactivateSubscriptionAsync(Guid subscriptionId)
    {
        try
        {
            var subscription = await _dbContext.Subscriptions
                .Include(s => s.Organization)
                .Include(s => s.Plan)
                .FirstOrDefaultAsync(s => s.Id == subscriptionId);

            if (subscription == null)
            {
                return new SubscriptionResult(false, null, "NOT_FOUND", "Subscription not found");
            }

            if (subscription.Status != SubscriptionStatus.Cancelled)
            {
                return new SubscriptionResult(false, null, "NOT_CANCELLED",
                    "Subscription is not cancelled");
            }

            subscription.Status = SubscriptionStatus.Active;
            subscription.CancelledAt = null;
            subscription.CancelAtPeriodEnd = false;
            subscription.CancellationReason = null;
            subscription.UpdatedAt = DateTime.UtcNow;

            // Extend the subscription period
            var now = DateTime.UtcNow;
            subscription.CurrentPeriodStart = now;
            subscription.CurrentPeriodEnd = subscription.BillingCycle == BillingCycle.Annual
                ? now.AddYears(1)
                : now.AddMonths(1);

            // Update organization
            if (subscription.Organization != null)
            {
                subscription.Organization.Status = TenantStatus.Active;
                subscription.Organization.Tier = subscription.Plan.Tier;
                subscription.Organization.SubscriptionId = subscription.Id;
            }

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Reactivated subscription {SubscriptionId}", subscriptionId);

            return new SubscriptionResult(true, subscription, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reactivate subscription {SubscriptionId}", subscriptionId);
            return new SubscriptionResult(false, null, "REACTIVATE_FAILED", ex.Message);
        }
    }

    public async Task<SubscriptionResult> ProcessRenewalAsync(Guid subscriptionId)
    {
        try
        {
            var subscription = await _dbContext.Subscriptions
                .Include(s => s.Plan)
                .FirstOrDefaultAsync(s => s.Id == subscriptionId);

            if (subscription == null)
            {
                return new SubscriptionResult(false, null, "NOT_FOUND", "Subscription not found");
            }

            // Update billing period
            subscription.CurrentPeriodStart = subscription.CurrentPeriodEnd;
            subscription.CurrentPeriodEnd = subscription.BillingCycle == BillingCycle.Annual
                ? subscription.CurrentPeriodEnd.AddYears(1)
                : subscription.CurrentPeriodEnd.AddMonths(1);
            subscription.NextBillingDate = subscription.CurrentPeriodEnd;
            subscription.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Processed renewal for subscription {SubscriptionId}, new period ends {EndDate}",
                subscriptionId, subscription.CurrentPeriodEnd);

            return new SubscriptionResult(true, subscription, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process renewal for subscription {SubscriptionId}", subscriptionId);
            return new SubscriptionResult(false, null, "RENEWAL_FAILED", ex.Message);
        }
    }

    public async Task HandlePaymentFailureAsync(Guid subscriptionId, string? errorMessage = null)
    {
        var subscription = await _dbContext.Subscriptions
            .Include(s => s.Organization)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId);

        if (subscription == null) return;

        subscription.Status = SubscriptionStatus.PastDue;
        subscription.UpdatedAt = DateTime.UtcNow;

        if (subscription.Organization != null)
        {
            subscription.Organization.Status = TenantStatus.Suspended;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogWarning(
            "Payment failed for subscription {SubscriptionId}: {Error}",
            subscriptionId, errorMessage);
    }

    public async Task<SubscriptionWithPlan?> GetSubscriptionWithPlanAsync(Guid organizationId)
    {
        var subscription = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      (s.Status == SubscriptionStatus.Active ||
                                       s.Status == SubscriptionStatus.Trialing));

        if (subscription == null) return null;

        var limits = await GetEffectiveLimitsBySubscriptionIdAsync(subscription.Id);

        return new SubscriptionWithPlan(subscription, subscription.Plan, limits);
    }

    #endregion

    #region Invoices

    public async Task<Invoice> GenerateInvoiceAsync(Guid subscriptionId, DateTime periodStart, DateTime periodEnd)
    {
        var subscription = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .Include(s => s.Organization)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId);

        if (subscription == null)
            throw new InvalidOperationException("Subscription not found");

        var plan = subscription.Plan;
        var price = subscription.BillingCycle == BillingCycle.Annual
            ? (subscription.Organization?.PreferredCurrency == "UGX" ? plan.AnnualPriceUgx : plan.AnnualPriceUsd)
            : (subscription.Organization?.PreferredCurrency == "UGX" ? plan.MonthlyPriceUgx : plan.MonthlyPriceUsd);

        var invoice = new Invoice
        {
            OrganizationId = subscription.OrganizationId,
            SubscriptionId = subscriptionId,
            InvoiceNumber = GenerateInvoiceNumber(),
            Status = InvoiceStatus.Open,
            Currency = subscription.Organization?.PreferredCurrency ?? "USD",
            Subtotal = price,
            TaxAmount = 0,
            DiscountAmount = 0,
            Total = price,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            InvoiceDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(7),
            BillingEmail = subscription.Organization?.EffectiveBillingEmail,
            BillingName = subscription.Organization?.Name,
            LineItems = JsonSerializer.Serialize(new[]
            {
                new
                {
                    description = $"{plan.Name} - {subscription.BillingCycle}",
                    quantity = 1,
                    unitPrice = price,
                    total = price
                }
            }),
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Generated invoice {InvoiceNumber} for subscription {SubscriptionId}",
            invoice.InvoiceNumber, subscriptionId);

        return invoice;
    }

    public async Task<IEnumerable<Invoice>> GetInvoicesAsync(Guid organizationId, int page = 1, int pageSize = 20)
    {
        return await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.OrganizationId == organizationId)
            .OrderByDescending(i => i.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<Invoice?> GetInvoiceAsync(Guid invoiceId)
    {
        return await _dbContext.Invoices
            .AsNoTracking()
            .Include(i => i.Organization)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);
    }

    public async Task MarkInvoicePaidAsync(Guid invoiceId, Guid paymentId)
    {
        var invoice = await _dbContext.Invoices.FindAsync(invoiceId);
        if (invoice == null) return;

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = DateTime.UtcNow;
        invoice.AmountPaid = invoice.Total;
        invoice.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
    }

    public async Task VoidInvoiceAsync(Guid invoiceId, string reason)
    {
        var invoice = await _dbContext.Invoices.FindAsync(invoiceId);
        if (invoice == null) return;

        invoice.Status = InvoiceStatus.Void;
        invoice.Notes = reason;
        invoice.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
    }

    public async Task<Invoice> GenerateInvoiceAsync(Guid subscriptionId)
    {
        var subscription = await _dbContext.Subscriptions.FindAsync(subscriptionId);
        if (subscription == null)
            throw new InvalidOperationException("Subscription not found");

        return await GenerateInvoiceAsync(
            subscriptionId,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd);
    }

    public async Task<PaymentCollectionResult> CollectPaymentAsync(Guid invoiceId)
    {
        var invoice = await _dbContext.Invoices
            .Include(i => i.Subscription)
                .ThenInclude(s => s!.Organization)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice == null)
            return new PaymentCollectionResult(false, null, "INVOICE_NOT_FOUND", "Invoice not found");

        if (invoice.Status == InvoiceStatus.Paid)
            return new PaymentCollectionResult(false, null, "ALREADY_PAID", "Invoice is already paid");

        try
        {
            // Try to collect based on organization's preferred payment method
            var org = invoice.Subscription?.Organization;
            if (org == null)
                return new PaymentCollectionResult(false, null, "ORG_NOT_FOUND", "Organization not found");

            // If Stripe customer exists, try card payment
            if (!string.IsNullOrEmpty(org.StripeCustomerId))
            {
                var stripeResult = await _stripeService.ChargeCustomerAsync(
                    org.StripeCustomerId,
                    invoice.Total,
                    invoice.Currency,
                    $"Invoice {invoice.InvoiceNumber}");

                if (stripeResult.Success)
                {
                    var payment = await RecordPaymentAsync(
                        org.Id,
                        invoice.Total,
                        invoice.Currency,
                        PaymentMethod.Card,
                        invoice.SubscriptionId,
                        invoiceId,
                        stripeResult.ChargeId);

                    return new PaymentCollectionResult(true, payment, null, null);
                }

                return new PaymentCollectionResult(false, null, stripeResult.ErrorCode, stripeResult.ErrorMessage);
            }

            // If mobile money phone exists, try mobile money
            if (!string.IsNullOrEmpty(org.BillingPhone))
            {
                var mobileResult = await _mobileMoneyService.CollectPaymentAsync(
                    org.Id,
                    org.BillingPhone,
                    invoice.Total,
                    invoice.Currency,
                    $"Invoice {invoice.InvoiceNumber}",
                    invoice.InvoiceNumber);

                if (mobileResult.Success)
                {
                    var payment = await RecordPaymentAsync(
                        org.Id,
                        invoice.Total,
                        invoice.Currency,
                        mobileResult.Channel == "mtn" ? PaymentMethod.MtnMobileMoney : PaymentMethod.AirtelMoney,
                        invoice.SubscriptionId,
                        invoiceId,
                        mobileResult.TransactionId);

                    return new PaymentCollectionResult(true, payment, null, null);
                }

                return new PaymentCollectionResult(false, null, mobileResult.ErrorCode, mobileResult.ErrorMessage);
            }

            return new PaymentCollectionResult(false, null, "NO_PAYMENT_METHOD", "No valid payment method on file");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting payment for invoice {InvoiceId}", invoiceId);
            return new PaymentCollectionResult(false, null, "PAYMENT_ERROR", "An error occurred processing the payment");
        }
    }

    #endregion

    #region Payments

    public async Task<Payment> RecordPaymentAsync(
        Guid organizationId,
        decimal amount,
        string currency,
        PaymentMethod method,
        Guid? subscriptionId = null,
        Guid? invoiceId = null,
        string? externalReference = null)
    {
        var payment = new Payment
        {
            OrganizationId = organizationId,
            SubscriptionId = subscriptionId,
            InvoiceId = invoiceId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = method,
            Status = PaymentStatus.Succeeded,
            ReferenceId = Guid.NewGuid().ToString("N"),
            ExternalReferenceId = externalReference,
            InitiatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Payments.Add(payment);
        await _dbContext.SaveChangesAsync();

        // Mark invoice as paid if provided
        if (invoiceId.HasValue)
        {
            await MarkInvoicePaidAsync(invoiceId.Value, payment.Id);
        }

        _logger.LogInformation(
            "Recorded payment {PaymentId} of {Amount} {Currency} for organization {OrganizationId}",
            payment.Id, amount, currency, organizationId);

        return payment;
    }

    public async Task<IEnumerable<Payment>> GetPaymentsAsync(Guid organizationId, int page = 1, int pageSize = 20)
    {
        return await _dbContext.Payments
            .AsNoTracking()
            .Where(p => p.OrganizationId == organizationId)
            .OrderByDescending(p => p.InitiatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<Payment> ProcessRefundAsync(Guid paymentId, decimal? amount = null, string? reason = null)
    {
        var payment = await _dbContext.Payments.FindAsync(paymentId);
        if (payment == null)
            throw new InvalidOperationException("Payment not found");

        payment.Status = PaymentStatus.Refunded;
        payment.RefundedAt = DateTime.UtcNow;
        payment.RefundAmount = amount ?? payment.Amount;
        payment.RefundReason = reason;
        payment.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Processed refund for payment {PaymentId}, amount={Amount}",
            paymentId, payment.RefundAmount);

        return payment;
    }

    #endregion

    #region Limits

    public async Task<LimitCheckResult> CheckLimitAsync(Guid organizationId, string limitType)
    {
        return await _usageTrackingService.GetLimitStatusAsync(organizationId, limitType) switch
        {
            var status => new LimitCheckResult(
                !status.IsExceeded,
                status.LimitType,
                status.CurrentUsage,
                status.MaxAllowed,
                status.MaxAllowed - status.CurrentUsage,
                status.PercentageUsed)
        };
    }

    public async Task<OrganizationLimits> GetLimitsAsync(Guid organizationId)
    {
        var organization = await _dbContext.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == organizationId);

        if (organization == null)
            throw new InvalidOperationException("Organization not found");

        var limitTypes = new[] { "tokens", "api_calls", "storage", "users", "branches" };
        var limits = new Dictionary<string, LimitCheckResult>();

        foreach (var limitType in limitTypes)
        {
            limits[limitType] = await CheckLimitAsync(organizationId, limitType);
        }

        return new OrganizationLimits(organizationId, organization.Tier, limits);
    }

    public async Task<EffectiveLimits> GetEffectiveLimitsAsync(Guid organizationId)
    {
        // Resolves the organization's active subscription first, then delegates to the
        // subscription-keyed overload below. Found while wiring storage-quota enforcement:
        // both existing callers of that overload (SuperAdminController.GetTenant,
        // BillingJobs.CheckUsageLimitsAsync) were passing an organizationId where a
        // subscriptionId was expected — since a Subscription's own Id essentially never
        // equals its owning Organization's Id, that lookup always missed and silently fell
        // back to hardcoded free-tier limits (100 tokens, 0 API calls, 100MB storage)
        // regardless of the org's real plan. This name is now accurate; the old
        // subscription-keyed method is renamed below to make the distinction unambiguous.
        var subscription = await _dbContext.Subscriptions
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                       s.Status == SubscriptionStatus.Active);

        return await GetEffectiveLimitsBySubscriptionIdAsync(subscription?.Id ?? Guid.Empty);
    }

    public async Task<EffectiveLimits> GetEffectiveLimitsBySubscriptionIdAsync(Guid subscriptionId)
    {
        var subscription = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId);

        if (subscription == null)
        {
            // Return free tier limits
            return new EffectiveLimits(
                MaxBranches: 1,
                MaxUsersPerBranch: 2,
                MaxCountersPerBranch: 3,
                MaxTokensPerMonth: 100,
                MaxApiCallsPerMonth: 0,
                MaxStorageMb: 100,
                HasApiAccess: false,
                HasSmsNotifications: false,
                HasCustomBranding: false,
                HasAdvancedAnalytics: false,
                ShowAds: true);
        }

        var plan = subscription.Plan;
        var features = ParseFeatures(plan.Features);

        return new EffectiveLimits(
            MaxBranches: subscription.MaxBranchesOverride ?? plan.MaxBranches,
            MaxUsersPerBranch: subscription.MaxUsersOverride ?? plan.MaxUsersPerBranch,
            MaxCountersPerBranch: plan.MaxCountersPerBranch,
            MaxTokensPerMonth: subscription.MaxTokensOverride ?? plan.MaxTokensPerMonth,
            MaxApiCallsPerMonth: subscription.MaxApiCallsOverride ?? plan.MaxApiCallsPerMonth,
            MaxStorageMb: subscription.MaxStorageOverride ?? plan.MaxStorageMb,
            HasApiAccess: features.GetValueOrDefault("api_access", false),
            HasSmsNotifications: features.GetValueOrDefault("sms_notifications", false),
            HasCustomBranding: features.GetValueOrDefault("custom_branding", false),
            HasAdvancedAnalytics: features.GetValueOrDefault("advanced_analytics", false),
            ShowAds: plan.ShowAds);
    }

    #endregion

    #region Trial

    public async Task StartTrialAsync(Guid organizationId, string planCode, int trialDays)
    {
        var organization = await _dbContext.Organizations.FindAsync(organizationId);
        if (organization == null) return;

        var plan = await GetPlanByCodeAsync(planCode);
        if (plan == null) return;

        var now = DateTime.UtcNow;
        var subscription = new Subscription
        {
            OrganizationId = organizationId,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Trialing,
            BillingCycle = BillingCycle.Monthly,
            StartDate = now,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddDays(trialDays),
            TrialEnd = now.AddDays(trialDays),
            CreatedAt = now
        };

        _dbContext.Subscriptions.Add(subscription);

        organization.SubscriptionId = subscription.Id;
        organization.Status = TenantStatus.Trialing;
        organization.Tier = plan.Tier;
        organization.TrialEndsAt = subscription.TrialEnd;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Started {Days}-day trial for organization {OrganizationId} on plan {PlanCode}",
            trialDays, organizationId, planCode);
    }

    public async Task<bool> IsTrialExpiredAsync(Guid organizationId)
    {
        var subscription = await _dbContext.Subscriptions
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      s.Status == SubscriptionStatus.Trialing);

        if (subscription == null) return true;

        return subscription.TrialEnd.HasValue && subscription.TrialEnd.Value < DateTime.UtcNow;
    }

    public async Task<SubscriptionResult> ConvertTrialAsync(
        Guid organizationId,
        PaymentMethod paymentMethod,
        string? stripePaymentMethodId = null,
        string? mobileMoneyPhone = null)
    {
        var subscription = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .Include(s => s.Organization)
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      s.Status == SubscriptionStatus.Trialing);

        if (subscription == null)
        {
            return new SubscriptionResult(false, null, "NO_TRIAL", "No trial subscription found");
        }

        subscription.Status = SubscriptionStatus.Active;
        subscription.TrialEnd = null;
        subscription.PreferredPaymentMethod = paymentMethod;
        subscription.MobileMoneyPhone = mobileMoneyPhone;
        subscription.StripePaymentMethodId = stripePaymentMethodId;
        subscription.CurrentPeriodStart = DateTime.UtcNow;
        subscription.CurrentPeriodEnd = subscription.BillingCycle == BillingCycle.Annual
            ? DateTime.UtcNow.AddYears(1)
            : DateTime.UtcNow.AddMonths(1);
        subscription.UpdatedAt = DateTime.UtcNow;

        if (subscription.Organization != null)
        {
            subscription.Organization.Status = TenantStatus.Active;
            subscription.Organization.TrialEndsAt = null;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Converted trial to paid subscription for organization {OrganizationId}", organizationId);

        return new SubscriptionResult(true, subscription, null, null);
    }

    #endregion

    #region Private Helpers

    private static string GenerateInvoiceNumber()
    {
        return $"INV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString("N")[..8].ToUpper()}";
    }

    private static Dictionary<string, bool> ParseFeatures(string? featuresJson)
    {
        if (string.IsNullOrEmpty(featuresJson))
            return new Dictionary<string, bool>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(featuresJson)
                   ?? new Dictionary<string, bool>();
        }
        catch
        {
            return new Dictionary<string, bool>();
        }
    }

    #endregion
}
