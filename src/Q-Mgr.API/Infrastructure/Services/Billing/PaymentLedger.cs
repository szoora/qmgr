using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Domain.Payments;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Email;
using QMgr.API.Application.Services;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>See <see cref="IPaymentLedger"/> for the rules this class exists to keep.</summary>
public sealed class PaymentLedger : IPaymentLedger
{
    /// <summary>A pending collection the gateway has never heard of is dropped after this long: the
    /// request did not reach it. Long enough for a slow gateway to have written its own row.</summary>
    private static readonly TimeSpan UnknownToGatewayAfter = TimeSpan.FromMinutes(30);

    /// <summary>The gateway's own reconciliation deadline (HangfireSettings:Reconciliation:DeadlineHours).
    /// A payment still open after this is abandoned here too.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromHours(72);

    /// <summary>A purchase started less than this long ago for the same module is the same purchase:
    /// a double click or a second tab returns it instead of prompting the phone twice.</summary>
    private static readonly TimeSpan InFlightWindow = TimeSpan.FromMinutes(10);

    private const string KindModulePurchase = "module-purchase";
    private const string KindInvoice = "invoice";

    private readonly QMgrDbContext _db;
    private readonly ISaccGateway _gateway;
    private readonly IModuleAccessService _modules;
    private readonly IBillingAccountProvider _accounts;
    private readonly INotificationService _notifications;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PaymentLedger> _logger;

    public PaymentLedger(
        QMgrDbContext db,
        ISaccGateway gateway,
        IModuleAccessService modules,
        IBillingAccountProvider accounts,
        INotificationService notifications,
        IHostEnvironment environment,
        ILogger<PaymentLedger> logger)
    {
        _db = db;
        _gateway = gateway;
        _modules = modules;
        _accounts = accounts;
        _notifications = notifications;
        _environment = environment;
        _logger = logger;
    }

    // ================================================================== what a payment is for

    private sealed record PaymentPurpose(string Kind, string? ModuleCode, string? Cycle, string? ModuleName,
        PaymentResolution? Resolution = null);

    /// <summary>A platform administrator's decision on a payment held for review. Kept inside
    /// Metadata rather than in a column of its own: it happens to a handful of payments, ever.</summary>
    private sealed record PaymentResolution(string Outcome, string By, DateTime At, string Note);

