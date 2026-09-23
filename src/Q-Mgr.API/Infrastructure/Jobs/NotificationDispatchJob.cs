using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces;
using QMgr.Application.DTOs;
using QMgr.API.Application.Services;
using QMgr.Domain.Entities.Notification;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Delivers one notification on one channel, off the request thread, with retries, and writes down
/// what happened.
///
/// Three things were wrong before this existed, and this fixes all three:
///
///  1. SMS and email were awaited inline inside <c>CreateInAppNotificationAsync</c>, so a slow SMTP
///     server stalled whatever HTTP request had triggered the notification — and a fan-out to a
///     class with two teachers stalled it twice.
///  2. There was no retry anywhere. A transient SMTP blip lost the message permanently.
///  3. <c>NotificationLog</c> was declared as a DbSet in QMgrDbContext and NOTHING in the codebase
///     ever wrote a row to it, so "was this actually delivered?" had no answer at all.
///
/// Hangfire's store here is PostgreSQL (see Program.cs), so an enqueued job survives a restart.
/// That is the whole reason a message broker is unnecessary and the standing "no new server-side
/// dependency" rule stays intact.
/// </summary>
public class NotificationDispatchJob
{
    private readonly QMgrDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly Infrastructure.Email.IEmailBrandService _brands;
    private readonly Services.Mobile.IPushSender _push;
    private readonly ILogger<NotificationDispatchJob> _logger;

    /// <summary>
    /// Matches <c>[AutomaticRetry(Attempts = 3)]</c> below. Used only to decide when a failure is
    /// FINAL and therefore worth telling an administrator about — retrying quietly is normal, and
    /// raising an alert on the first transient blip would train people to ignore the alert.
    /// </summary>
    private const int MaxAttempts = 3;

