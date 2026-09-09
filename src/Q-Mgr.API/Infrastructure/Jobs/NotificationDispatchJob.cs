using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces;
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
        ILogger<NotificationDispatchJob> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire retries this three times on an unhandled exception. A send that FAILS (as opposed
    /// to throwing) is rethrown deliberately at the end so that path retries too — an SMTP timeout
    /// is caught and turned into a Failed result by NotificationService, and without the rethrow it
    /// would look to Hangfire like a success and never be retried.
    /// </summary>
    [AutomaticRetry(Attempts = MaxAttempts)]
    public async Task DispatchAsync(
        Guid notificationId,
        NotificationChannel channel,
        Guid organizationId,
        string recipient,
        string subject,
        string message,
        PerformContext? context = null)
    {
        var attempt = context?.GetJobParameter<int>("RetryCount") ?? 0;

        var result = channel switch
        {
            NotificationChannel.Sms => await _notificationService.SendSmsAsync(organizationId, recipient, message),
            NotificationChannel.Email => await _notificationService.SendEmailAsync(organizationId, recipient, subject, WrapEmailBody(subject, message), isHtml: true),
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
    /// Tells the organization's administrators that a channel is not working. Deliberately in-app
    /// only and deliberately keyed to nothing — emailing someone to say email is broken is a loop,
    /// and running it through the preference resolver would let the very person who needs to know
    /// have opted out of hearing it.
    /// </summary>
    private async Task RaiseDeliveryAlertAsync(Notification notification, NotificationChannel channel, string recipient, string? reason)
    {
        try
        {
            var admins = await _context.Users
                .AsNoTracking()
                .Where(u => u.OrganizationId == notification.OrganizationId && u.IsActive
                            && u.Role.RolePermissions.Any(rp => rp.Permission.Code == Domain.Constants.Permissions.NotificationsManage))
                .Select(u => u.Id)
                .Take(5)
                .ToListAsync();

            foreach (var adminId in admins)
            {
                _context.Notifications.Add(new Notification
                {
                    UserId = adminId,
                    OrganizationId = notification.OrganizationId,
                    BranchId = notification.BranchId,
                    Title = $"{channel} delivery is failing",
                    Message = $"Could not deliver \"{notification.Title}\" to {Mask(recipient)} after {MaxAttempts} attempts. {reason}",
                    Type = NotificationType.SystemAlert,
                    Priority = NotificationPriority.High,
                    IconClass = "exclamation-triangle",
                    ActionUrl = "/admin/notifications",
                    DeliveredVia = NotificationChannel.InApp
                });
            }

            await _context.SaveChangesAsync();
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
    private static string WrapEmailBody(string subject, string message)
        => Infrastructure.Email.EmailTemplates.Layout(
            title: subject,
            greeting: null,
            // One paragraph per line, HTML-encoded — the convention the layout documents and the
            // queue templates already follow. The message is staff-authored free text, so encoding
            // it is not optional.
            paragraphs: message
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Infrastructure.Email.EmailTemplates.P));
}