    private static string? ResolutionText(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<PaymentPurpose>(metadata)?.Resolution;
            return r == null ? null : string.Create(CultureInfo.InvariantCulture,
                $"{(r.Outcome == ReviewOutcomes.Received ? "Marked received" : "Marked not received")} by {r.By} on {r.At:dd MMM yyyy}: {r.Note}");
        }
        catch (JsonException) { return null; }
    }

    private static string Describe(PaymentPurpose purpose) => JsonSerializer.Serialize(purpose);

    private static PaymentPurpose? PurposeOf(Payment payment)
    {
        if (string.IsNullOrWhiteSpace(payment.Metadata)) return null;
        try { return JsonSerializer.Deserialize<PaymentPurpose>(payment.Metadata); }
        catch (JsonException) { return null; }
    }

    // ================================================================== state mapping (the one map)

    private static PaymentStatus ToLedger(string gatewayState) => gatewayState switch
    {
        PaymentStates.Succeeded => PaymentStatus.Succeeded,
        PaymentStates.Failed => PaymentStatus.Failed,
        PaymentStates.Abandoned => PaymentStatus.Abandoned,
        PaymentStates.Review => PaymentStatus.Review,
        _ => PaymentStatus.Pending
    };

    private static string ToWire(PaymentStatus status) => status switch
    {
        PaymentStatus.Succeeded => PaymentStates.Succeeded,
        PaymentStatus.Failed or PaymentStatus.Cancelled or PaymentStatus.Refunded => PaymentStates.Failed,
        PaymentStatus.Abandoned => PaymentStates.Abandoned,
        PaymentStatus.Review => PaymentStates.Review,
        _ => PaymentStates.Pending
    };

    private static bool IsOpen(PaymentStatus status) => status is PaymentStatus.Pending or PaymentStatus.Processing or PaymentStatus.Review;

    private static string MessageFor(Payment p) => p.Status switch
    {
        PaymentStatus.Succeeded => "Payment received.",
        PaymentStatus.Review => "The payment is being checked by the payment provider. You will be told when it is confirmed.",
        PaymentStatus.Abandoned => "The payment was not approved in time. Nothing was charged.",
        PaymentStatus.Failed => string.IsNullOrWhiteSpace(p.ErrorMessage) ? "The payment did not go through. Nothing was charged." : p.ErrorMessage!,
        _ => p.PaymentMethod == PaymentMethod.Card
            ? "Waiting for the card payment to complete."
            : "Waiting for you to approve the payment on your phone."
    };

    private static PaymentMethod MobileMoneyMethodFor(string? phone, string? channel)
    {
        var op = channel ?? UgandaPhone.Operator(phone);
        return op != null && op.Contains("mtn", StringComparison.OrdinalIgnoreCase)
            ? PaymentMethod.MtnMobileMoney
            : PaymentMethod.AirtelMoney;
    }

    // ================================================================== start: module purchase

    public async Task<PaymentStartDto> StartModulePurchaseAsync(
        Guid organizationId, string moduleCode, BillingCycle cycle, ModulePurchaseRequest request,
        string? ipAddress, CancellationToken cancellationToken = default)
    {
        var method = (request.Method ?? string.Empty).Trim().ToLowerInvariant();
        if (method != PaymentMethodCodes.MobileMoney && method != PaymentMethodCodes.Card)
            throw new PaymentRequestException("INVALID_METHOD", "Choose Mobile Money or card.");

        var module = await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == moduleCode, cancellationToken)
            ?? throw new PaymentRequestException("UNKNOWN_MODULE", "That module is not in the catalog.", 404);

        var organization = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken)
            ?? throw new PaymentRequestException("UNKNOWN_ORGANIZATION", "Your organization could not be found.", 404);

        var config = await _gateway.GetConfigAsync();
        var devSimulation = _environment.IsDevelopment() && !config.MobileMoneyAvailable;

        string? phone = null;
        string? email = null;
        string? name = null;
        if (method == PaymentMethodCodes.MobileMoney)
        {
            if (!devSimulation && !config.MobileMoneyAvailable)
                throw new PaymentRequestException("UNAVAILABLE", SaccGateway.UnavailableMessage);
            var problem = UgandaPhone.Problem(request.PhoneNumber);
            if (problem != null) throw new PaymentRequestException("INVALID_PHONE", problem);
            phone = UgandaPhone.Normalize(request.PhoneNumber);
        }
        else
        {
            if (!config.CardsAvailable)
                throw new PaymentRequestException("UNAVAILABLE", "Card payments are not available right now. Please use Mobile Money.");
            email = (request.PayerEmail ?? string.Empty).Trim();
            if (!IsEmail(email)) throw new PaymentRequestException("INVALID_EMAIL", "Enter the email address the card receipt should go to.");
            name = string.IsNullOrWhiteSpace(request.PayerName) ? organization.Name : request.PayerName!.Trim();
            if (name.Length > 100) throw new PaymentRequestException("INVALID_NAME", "The payer's name is too long (100 characters at most).");
            if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
            {
                var problem = UgandaPhone.Problem(request.PhoneNumber);
                if (problem != null) throw new PaymentRequestException("INVALID_PHONE", problem);
                phone = UgandaPhone.Normalize(request.PhoneNumber);
            }
        }

        var amount = await _modules.GetChargeableUgxPriceAsync(organizationId, moduleCode, cycle);
        if (amount <= 0)
            throw new PaymentRequestException("NO_PRICE", "This module has no price set yet. Contact support.");
        if (amount > 50_000_000m)
            throw new PaymentRequestException("AMOUNT_TOO_LARGE", "This amount is above what a single payment can collect. Contact support.");

        // A second click, or a second tab, while a purchase of the same module is waiting on the payer
        // is the SAME purchase. Returning it is what stops two prompts arriving on one phone.
        //
        // Matched in memory, over this organization's few pending rows in the window. Metadata is a
        // jsonb column: a string Contains translates to LIKE, which Postgres refuses on jsonb (every
        // purchase 500ed, found by e2e 16.8), and jsonb rewrites the stored text anyway, so a text
        // match could never have found the row.
        var since = DateTime.UtcNow - InFlightWindow;
        var pendingInWindow = await _db.Payments
            .Where(p => p.OrganizationId == organizationId && p.Status == PaymentStatus.Pending && p.InitiatedAt >= since)
            .OrderByDescending(p => p.InitiatedAt)
            .ToListAsync(cancellationToken);
        var inFlight = pendingInWindow.FirstOrDefault(p =>
            PurposeOf(p) is { Kind: KindModulePurchase } purpose && purpose.ModuleCode == moduleCode);
        if (inFlight != null)
            return new PaymentStartDto(inFlight.Id, PaymentStates.Pending, false,
                "A payment for this module is already waiting. Approve it on your phone, or wait for it to expire.");

        var now = DateTime.UtcNow;
        var account = await _accounts.GetOrOpenAsync(organizationId, now);
        var periodEnd = cycle == BillingCycle.Annual ? now.AddYears(1) : now.AddMonths(1);

        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            OrganizationId = organizationId,
            SubscriptionId = account.Id,
            InvoiceNumber = BillingService.GenerateInvoiceNumber(),
            Status = InvoiceStatus.Open,
            Currency = "UGX",
            Subtotal = amount,
            Total = amount,
            PeriodStart = now,
            PeriodEnd = periodEnd,
            InvoiceDate = now,
            DueDate = now,
            BillingEmail = organization.EffectiveBillingEmail,
            BillingName = organization.Name,
            LineItems = JsonSerializer.Serialize(new[]
            {
                new
                {
                    moduleCode,
                    description = $"{module.Name} — {cycle}",
                    quantity = 1,
                    unitPrice = amount,
                    total = amount,
                    periodStart = now,
                    periodEnd
                }
            }),
            CreatedAt = now
        };
        _db.Invoices.Add(invoice);

        var payment = NewPayment(organizationId, account.Id, invoiceId, amount,
            method == PaymentMethodCodes.Card ? PaymentMethod.Card : MobileMoneyMethodFor(phone, null),
            phone, $"{module.Name} ({cycle})", ipAddress,
            new PaymentPurpose(KindModulePurchase, moduleCode, cycle.ToString(), module.Name));
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(cancellationToken);

        if (devSimulation && method == PaymentMethodCodes.MobileMoney)
        {
            // Development only, and only with no gateway configured: settle through the real path so
            // the purchase flow can be demonstrated end to end. Never outside Development.
            _logger.LogInformation("[DEV SIMULATION] Settling {Reference} without a gateway", payment.Id);
            await SettleAsync(payment.Id, new GatewayStatusResult(true, PaymentStates.Succeeded, true, amount, "simulation", "SIMULATION", null, "Simulated"), cancellationToken);
            return new PaymentStartDto(payment.Id, PaymentStates.Succeeded, true,
                "No payment gateway is configured in this environment — the module was activated for testing.", Simulated: true);
        }

        var returnUrl = method == PaymentMethodCodes.Card
            ? BillingLinks.Absolute(await PublicBaseUrlAsync(), BillingLinks.CheckoutReturn(true)) + "&ref=" + payment.Id
            : null;

        return await SendAsync(payment, new GatewayCollectRequest(
            payment.Id, amount, $"Q-Mgr {module.Name} module ({cycle})", method, phone, email, name, returnUrl,
            invoice.InvoiceNumber, $"Q-Mgr: {module.Name}"), cancellationToken);
    }

    // ================================================================== start: pay an invoice

    public async Task<PaymentStartDto> StartInvoicePaymentAsync(
        Guid organizationId, Guid invoiceId, string phoneNumber, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.OrganizationId == organizationId, cancellationToken)
            ?? throw new PaymentRequestException("NOT_FOUND", "That invoice could not be found.", 404);

        if (invoice.Status == InvoiceStatus.Paid)
            throw new PaymentRequestException("ALREADY_PAID", "This invoice is already paid.", 409);
        if (invoice.Status is InvoiceStatus.Void or InvoiceStatus.Draft)
            throw new PaymentRequestException("NOT_PAYABLE", "This invoice is not open for payment.", 409);
        if (!string.Equals(invoice.Currency, "UGX", StringComparison.OrdinalIgnoreCase))
            throw new PaymentRequestException("CURRENCY", $"This invoice is in {invoice.Currency}; Mobile Money collects UGX only. Contact support to settle it.");
        if (invoice.Total <= 0)
            throw new PaymentRequestException("NOTHING_DUE", "Nothing is due on this invoice.", 409);

        if (!await _gateway.IsMobileMoneyAvailableAsync())
            throw new PaymentRequestException("UNAVAILABLE", SaccGateway.UnavailableMessage);

        var problem = UgandaPhone.Problem(phoneNumber);
        if (problem != null) throw new PaymentRequestException("INVALID_PHONE", problem);
        var phone = UgandaPhone.Normalize(phoneNumber);

        // One open collection per invoice. A second request while the first is still on the payer's
        // phone returns the first rather than sending another prompt.
        var open = await OpenPaymentForInvoiceAsync(invoice.Id, cancellationToken);
        if (open is { } openId)
            return new PaymentStartDto(openId, PaymentStates.Pending, false,
                "A payment for this invoice is already waiting. Approve it on your phone.");

        var due = invoice.Total - invoice.AmountPaid;
        var payment = NewPayment(organizationId, invoice.SubscriptionId, invoice.Id, due,
            MobileMoneyMethodFor(phone, null), phone, $"Invoice {invoice.InvoiceNumber}", ipAddress,
            new PaymentPurpose(KindInvoice, null, null, null));
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(cancellationToken);

        return await SendAsync(payment, new GatewayCollectRequest(
            payment.Id, due, $"Q-Mgr invoice {invoice.InvoiceNumber}", PaymentMethodCodes.MobileMoney, phone,
            null, null, null, invoice.InvoiceNumber, $"Q-Mgr invoice {invoice.InvoiceNumber}"), cancellationToken);
    }

    private static Payment NewPayment(Guid organizationId, Guid? subscriptionId, Guid invoiceId, decimal amount,
        PaymentMethod method, string? phone, string description, string? ipAddress, PaymentPurpose purpose)
    {
        var id = Guid.NewGuid();
        return new Payment
        {
            Id = id,
            OrganizationId = organizationId,
            SubscriptionId = subscriptionId,
            InvoiceId = invoiceId,
            Amount = amount,
            Currency = "UGX",
            PaymentMethod = method,
            Status = PaymentStatus.Pending,
            // The payment id IS the gateway reference and idempotency key — one id end to end.
            ReferenceId = id.ToString(),
            ExternalReferenceId = id.ToString(),
            MobileMoneyPhone = phone,
            MobileMoneyChannel = UgandaPhone.Operator(phone),
            Description = description,
            Metadata = Describe(purpose),
            IpAddress = ipAddress,
            InitiatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>Ask the gateway, and record what it said. A refusal before the gateway accepted the
    /// payment closes the row and, for a purchase, voids its invoice — nothing is owed for a module
    /// that was never bought.</summary>
    private async Task<PaymentStartDto> SendAsync(Payment payment, GatewayCollectRequest request, CancellationToken cancellationToken)
    {
        var result = await _gateway.CollectAsync(request, cancellationToken);

        if (!result.Accepted)
        {
            _logger.LogWarning("Payment {Reference} refused by the gateway: {Code} {Message}", payment.Id, result.ErrorCode, result.AdminMessage);
            await SettleAsync(payment.Id, new GatewayStatusResult(true, PaymentStates.Failed, true, null, null, null,
                result.ErrorCode, result.CustomerMessage), cancellationToken);
            return new PaymentStartDto(payment.Id, PaymentStates.Failed, true, result.CustomerMessage);
        }

        if (!string.IsNullOrWhiteSpace(result.Channel)) payment.MobileMoneyChannel = result.Channel;
        await _db.SaveChangesAsync(cancellationToken);

        // A verdict reached inline (a card declined at once, a gateway refusal) is settled now.
        if (result.IsFinal || result.State == PaymentStates.Review)
        {
            var settled = await SettleAsync(payment.Id, new GatewayStatusResult(true, result.State, result.IsFinal, null, null,
                result.Channel, result.ErrorCode, result.CustomerMessage), cancellationToken);
            var row = await _db.Payments.AsNoTracking().FirstAsync(p => p.Id == payment.Id, cancellationToken);
            return new PaymentStartDto(payment.Id, settled ?? result.State, PaymentStates.IsFinal(settled), MessageFor(row));
        }

        return new PaymentStartDto(payment.Id, PaymentStates.Pending, false, result.CustomerMessage, result.RedirectUrl);
    }

    // ================================================================== status

    public async Task<PaymentStatusDto?> GetStatusAsync(Guid organizationId, Guid referenceId, bool refresh, CancellationToken cancellationToken = default)
    {
        var payment = await _db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == referenceId && p.OrganizationId == organizationId, cancellationToken);
        if (payment == null) return null;

        if (refresh && IsOpen(payment.Status))
        {
            await RefreshAsync(referenceId, cancellationToken);
            payment = await _db.Payments.AsNoTracking().FirstAsync(p => p.Id == referenceId, cancellationToken);
        }

        var state = ToWire(payment.Status);
        return new PaymentStatusDto(payment.Id, state, PaymentStates.IsFinal(state), MessageFor(payment),
            payment.Amount, payment.Currency, payment.Description, payment.InitiatedAt, payment.CompletedAt);
    }

    public async Task<string?> RefreshAsync(Guid referenceId, CancellationToken cancellationToken = default)
    {
        var payment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == referenceId, cancellationToken);
        if (payment == null) return null;
        if (!IsOpen(payment.Status)) return ToWire(payment.Status);

        var status = await _gateway.GetStatusAsync(referenceId, cancellationToken);
        var age = DateTime.UtcNow - payment.InitiatedAt;

        if (!status.Known)
        {
            // The gateway has no such payment: the request never reached it. After a grace period
            // that is final — nothing was collected, and a new attempt is allowed.
            return age > UnknownToGatewayAfter
                ? await SettleAsync(referenceId, new GatewayStatusResult(true, PaymentStates.Abandoned, true, null, null, null,
                    "NOT_AT_GATEWAY", "The payment request never reached the payment provider. Nothing was charged."), cancellationToken)
                : ToWire(payment.Status);
        }

        if (!status.IsFinal && status.State != PaymentStates.Review && age > Deadline)
        {
            return await SettleAsync(referenceId, status with
            {
                State = PaymentStates.Abandoned, IsFinal = true, ErrorCode = "DEADLINE",
                Message = "The payment was not completed in time. Nothing was charged."
            }, cancellationToken);
        }

        return await SettleAsync(referenceId, status, cancellationToken);
    }

    // ================================================================== settle (the one path)

    public async Task<string?> SettleAsync(Guid referenceId, GatewayStatusResult status, CancellationToken cancellationToken = default)
    {
        string? resultState = null;
        Payment? settledPayment = null;
        PaymentStatus before = PaymentStatus.Pending;

        // A caller that already holds a transaction (the renewal job, inside its own unit of work) is
        // joined, never given a second one; otherwise the work runs inside the execution strategy, as
        // every user transaction here must (CLAUDE.md, "A user transaction must go through the
        // execution strategy").
        async Task Body(bool ownTransaction)
        {
            // One settlement at a time per payment: the webhook, a status read and the job can all
            // arrive together, and only one of them may apply the money.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({"payment:" + referenceId}))", cancellationToken);

            // Re-read under the lock. A row this context already tracks is RELOADED, not detached:
            // clearing the tracker would silently drop whatever unsaved work the caller holds.
            var payment = _db.Payments.Local.FirstOrDefault(p => p.Id == referenceId);
            if (payment != null) await _db.Entry(payment).ReloadAsync(cancellationToken);
            else payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == referenceId, cancellationToken);
            if (payment == null) { resultState = null; return; }

            before = payment.Status;
            var target = ToLedger(status.State);

            // Final is final. A late "pending" webhook cannot re-open a settled payment, and nothing
            // downgrades a success.
            if (!IsOpen(payment.Status) || target == PaymentStatus.Pending || target == payment.Status)
            {
                resultState = ToWire(payment.Status);
                return;
            }

            var now = DateTime.UtcNow;
            if (target == PaymentStatus.Succeeded && status.ConfirmedAmount is { } confirmed && confirmed < payment.Amount)
            {
                // Less money arrived than was asked for. Not provisioned: held for a person.
                target = PaymentStatus.Review;
                payment.ErrorCode = "UNDERPAID";
                payment.ErrorMessage = $"The provider confirmed {confirmed.ToString("N0", CultureInfo.InvariantCulture)} of {payment.Amount.ToString("N0", CultureInfo.InvariantCulture)} {payment.Currency}.";
            }

            payment.Status = target;
            payment.UpdatedAt = now;
            if (!string.IsNullOrWhiteSpace(status.Channel)) payment.MobileMoneyChannel = status.Channel;
            if (payment.PaymentMethod != PaymentMethod.Card && (status.Channel ?? status.Provider) is { } ch)
                payment.PaymentMethod = MobileMoneyMethodFor(payment.MobileMoneyPhone, ch);

            var purpose = PurposeOf(payment);
            Invoice? invoice = null;
            if (payment.InvoiceId is { } invoiceId)
            {
                invoice = _db.Invoices.Local.FirstOrDefault(i => i.Id == invoiceId);
                if (invoice != null) await _db.Entry(invoice).ReloadAsync(cancellationToken);
                else invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
            }

            switch (target)
            {
                case PaymentStatus.Succeeded:
                    payment.CompletedAt = now;
                    payment.ErrorCode = null;
                    payment.ErrorMessage = null;
                    if (invoice != null)
                    {
                        invoice.AmountPaid = Math.Min(invoice.Total, invoice.AmountPaid + payment.Amount);
                        if (invoice.AmountPaid >= invoice.Total)
                        {
                            invoice.Status = InvoiceStatus.Paid;
                            invoice.PaidAt = now;
                        }
                        invoice.UpdatedAt = now;
                    }
                    await ReinstateAsync(payment, now, cancellationToken);
                    break;

                case PaymentStatus.Failed:
                case PaymentStatus.Abandoned:
                    payment.FailedAt = now;
                    payment.ErrorCode = status.ErrorCode ?? payment.ErrorCode;
                    payment.ErrorMessage = string.IsNullOrWhiteSpace(status.Message) ? payment.ErrorMessage : status.Message;
                    // A purchase that never completed owes nothing: its invoice is voided. A renewal
                    // invoice stays open — the money is still due, and dunning decides what follows.
                    if (invoice != null && purpose?.Kind == KindModulePurchase && invoice.Status == InvoiceStatus.Open)
                    {
                        invoice.Status = InvoiceStatus.Void;
                        invoice.Notes = "Payment not completed.";
                        invoice.UpdatedAt = now;
                    }
                    break;

                case PaymentStatus.Review:
                    payment.ErrorMessage ??= "Held for review by the payment provider.";
                    break;
            }

            await _db.SaveChangesAsync(cancellationToken);
            resultState = ToWire(payment.Status);
            settledPayment = payment;
        }

        if (_db.Database.CurrentTransaction != null)
        {
            await Body(ownTransaction: false);
        }
        else
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                resultState = null;
                settledPayment = null;
                await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
                await Body(ownTransaction: true);
                await tx.CommitAsync(cancellationToken);
            });
        }

        if (settledPayment == null || !IsOpen(before)) return resultState;

        // After the commit, and never able to fail the settlement: provisioning is idempotent and a
        // notice that did not send is a degraded success (CLAUDE.md, the ProtectSystem rule).
        await AfterSettlementAsync(settledPayment, cancellationToken);
        return resultState;
    }

    /// <summary>A confirmed payment brings a past-due account back. The same effect as
    /// <c>BillingService.HandlePaymentSuccessAsync</c> for a card.</summary>
    private async Task ReinstateAsync(Payment payment, DateTime now, CancellationToken cancellationToken)
    {
        if (payment.SubscriptionId is { } accountId)
        {
            var account = await _db.Subscriptions.FirstOrDefaultAsync(s => s.Id == accountId, cancellationToken);
            if (account != null && account.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Suspended)
            {
                account.Status = SubscriptionStatus.Active;
                account.UpdatedAt = now;
            }
        }

        var organization = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == payment.OrganizationId, cancellationToken);
        if (organization != null)
        {
            if (organization.Status == TenantStatus.Suspended) organization.Status = TenantStatus.Active;
            // The number that just paid becomes the renewal number when none is on file.
            if (string.IsNullOrWhiteSpace(organization.BillingPhone) && UgandaPhone.IsValid(payment.MobileMoneyPhone))
                organization.BillingPhone = payment.MobileMoneyPhone;
        }
    }

    private async Task AfterSettlementAsync(Payment payment, CancellationToken cancellationToken)
    {
        var purpose = PurposeOf(payment);
        try
        {
            if (payment.Status == PaymentStatus.Succeeded && purpose?.Kind == KindModulePurchase
                && purpose.ModuleCode != null && Enum.TryParse<BillingCycle>(purpose.Cycle, out var cycle))
            {
                await _modules.ActivateAsync(payment.OrganizationId, purpose.ModuleCode, cycle);
            }
        }
        catch (Exception ex)
        {
            // The money is recorded; only the switch-on failed. Logged loudly so it can be applied by
            // hand — the payment row says exactly what was bought.
            _logger.LogError(ex, "Payment {Reference} succeeded but activating {Module} failed", payment.Id, purpose?.ModuleCode);
        }

        try
        {
            await NotifyAsync(payment, purpose, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Payment {Reference} settled but its notice was not sent", payment.Id);
        }
    }

    private async Task NotifyAsync(Payment payment, PaymentPurpose? purpose, CancellationToken cancellationToken)
    {
        var what = purpose?.ModuleName ?? payment.Description ?? "your payment";
        var amount = $"{payment.Currency} {payment.Amount.ToString("N0", CultureInfo.InvariantCulture)}";
        var (title, message, priority, icon) = payment.Status switch
        {
            PaymentStatus.Succeeded => ($"Payment received — {amount}",
                purpose?.Kind == KindModulePurchase ? $"{what} is active. Your receipt is on the Invoices tab." : $"{what} is paid. Your receipt is on the Invoices tab.",
                NotificationPriority.Normal, "check-circle-fill"),
            PaymentStatus.Review => ("Payment being checked",
                $"The payment of {amount} for {what} is being checked by the payment provider.",
                NotificationPriority.Normal, "hourglass-split"),
            PaymentStatus.Failed or PaymentStatus.Abandoned => ("Payment not completed",
                $"The payment of {amount} for {what} did not go through. Nothing was charged.",
                NotificationPriority.High, "x-circle-fill"),
            _ => (null, null, NotificationPriority.Normal, null)
        };
        if (title == null) return;

        // TO THE PEOPLE WHO CAN OPEN THE INVOICES TAB, and nobody else (2026-09-23). This was written
        // with no recipient, which the bell read as "everybody in the school" and the live push read
        // as "everybody on the platform": a newly onboarded teacher read the school's failed
        // payments. Sent once per settlement — SettleAsync only reaches here on a real change of
        // status, under the payment's own advisory lock.
        var audience = await NotificationAudience.HoldersAsync(_db, payment.OrganizationId, Permissions.BillingView, cancellationToken);
        await _notifications.NotifyManyAsync(audience, new CreateNotificationRequest
        {
            OrganizationId = payment.OrganizationId,
            EventKey = NotificationEventKeys.BillingPayment,
            Title = title,
            Message = message!,
            Type = NotificationType.SystemAlert,
            Priority = priority,
            Channels = NotificationChannel.InApp,
            ActionUrl = payment.InvoiceId is { } id ? BillingLinks.Invoice(id) : BillingLinks.Invoices,
            IconClass = icon
        }, cancellationToken);

        if (payment.Status != PaymentStatus.Succeeded) return;

        var organization = await _db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == payment.OrganizationId, cancellationToken);
        if (organization == null || string.IsNullOrWhiteSpace(organization.EffectiveBillingEmail)) return;

        var baseUrl = await PublicBaseUrlAsync();
        var invoiceNumber = payment.InvoiceId is { } invoiceId
            ? await _db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.InvoiceNumber).FirstOrDefaultAsync(cancellationToken)
            : null;
        await _notifications.SendEmailAsync(organization.Id, organization.EffectiveBillingEmail!,
            $"Receipt: {amount} received",
            EmailTemplates.Layout(
                "Payment received",
                null,
                new[]
                {
                    $"We received {EmailTemplates.B(amount)} from {EmailTemplates.B(organization.Name)} for {EmailTemplates.P(what)}.",
                    invoiceNumber != null ? $"Invoice {EmailTemplates.B(invoiceNumber)} is now paid." : "Thank you.",
                    $"Reference: {EmailTemplates.P(payment.Id.ToString())}"
                },
                "View invoice",
                EmailTemplates.Link(baseUrl, payment.InvoiceId is { } i2 ? BillingLinks.Invoice(i2) : BillingLinks.Invoices)),
            true, cancellationToken: cancellationToken);
    }

    // ================================================================== reconciliation

    public async Task<int> ReconcilePendingAsync(CancellationToken cancellationToken = default)
    {
        var settleAfter = DateTime.UtcNow.AddMinutes(-1);
        var open = await _db.Payments.AsNoTracking()
            .Where(p => (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Review)
                        && p.ExternalReferenceId != null && p.InitiatedAt <= settleAfter)
            .OrderBy(p => p.InitiatedAt)
            .Select(p => p.Id)
            .Take(200)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var id in open)
        {
            try
            {
                var before = await _db.Payments.AsNoTracking().Where(p => p.Id == id).Select(p => p.Status).FirstAsync(cancellationToken);
                var after = await RefreshAsync(id, cancellationToken);
                if (after != null && after != ToWire(before)) changed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconciling payment {Reference} failed; it will be tried again", id);
            }
        }
        return changed;
    }

    public async Task<Guid?> OpenPaymentForInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var id = await _db.Payments.AsNoTracking()
            .Where(p => p.InvoiceId == invoiceId && (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Review)
                        && p.ExternalReferenceId != null)
            .OrderByDescending(p => p.InitiatedAt)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return id;
    }

    public async Task<string?> ResolveReviewAsync(Guid referenceId, bool received, string note, string resolvedBy, CancellationToken cancellationToken = default)
    {
        var payment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == referenceId, cancellationToken);
        if (payment == null) return null;
        if (payment.Status != PaymentStatus.Review)
            throw new PaymentRequestException("NOT_IN_REVIEW",
                $"This payment is {ToWire(payment.Status)}, not held for review, so there is nothing to decide.", 409);

        var target = received ? PaymentStates.Succeeded : PaymentStates.Failed;
        var state = await SettleAsync(referenceId, new GatewayStatusResult(
            true, target, true,
            // "Received" confirms the amount due, or SettleAsync would hold an underpayment for review again.
            received ? payment.Amount : null,
            "platform", null,
            received ? null : "REVIEW_REJECTED",
            received ? "Confirmed as received by a platform administrator." : $"Not received: {note}"), cancellationToken);

        // Record the decision only when it was ours: the gateway may have settled it a moment earlier.
        if (state == target)
        {
            var row = await _db.Payments.FirstAsync(p => p.Id == referenceId, cancellationToken);
            var purpose = PurposeOf(row) ?? new PaymentPurpose("unknown", null, null, null);
            row.Metadata = Describe(purpose with
            {
                Resolution = new PaymentResolution(received ? ReviewOutcomes.Received : ReviewOutcomes.NotReceived, resolvedBy, DateTime.UtcNow, note)
            });
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Payment {Reference} held for review was marked {Outcome} by {By}: {Note}",
                referenceId, received ? "received" : "not received", resolvedBy, note);
        }

        return state;
    }

    public async Task<ReconciliationDto> GetReconciliationAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from) (from, to) = (to, from);
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var rows = await _db.Payments.AsNoTracking()
            .Where(p => p.ExternalReferenceId != null && p.InitiatedAt >= start && p.InitiatedAt < end)
            .Select(p => new
            {
                p.Id, p.OrganizationId, p.Description, p.PaymentMethod, p.MobileMoneyPhone,
                p.Amount, p.Currency, p.Status, p.InitiatedAt, p.CompletedAt, p.ErrorMessage, p.Metadata
            })
            .ToListAsync(cancellationToken);

        // Held for review, whatever the period: each needs a person, and one from last month must not
        // disappear because the reader is looking at this week.
        var inReview = await _db.Payments.AsNoTracking()
            .Where(p => p.ExternalReferenceId != null && p.Status == PaymentStatus.Review)
            .OrderBy(p => p.InitiatedAt)
            .Select(p => new
            {
                p.Id, p.OrganizationId, p.Description, p.PaymentMethod, p.MobileMoneyPhone,
                p.Amount, p.Currency, p.Status, p.InitiatedAt, p.CompletedAt, p.ErrorMessage, p.Metadata
            })
            .Take(200)
            .ToListAsync(cancellationToken);

        var days = new List<ReconciliationDayDto>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var day = rows.Where(r => DateOnly.FromDateTime(r.InitiatedAt) == d).ToList();
            days.Add(new ReconciliationDayDto(d,
                day.Count,
                day.Count(r => r.Status == PaymentStatus.Succeeded),
                day.Count(r => r.Status is PaymentStatus.Failed or PaymentStatus.Cancelled),
                day.Count(r => r.Status == PaymentStatus.Abandoned),
                day.Count(r => r.Status == PaymentStatus.Review),
                day.Count(r => r.Status is PaymentStatus.Pending or PaymentStatus.Processing),
                day.Where(r => r.Status == PaymentStatus.Succeeded).Sum(r => r.Amount)));
        }

        var orgIds = rows.Select(r => r.OrganizationId).Concat(inReview.Select(r => r.OrganizationId)).Distinct().ToList();
        var names = await _db.Organizations.AsNoTracking()
            .Where(o => orgIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);

        ReconciliationPaymentDto ToDto(Guid id, Guid orgId, string? description, PaymentMethod method, string? phone,
            decimal amount, string currency, PaymentStatus status, DateTime initiated, DateTime? completed, string? error, string? metadata) =>
            new(id,
                names.GetValueOrDefault(orgId) ?? "—",
                description ?? "—",
                method == PaymentMethod.Card ? "Card" : "Mobile Money",
                phone == null ? null : UgandaPhone.Display(phone, masked: true),
                amount, currency, ToWire(status), initiated, completed, error, ResolutionText(metadata));

        var recent = rows.OrderByDescending(r => r.InitiatedAt).Take(100)
            .Select(r => ToDto(r.Id, r.OrganizationId, r.Description, r.PaymentMethod, r.MobileMoneyPhone,
                r.Amount, r.Currency, r.Status, r.InitiatedAt, r.CompletedAt, r.ErrorMessage, r.Metadata))
            .ToList();
        var awaitingReview = inReview
            .Select(r => ToDto(r.Id, r.OrganizationId, r.Description, r.PaymentMethod, r.MobileMoneyPhone,
                r.Amount, r.Currency, r.Status, r.InitiatedAt, r.CompletedAt, r.ErrorMessage, r.Metadata))
            .ToList();

        return new ReconciliationDto(days.OrderByDescending(d => d.Day).ToList(),
            days.Sum(d => d.Sent), days.Sum(d => d.Confirmed), days.Sum(d => d.Failed), days.Sum(d => d.Abandoned),
            days.Sum(d => d.InReview), days.Sum(d => d.Pending), days.Sum(d => d.ConfirmedAmount), recent, awaitingReview);
    }

    // ================================================================== helpers

    private async Task<string> PublicBaseUrlAsync() => await _gateway.PublicBaseUrlAsync() ?? "https://qmgr.app";

    private static bool IsEmail(string value) =>
        value.Length is > 3 and <= 254 && System.Net.Mail.MailAddress.TryCreate(value, out var a) && a.Address == value;
}
