using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
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
    private readonly IPaymentLedger _ledger;
    private readonly IUsageTrackingService _usageTrackingService;
    private readonly IFeatureFlagService _featureFlags;
    private readonly IModuleLimitResolver _limitResolver;
    private readonly IBillingAccountProvider _accountProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        QMgrDbContext dbContext,
        IStripeService stripeService,
        IPaymentLedger ledger,
        IUsageTrackingService usageTrackingService,
        IFeatureFlagService featureFlags,
        IModuleLimitResolver limitResolver,
        IBillingAccountProvider accountProvider,
        IConfiguration configuration,
        ILogger<BillingService> logger)
    {
        _dbContext = dbContext;
        _stripeService = stripeService;
        _ledger = ledger;
        _usageTrackingService = usageTrackingService;
        _featureFlags = featureFlags;
        _limitResolver = limitResolver;
        _accountProvider = accountProvider;
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

    #region Billing account

    /// <summary>
    /// Opens the organization's billing account: when it is invoiced and how it pays. What it is
    /// billed for comes from the modules it holds, not from a plan — this used to take a plan code
    /// and set a tier.
    /// </summary>
    public async Task<SubscriptionResult> CreateSubscriptionAsync(
        Guid organizationId,
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

            var existing = await _dbContext.Subscriptions
                .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                         (s.Status == SubscriptionStatus.Active ||
                                          s.Status == SubscriptionStatus.Trialing));

            if (existing != null)
            {
                return new SubscriptionResult(false, null, "ALREADY_SUBSCRIBED",
                    "Organization already has a billing account");
            }

            var now = DateTime.UtcNow;
            var subscription = new Subscription
            {
                OrganizationId = organizationId,
                Status = SubscriptionStatus.Active,
                BillingCycle = billingCycle,
                PreferredPaymentMethod = paymentMethod,
                StartDate = now,
                CurrentPeriodStart = now,
                CurrentPeriodEnd = billingCycle == BillingCycle.Annual
                    ? now.AddYears(1)
                    : now.AddMonths(1),
                MobileMoneyPhone = mobileMoneyPhone,
                StripePaymentMethodId = stripePaymentMethodId,
                CreatedAt = now
            };

            _dbContext.Subscriptions.Add(subscription);

            organization.SubscriptionId = subscription.Id;
            organization.Status = TenantStatus.Active;

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Opened billing account {SubscriptionId} for organization {OrganizationId}",
                subscription.Id, organizationId);

            return new SubscriptionResult(true, subscription, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open a billing account for organization {OrganizationId}", organizationId);
            return new SubscriptionResult(false, null, "CREATE_FAILED", ex.Message);
        }
    }

    public async Task<Subscription?> GetSubscriptionAsync(Guid organizationId)
    {
        return await _dbContext.Subscriptions
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      (s.Status == SubscriptionStatus.Active ||
                                       s.Status == SubscriptionStatus.Trialing ||
                                       s.Status == SubscriptionStatus.PastDue));
    }

    public async Task<Subscription?> GetSubscriptionByStripeIdAsync(string stripeSubscriptionId)
    {
        return await _dbContext.Subscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);
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

    public async Task HandlePaymentSuccessAsync(Guid subscriptionId, decimal amount, string currency, string? externalReference = null)
    {
        var subscription = await _dbContext.Subscriptions
            .Include(s => s.Organization)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId);

        if (subscription == null) return;

        // The open/unpaid invoice for this subscription's current period, if one exists —
        // RecordPaymentAsync marks it Paid when an invoiceId is passed. A webhook-driven payment
        // doesn't always have one pre-generated (e.g. a first-time card save covering an
        // out-of-band charge), so this is best-effort, not a hard requirement.
        var openInvoice = await _dbContext.Invoices
            .Where(i => i.SubscriptionId == subscriptionId && i.Status == InvoiceStatus.Open)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync();

        await RecordPaymentAsync(
            subscription.OrganizationId,
            amount,
            currency,
            PaymentMethod.Card,
            subscriptionId,
            openInvoice?.Id,
            externalReference);

        // Reactivate — this is what was previously entirely missing: neither the webhook path
        // nor CollectPaymentAsync ever un-suspended an organization or reset a PastDue
        // subscription back to Active on a successful payment.
        subscription.Status = SubscriptionStatus.Active;
        subscription.UpdatedAt = DateTime.UtcNow;

        if (subscription.Organization != null &&
            subscription.Organization.Status is TenantStatus.Suspended or TenantStatus.Trialing)
        {
            subscription.Organization.Status = TenantStatus.Active;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Payment succeeded for subscription {SubscriptionId}: {Amount} {Currency} — subscription and organization reactivated",
            subscriptionId, amount, currency);
    }

    public async Task<SubscriptionWithPlan?> GetSubscriptionWithPlanAsync(Guid organizationId)
    {
        var subscription = await _dbContext.Subscriptions
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      (s.Status == SubscriptionStatus.Active ||
                                       s.Status == SubscriptionStatus.Trialing));

        if (subscription == null) return null;

        var limits = await GetEffectiveLimitsBySubscriptionIdAsync(subscription.Id);

        return new SubscriptionWithPlan(subscription, limits);
    }

    public async Task<OrganizationTrialInfo?> GetOrganizationTrialInfoAsync(Guid organizationId)
    {
        var organization = await _dbContext.Organizations
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.Status, o.TrialEndsAt })
            .FirstOrDefaultAsync();

        if (organization == null) return null;

        return new OrganizationTrialInfo(organization.Status, organization.TrialEndsAt);
    }

    #endregion

    #region Invoices

    /// <summary>
    /// Raises one invoice for every module an organization holds whose billing period has run out.
    /// Returns null when nothing is due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what replaced tier billing on 2026-09-04. Until then the only thing that generated a
    /// recurring invoice was a tier subscription, and a module was charged once at purchase and
    /// never again — <see cref="OrganizationModule.CurrentPeriodEnd"/> was written at activation and
    /// read by nothing. It is now the due date that drives this method, so a monthly module is
    /// billed every month and an annual one appears on an invoice once a year, without either
    /// having to be repriced onto a shared cadence.
    /// </para>
    /// <para>
    /// Everything due on the same run lands on one document, one line per module, so a customer
    /// receives a single invoice rather than one per module. Each line is priced from that module's
    /// agreed price and falls back to the catalog's list price, exactly as
    /// <c>GetChargeableUgxPriceAsync</c> does for a purchase — a renewal must never quietly cost
    /// more than what the customer agreed to.
    /// </para>
    /// <para>
    /// A module still in its trial is not billed. Its period starts when it activates.
    /// </para>
    /// </remarks>
    public async Task<Invoice?> GenerateInvoiceForDueModulesAsync(Guid organizationId, DateTime asOf)
    {
        var organization = await _dbContext.Organizations
            .FirstOrDefaultAsync(o => o.Id == organizationId);

        if (organization == null)
            throw new InvalidOperationException("Organization not found");

        var due = await _dbContext.OrganizationModules
            .Include(om => om.Module)
            .Where(om => om.OrganizationId == organizationId &&
                         om.Status == OrganizationModuleStatus.Active &&
                         om.CurrentPeriodEnd != null &&
                         om.CurrentPeriodEnd <= asOf)
            .ToListAsync();

        if (due.Count == 0) return null;

        var currency = organization.PreferredCurrency ?? "UGX";
        var isUgx = string.Equals(currency, "UGX", StringComparison.OrdinalIgnoreCase);

        var account = await _accountProvider.GetOrOpenAsync(organizationId, asOf);

        var lines = new List<object>();
        decimal subtotal = 0;
        var periodStart = asOf;
        var periodEnd = asOf;

        foreach (var hold in due)
        {
            var module = hold.Module;
            if (module == null) continue;

            var annual = hold.BillingCycle == BillingCycle.Annual;
            var listPrice = annual
                ? (isUgx ? module.AnnualPriceUgx : module.AnnualPriceUsd)
                : (isUgx ? module.MonthlyPriceUgx : module.MonthlyPriceUsd);

            var unitPrice = isUgx
                ? hold.GetEffectivePriceUgx(listPrice)
                : hold.GetEffectivePriceUsd(listPrice);

            var lineStart = hold.CurrentPeriodEnd!.Value;
            var lineEnd = annual ? lineStart.AddYears(1) : lineStart.AddMonths(1);

            lines.Add(new
            {
                moduleCode = module.Code,
                description = $"{module.Name} — {hold.BillingCycle}",
                quantity = 1,
                unitPrice,
                total = unitPrice,
                periodStart = lineStart,
                periodEnd = lineEnd
            });

            subtotal += unitPrice;
            if (lineStart < periodStart) periodStart = lineStart;
            if (lineEnd > periodEnd) periodEnd = lineEnd;

            // Roll this module's own period forward. Done here rather than on payment so a failed
            // collection dunning the existing invoice cannot also raise a second one next run.
            hold.CurrentPeriodEnd = lineEnd;
            hold.UpdatedAt = asOf;
        }

        var invoice = new Invoice
        {
            OrganizationId = organizationId,
            SubscriptionId = account.Id,
            InvoiceNumber = GenerateInvoiceNumber(),
            Status = InvoiceStatus.Open,
            Currency = currency,
            Subtotal = subtotal,
            TaxAmount = 0,
            DiscountAmount = 0,
            Total = subtotal,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            InvoiceDate = asOf,
            DueDate = asOf.AddDays(7),
            BillingEmail = organization.EffectiveBillingEmail,
            BillingName = organization.Name,
            LineItems = JsonSerializer.Serialize(lines),
            CreatedAt = asOf
        };

        _dbContext.Invoices.Add(invoice);

        account.CurrentPeriodStart = periodStart;
        account.CurrentPeriodEnd = periodEnd;
        account.NextBillingDate = periodEnd;
        account.UpdatedAt = asOf;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Generated invoice {InvoiceNumber} for organization {OrganizationId}: {LineCount} module(s), {Total} {Currency}",
            invoice.InvoiceNumber, organizationId, due.Count, subtotal, currency);

        return invoice;
    }

    /// <summary>

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

            // A card on file with Stripe — only when Stripe is actually configured. It is off unless
            // an administrator configures it (2026-09-19); a Stripe customer id left over from a test
            // would otherwise send every renewal into a charge that can only fail, and on into dunning.
            if (!string.IsNullOrEmpty(org.StripeCustomerId) && await _stripeService.IsConfiguredAsync())
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

            // Mobile Money to the renewal number on file, through the sacc.ug gateway and the ledger
            // (2026-09-19). This used to record the invoice PAID the moment the prompt was sent —
            // before any money moved — and recorded every MTN payment as Airtel. Now a prompt is a
            // pending ledger row; only the gateway's confirmation marks the invoice paid.
            if (!string.IsNullOrEmpty(org.BillingPhone))
            {
                // A prompt already out for this invoice is settled first, never sent again.
                if (await _ledger.OpenPaymentForInvoiceAsync(invoice.Id) is { } openId)
                {
                    var state = await _ledger.RefreshAsync(openId);
                    if (state == PaymentStates.Succeeded)
                        return new PaymentCollectionResult(true, await _dbContext.Payments.FindAsync(openId), null, null);
                    if (state is PaymentStates.Pending or PaymentStates.Review)
                        return new PaymentCollectionResult(false, null, AwaitingConfirmation,
                            "A mobile money prompt is waiting for the customer to approve it.");
                    // Failed or abandoned: a fresh attempt goes ahead below.
                }

                try
                {
                    var start = await _ledger.StartInvoicePaymentAsync(org.Id, invoice.Id, org.BillingPhone, null);
                    if (start.State == PaymentStates.Succeeded && start.ReferenceId is { } paid)
                        return new PaymentCollectionResult(true, await _dbContext.Payments.FindAsync(paid), null, null);
                    if (!start.IsFinal)
                        return new PaymentCollectionResult(false, null, AwaitingConfirmation, start.Message);
                    return new PaymentCollectionResult(false, null, "MOBILE_MONEY_FAILED", start.Message);
                }
                catch (PaymentRequestException ex)
                {
                    return new PaymentCollectionResult(false, null, ex.Code, ex.Message);
                }
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

    /// <summary>A mobile money prompt has been sent and not yet answered. NOT a failure: the renewal
    /// job must neither mark the subscription past due nor tell the customer their payment failed
    /// while their phone is still asking them to approve it.</summary>
    public const string AwaitingConfirmation = "AWAITING_CONFIRMATION";

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

        return new OrganizationLimits(organizationId, limits);
    }

    public async Task<EffectiveLimits> GetEffectiveLimitsAsync(Guid organizationId)
    {
        return await _limitResolver.ResolveAsync(organizationId);
    }

    public async Task<EffectiveLimits> GetEffectiveLimitsBySubscriptionIdAsync(Guid subscriptionId)
    {
        var organizationId = await _dbContext.Subscriptions
            .AsNoTracking()
            .Where(s => s.Id == subscriptionId)
            .Select(s => (Guid?)s.OrganizationId)
            .FirstOrDefaultAsync();

        return organizationId.HasValue
            ? await _limitResolver.ResolveAsync(organizationId.Value)
            : await _limitResolver.ResolveAsync(Guid.Empty);
    }


    #endregion

    #region Trial

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


    /// <summary>
    /// Monthly recurring revenue across the platform, in USD.
    /// </summary>
    /// <remarks>
    /// Summed from the modules organizations actually hold, each at the price that organization
    /// agreed to and falling back to the catalog's list price. Until 2026-09-04 this was computed
    /// from tier subscriptions, which meant a customer who only ever bought modules — by then most
    /// of them — counted as zero revenue. An annual holding contributes a twelfth of its price.
    /// A module granted by a platform administrator is excluded: nothing is charged for it.
    /// </remarks>
    public async Task<decimal> GetPlatformMonthlyRecurringRevenueUsdAsync()
    {
        var held = await _dbContext.OrganizationModules
            .AsNoTracking()
            .Where(om => om.Status == OrganizationModuleStatus.Active && !om.GrantedByPlatformAdmin)
            .Select(om => new
            {
                om.BillingCycle,
                om.AgreedPriceUsd,
                ListMonthly = om.Module!.MonthlyPriceUsd,
                ListAnnual = om.Module.AnnualPriceUsd
            })
            .ToListAsync();

        decimal mrr = 0;
        foreach (var h in held)
        {
            var annual = h.BillingCycle == BillingCycle.Annual;
            var listPrice = annual ? h.ListAnnual : h.ListMonthly;
            var price = h.AgreedPriceUsd ?? listPrice;
            mrr += annual ? price / 12 : price;
        }

        return mrr;
    }

    #region Private Helpers

    /// <summary>The one invoice-number format; the payment ledger uses it too.</summary>
    internal static string GenerateInvoiceNumber()
    {
        return $"INV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString("N")[..8].ToUpper()}";
    }

    /// <summary>
    /// The list price of one billing period of <paramref name="plan"/> in
    /// <paramref name="currency"/> — the price an agreed price is captured from, and the fallback
    /// when a row has none.
    /// </summary>
    private static decimal ListPriceFor(SubscriptionPlan plan, BillingCycle cycle, string? currency)
    {
        var isUgx = string.Equals(currency, "UGX", StringComparison.OrdinalIgnoreCase);
        return cycle == BillingCycle.Annual
            ? (isUgx ? plan.AnnualPriceUgx : plan.AnnualPriceUsd)
            : (isUgx ? plan.MonthlyPriceUgx : plan.MonthlyPriceUsd);
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