    public NotificationDispatchJob(
        QMgrDbContext context,
        INotificationService notificationService,
        Infrastructure.Email.IEmailBrandService brands,
        Services.Mobile.IPushSender push,
        ILogger<NotificationDispatchJob> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _brands = brands;
        _push = push;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire retries this three times on an unhandled exception. A send that FAILS (as opposed
    /// to throwing) is rethrown deliberately at the end so that path retries too — an SMTP timeout
    /// is caught and turned into a Failed result by NotificationService, and without the rethrow it
    /// would look to Hangfire like a success and never be retried.
    /// </summary>
    [AutomaticRetry(Attempts = MaxAttempts)]
    public Task DispatchAsync(
        Guid notificationId,
        NotificationChannel channel,
        Guid organizationId,
        string recipient,
        string subject,
        string message,
        PerformContext? context = null)
        => DispatchCoreAsync(notificationId, channel, organizationId, recipient, subject, message, bodyIsHtml: false, context);

    /// <summary>
    /// Email only, body already HTML (a staff digest with tables — see
    /// CreateNotificationRequest.EmailHtmlBody). A SEPARATE job method rather than a flag on
    /// <see cref="DispatchAsync"/>: Hangfire stores a queued job by method signature, so widening
    /// the existing one would leave every job queued before a deploy unable to deserialise.
    /// </summary>
    [AutomaticRetry(Attempts = MaxAttempts)]
    public Task DispatchHtmlEmailAsync(
        Guid notificationId,
        Guid organizationId,
        string recipient,
        string subject,
        string html,
        PerformContext? context = null)
        => DispatchCoreAsync(notificationId, NotificationChannel.Email, organizationId, recipient, subject, html, bodyIsHtml: true, context);

    /// <summary>
    /// A push to every handset this person has signed in on.
    ///
    /// <para>Takes a USER ID where the others take a phone number or an address, because the devices
    /// are resolved at SEND time rather than at enqueue time — a handset that signs in between the
    /// two would otherwise be missed, and one that signs out would still be tried.</para>
    ///
    /// <para>Its own job method for the same reason <see cref="DispatchHtmlEmailAsync"/> is: Hangfire
    /// stores a queued job by its method signature.</para>
    /// </summary>
    [AutomaticRetry(Attempts = MaxAttempts)]
    public async Task DispatchPushAsync(
        Guid notificationId,
        Guid organizationId,
        Guid userId,
        string title,
        string message,
        string? actionUrl,
        PerformContext? context = null)
    {
        var attempt = context?.GetJobParameter<int>("RetryCount") ?? 0;
        var result = await _push.SendAsync(userId, title, message, actionUrl);

        // The recipient is MASKED to the user's id rather than written as a push token. A token is a
        // credential for sending to that handset, and the delivery log is read by administrators.
        await RecordAsync(notificationId, NotificationChannel.Push, $"user:{userId}", result, attempt);

        // Rethrown so a genuine failure retries, exactly as the other channels do. A Skipped
        // outcome — nobody has a handset, or push is not configured — is NOT retried, because
        // nothing about it will be different in thirty seconds.
        if (result.Outcome == ChannelSendOutcome.Failed)
            throw new InvalidOperationException($"Push delivery failed: {result.Reason}");
    }

    private async Task DispatchCoreAsync(
        Guid notificationId,
        NotificationChannel channel,
        Guid organizationId,
        string recipient,
        string subject,
        string message,
        bool bodyIsHtml,
        PerformContext? context)
    {
        var attempt = context?.GetJobParameter<int>("RetryCount") ?? 0;

        var result = channel switch
        {
            NotificationChannel.Sms => await _notificationService.SendSmsAsync(organizationId, recipient, message),
            NotificationChannel.Email => await _notificationService.SendEmailAsync(organizationId, recipient, subject, bodyIsHtml ? message : WrapEmailBody(subject, message, await _brands.ForOrganizationAsync(organizationId)), isHtml: true),
            _ => ChannelSendResult.Skipped($"{channel} has no dispatcher.")
        };

        // One row per real ATTEMPT, not per notification. A message that succeeds on the third try
        // shows two failures and a success, because "it eventually worked" and "it worked first
        // time" are different operational facts.
        //
        // A SKIPPED outcome writes NO ROW. Nothing was attempted, so there is no delivery attempt
        // to record — and logging it as Success=false would put a red failure row against every
        // single notification for a tenant that simply has the channel switched off, which is the
        // skipped-vs-failed conflation this rework exists to end. "Email is off for this tenant"
        // is a settings fact, and it belongs on the settings screen, not in a per-message log.
        if (result.Outcome != ChannelSendOutcome.Skipped)
        {
            _context.NotificationLogs.Add(new NotificationLog
            {
                NotificationId = notificationId,
                Channel = channel,
                Recipient = recipient,
                Success = result.IsSent,
                ErrorMessage = result.Reason,
                RetryCount = attempt,
                LastRetryAt = attempt > 0 ? DateTime.UtcNow : null
            });
        }

        // Mirror onto the notification row so the bell and the notification list can show a
        // delivery state without joining the log.
        var notification = await _context.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId);
        if (notification != null && result.IsSent)
        {
            if (channel == NotificationChannel.Sms) { notification.SmsSent = true; notification.SmsSentAt = DateTime.UtcNow; }
            if (channel == NotificationChannel.Email) { notification.EmailSent = true; notification.EmailSentAt = DateTime.UtcNow; }
        }

        await _context.SaveChangesAsync();

        if (result.IsSent)
        {
            _logger.LogInformation("Notification {NotificationId} delivered via {Channel}", notificationId, channel);
            return;
        }

        if (result.Outcome == ChannelSendOutcome.Skipped)
        {
            // Not a failure. The channel is switched off, or there is no address — nothing to retry
            // and nothing to alarm anyone about.
            _logger.LogInformation("Notification {NotificationId} skipped {Channel}: {Reason}", notificationId, channel, result.Reason);
            return;
        }

        // A real failure. On the last attempt, tell somebody — a channel that has been silently
        // failing for a week is exactly the state this whole rework exists to make impossible.
        if (attempt >= MaxAttempts - 1 && notification != null)
            await RaiseDeliveryAlertAsync(notification, channel, recipient, result.Reason);

        // Rethrow so Hangfire retries. NotificationService catches transport exceptions and returns
        // Failed rather than throwing, so without this the job would look successful to Hangfire.
        throw new InvalidOperationException(
            $"{channel} delivery failed for notification {notificationId}: {result.Reason}");
    }

    /// <summary>
    /// Writes one delivery-log row, honouring the standing rule that a SKIPPED outcome writes none.
    ///
    /// <para>Extracted so the push path shares it rather than carrying a second copy — the row's
    /// shape, and above all the skipped-writes-nothing rule, is exactly the kind of thing that
    /// drifts when it exists twice.</para>
    /// </summary>
    private async Task RecordAsync(Guid notificationId, NotificationChannel channel, string recipient,
                                   ChannelSendResult result, int attempt)
    {
        if (result.Outcome != ChannelSendOutcome.Skipped)
        {
            _context.NotificationLogs.Add(new NotificationLog
            {
                NotificationId = notificationId,
                Channel = channel,
                Recipient = recipient,
                Success = result.IsSent,
                ErrorMessage = result.Reason,
                RetryCount = attempt,
                LastRetryAt = attempt > 0 ? DateTime.UtcNow : null
            });
        }

        if (result.IsSent)
        {
            var notification = await _context.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId);
            if (notification != null && channel == NotificationChannel.Push)
            {
                notification.PushSent = true;
                notification.PushSentAt = DateTime.UtcNow;
            }
        }

        await _context.SaveChangesAsync();

        if (result.IsSent)
            _logger.LogInformation("Notification {NotificationId} delivered via {Channel}", notificationId, channel);
        else if (result.Outcome == ChannelSendOutcome.Skipped)
            _logger.LogInformation("Notification {NotificationId} skipped {Channel}: {Reason}", notificationId, channel, result.Reason);
        else
            _logger.LogWarning("Notification {NotificationId} failed {Channel}: {Reason}", notificationId, channel, result.Reason);
    }

