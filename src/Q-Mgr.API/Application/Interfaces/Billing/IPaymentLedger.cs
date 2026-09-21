using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Every gateway collection, from the first request to its final state — the ONE place a payment
/// changes state (2026-09-19).
///
/// The rule it exists for: <b>a ledger row exists before any money moves</b>. A collection starts as an
/// invoice plus a <c>Pending</c> <see cref="Domain.Entities.Billing.Payment"/> whose id is also the
/// gateway's reference and idempotency key. Only <see cref="SettleAsync"/> moves it on, and it does so
/// from what the gateway reports — reached from the signed webhook, from a status read, or from the
/// reconciliation job, all three the same code. Before this:
/// <list type="bullet">
/// <item>a renewal recorded the invoice PAID the moment the prompt was sent, before any money moved;</item>
/// <item>a module purchase was activated only while the browser kept polling, from a note kept thirty
///   minutes in a cache, so a customer who approved late or closed the tab paid for nothing;</item>
/// <item>a Mobile Money purchase wrote no invoice and no payment at all.</item>
/// </list>
/// </summary>
public interface IPaymentLedger
{
    /// <summary>Buy a module, or pay for one on trial. Validates everything, writes the invoice and the
    /// pending payment, then asks the gateway. The module is activated by settlement, never here.</summary>
    Task<PaymentStartDto> StartModulePurchaseAsync(
        Guid organizationId, string moduleCode, BillingCycle cycle, ModulePurchaseRequest request,
        string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Pay an open invoice by Mobile Money — a tenant settling a renewal by hand, or the
    /// renewal job charging the number on file.</summary>
    Task<PaymentStartDto> StartInvoicePaymentAsync(
        Guid organizationId, Guid invoiceId, string phoneNumber, string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>Where a payment stands for this organization, or null when it is not theirs (the
    /// caller answers 404). <paramref name="refresh"/> asks the gateway first when it is still open.</summary>
    Task<PaymentStatusDto?> GetStatusAsync(Guid organizationId, Guid referenceId, bool refresh, CancellationToken cancellationToken = default);

    /// <summary>Ask the gateway about one payment and settle what it says. Returns the resulting
    /// state, or null when there is no such payment.</summary>
    Task<string?> RefreshAsync(Guid referenceId, CancellationToken cancellationToken = default);

    /// <summary>Apply what the gateway reported. Idempotent: the webhook, a status read and the
    /// reconciliation job may all report the same verdict, and only the first changes anything.</summary>
    Task<string?> SettleAsync(Guid referenceId, GatewayStatusResult status, CancellationToken cancellationToken = default);

    /// <summary>Ask the gateway about every payment still open. Run by a recurring job.</summary>
    Task<int> ReconcilePendingAsync(CancellationToken cancellationToken = default);

    /// <summary>The newest open collection on an invoice, if one is still waiting for the payer.</summary>
    Task<Guid?> OpenPaymentForInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A platform administrator decides a payment the gateway held for review (2026-09-19):
    /// <paramref name="received"/> settles it as paid — the invoice is paid and a purchased module
    /// activated — otherwise as failed, which voids a purchase's invoice. Final, like every settlement:
    /// a later answer from the gateway does not change it. The decision, who made it and the note are
    /// kept on the payment. Returns the resulting state, or null when there is no such payment; refuses
    /// a payment that is not in review with <see cref="PaymentRequestException"/> (409).
    /// </summary>
    Task<string?> ResolveReviewAsync(Guid referenceId, bool received, string note, string resolvedBy, CancellationToken cancellationToken = default);

    /// <summary>The platform's view: sent, confirmed, failed, abandoned and in review per day.</summary>
    Task<ReconciliationDto> GetReconciliationAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Raised for a request the ledger refuses before anything is written — a tenant-readable
/// message and a code the controller maps to 400 or 409.</summary>
public sealed class PaymentRequestException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }

    public PaymentRequestException(string code, string message, int statusCode = 400) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}
