using Hangfire;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces.Billing;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Every five minutes, asks the sacc.ug gateway about each payment still open (2026-09-19).
///
/// The gateway's signed webhook settles a payment within seconds of the payer answering; this job is
/// the fallback for a webhook that was lost, a callback address that was wrong, or a collect whose
/// answer never came back. It also closes a payment the gateway never received (after 30 minutes)
/// and one still open after the gateway's own 72-hour deadline. All of it goes through
/// <see cref="IPaymentLedger.RefreshAsync"/> — the same settlement the webhook uses.
/// </summary>
public sealed class PaymentReconciliationJob
{
    private readonly IPaymentLedger _ledger;
    private readonly ISaccGateway _gateway;
    private readonly ILogger<PaymentReconciliationJob> _logger;

    public PaymentReconciliationJob(IPaymentLedger ledger, ISaccGateway gateway, ILogger<PaymentReconciliationJob> logger)
    {
        _ledger = ledger;
        _gateway = gateway;
        _logger = logger;
    }

    /// <summary>One run at a time: a slow gateway must not let two runs ask about the same payments.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync()
    {
        var config = await _gateway.GetConfigAsync();
        if (!config.HasValidBaseUrl || !config.HasApiKey) return;

        var changed = await _ledger.ReconcilePendingAsync();
        if (changed > 0) _logger.LogInformation("Payment reconciliation settled {Count} payment(s)", changed);
    }
}