    /// <summary>How long one "delivery is failing" alert stands for its channel before another may be raised.</summary>
    private static readonly TimeSpan DeliveryAlertQuietPeriod = TimeSpan.FromHours(6);

    /// <summary>
    /// Tells the organization's administrators that a channel is not working. Deliberately in-app
    /// only and deliberately keyed to nothing — emailing someone to say email is broken is a loop,
    /// and running it through the preference resolver would let the very person who needs to know
    /// have opted out of hearing it.
    ///
    /// <para><b>Reworked 2026-09-23.</b> It picked five administrators with no ordering (so a school
    /// with six told a random five), read the role alone, and wrote rows straight into the table, so
    /// nothing was pushed and no badge moved until somebody reloaded. It also QUOTED THE FAILED
    /// NOTIFICATION'S TITLE — which is another person's notification, and can be a welfare or staff
    /// record title an administrator holding notifications.manage has no business reading. It now
    /// names the category and the masked address only, goes to every holder through the ordinary
    /// service, and is raised once per channel per <see cref="DeliveryAlertQuietPeriod"/>: a relay
    /// that is down fails every message, and one alert per failed message is how an alert gets ignored.</para>
    /// </summary>
    private async Task RaiseDeliveryAlertAsync(Notification notification, NotificationChannel channel, string recipient, string? reason)
    {
        try
        {
            var title = $"{channel} delivery is failing";
            var category = NotificationEventKeys.All.FirstOrDefault(e => e.Key == notification.EventKey)?.Category ?? "general";

            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _context.Database.BeginTransactionAsync();

                // One alert decision at a time per organization and channel: a relay outage fails a
                // hundred jobs at once, and each would otherwise find "no alert yet" and raise its own.
                var lockKey = $"delivery-alert:{notification.OrganizationId}:{channel}";
                await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))");

                var since = DateTime.UtcNow - DeliveryAlertQuietPeriod;
                var alreadyTold = await _context.Notifications.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(n => n.OrganizationId == notification.OrganizationId && n.Title == title && n.CreatedAt >= since);

                if (!alreadyTold)
                {
                    var admins = await NotificationAudience.HoldersAsync(_context, notification.OrganizationId, Domain.Constants.Permissions.NotificationsManage);
                    await _notificationService.NotifyManyAsync(admins, new CreateNotificationRequest
                    {
                        OrganizationId = notification.OrganizationId,
                        Title = title,
                        Message = $"A {category.ToLowerInvariant()} notification could not be delivered to {Mask(recipient)} after {MaxAttempts} attempts. {reason}",
                        Type = NotificationType.SystemAlert,
                        Priority = NotificationPriority.High,
                        IconClass = "exclamation-triangle",
                        ActionUrl = "/admin/settings?tab=notifications",
                        Channels = NotificationChannel.InApp
                    });
                }

                await tx.CommitAsync();
            });
        }
        catch (Exception ex)
        {
            // Failing to report a failure must not itself fail the job — the retry is the important
            // part, and this is the notification of last resort.
            _logger.LogError(ex, "Could not raise a delivery-failure alert for notification {NotificationId}", notification.Id);
        }
    }

    /// <summary>
    /// Enough of the address for an administrator to recognise which one broke, not enough to
    /// harvest a staff directory out of the delivery log.
    /// </summary>
    internal static string Mask(string recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient)) return "(none)";

        var at = recipient.IndexOf('@');
        if (at > 0)
        {
            var local = recipient[..at];
            var shown = local.Length <= 2 ? local[..1] : local[..2];
            return $"{shown}{new string('*', Math.Max(1, local.Length - shown.Length))}{recipient[at..]}";
        }

        return recipient.Length <= 4
            ? new string('*', recipient.Length)
            : new string('*', recipient.Length - 4) + recipient[^4..];
    }

    /// <summary>
    /// Wraps a plain message in the tenant's branded email chrome. EmailTemplates.Layout already
    /// existed and was already used by the queue notifier — staff notifications were the one path
    /// still sending an unstyled bare string.
    /// </summary>
    private static string WrapEmailBody(string subject, string message, Infrastructure.Email.EmailTemplates.EmailBrand brand)
        => Infrastructure.Email.EmailTemplates.Layout(
            title: subject,
            greeting: null,
            // One paragraph per line, HTML-encoded — the convention the layout documents and the
            // queue templates already follow. The message is staff-authored free text, so encoding
            // it is not optional.
            paragraphs: message
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Infrastructure.Email.EmailTemplates.P),
            // This is the busiest email path in the app — every staff notification a tenant sends
            // goes through here — so it is the one that most needs to carry the school's name
            // rather than ours.
            brand: brand);
}
