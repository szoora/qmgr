using Hangfire;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// BUG FIX: TriggerWebhookAsync (WebhookService, called from every queue-state-change handler)
/// only ever INSERTS WebhooksOutgoing rows with Status="pending" — delivery happens in
/// IWebhookService.ProcessPendingWebhooksAsync, but nothing in the API project ever called it
/// (confirmed via a full-project grep: no BackgroundService/IHostedService existed anywhere, and
/// no endpoint triggers it manually either). Every webhook a tenant configured was queued and then
/// sat in the table forever. Registers a Hangfire recurring job to actually drain the queue, same
/// pattern as RateLimitJobsRegistration/BillingJobsRegistration.
/// </summary>
public static class WebhookJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Every minute — webhooks are meant to notify external systems close to real-time;
        // ProcessPendingWebhooksAsync's own per-webhook Attempts/Status bookkeeping (up to 5
        // tries) already handles retry backoff, this job just needs to keep draining the queue.
        RecurringJob.AddOrUpdate<IWebhookService>(
            "process-pending-webhooks",
            job => job.ProcessPendingWebhooksAsync(CancellationToken.None),
            Cron.Minutely);
    }
}
