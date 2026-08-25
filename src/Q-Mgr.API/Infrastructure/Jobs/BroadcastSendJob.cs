using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Marketing;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Processes broadcast campaigns: picks up Scheduled broadcasts whose time has come, materializes
/// their recipient list from Contacts (skipping anyone opted out), and sends each pending recipient
/// through the existing real SMTP/SMS transport (NotificationService) — the same transport the
/// transactional token-lifecycle notifications already use, just fanned out to many recipients
/// instead of one.
/// </summary>
public class BroadcastSendJob
{
    private readonly QMgrDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BroadcastSendJob> _logger;

    public BroadcastSendJob(QMgrDbContext context, INotificationService notificationService, IConfiguration configuration, ILogger<BroadcastSendJob> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// COMPLIANCE: every broadcast message must carry a working, per-recipient unsubscribe link —
    /// this codebase had zero opt-out mechanism anywhere before this feature (see the earlier
    /// assessment). The API endpoint alone isn't enough; a recipient has to actually be able to
    /// find and use it from inside the message they received.
    /// </summary>
    private string AppendUnsubscribeFooter(string message, Guid optOutToken, bool isHtml)
    {
        var webBaseUrl = (_configuration["App:PublicWebBaseUrl"] ?? "https://localhost:5002").TrimEnd('/');
        var unsubscribeUrl = $"{webBaseUrl}/unsubscribe/{optOutToken}";

        return isHtml
            ? $"{message}<br/><br/><small><a href=\"{unsubscribeUrl}\">Unsubscribe</a> from these messages.</small>"
            : $"{message}\n\nReply STOP or visit {unsubscribeUrl} to unsubscribe.";
    }

    [AutomaticRetry(Attempts = 1)]
    public async Task ProcessAsync()
    {
        var now = DateTime.UtcNow;

        // CONCURRENCY: atomic conditional claim — only a broadcast still Scheduled (not already
        // picked up by an overlapping run) transitions to Sending here, checked as part of the
        // same UPDATE rather than read-then-write, same pattern as VisitorsController.CheckOut.
        var dueBroadcastIds = await _context.Broadcasts
            .Where(b => b.Status == BroadcastStatus.Scheduled && b.ScheduledAt != null && b.ScheduledAt <= now)
            .Select(b => b.Id)
            .ToListAsync();

        foreach (var broadcastId in dueBroadcastIds)
        {
            var claimed = await _context.Broadcasts
                .Where(b => b.Id == broadcastId && b.Status == BroadcastStatus.Scheduled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BroadcastStatus.Sending)
                    .SetProperty(b => b.SendStartedAt, now));

            if (claimed > 0)
            {
                await ProcessBroadcastAsync(broadcastId);
            }
        }

        // Also keep working through any broadcast still Sending (e.g. a previous run got
        // interrupted partway, or had more recipients than fit in one pass) — idempotent, since
        // MaterializeRecipientsAsync only inserts recipients that don't already exist and sending
        // only ever touches recipients still Pending.
        var inProgressIds = await _context.Broadcasts
            .Where(b => b.Status == BroadcastStatus.Sending)
            .Select(b => b.Id)
            .ToListAsync();

        foreach (var broadcastId in inProgressIds.Except(dueBroadcastIds))
        {
            await ProcessBroadcastAsync(broadcastId);
        }
    }

    private async Task ProcessBroadcastAsync(Guid broadcastId)
    {
        var broadcast = await _context.Broadcasts.FirstOrDefaultAsync(b => b.Id == broadcastId);
        if (broadcast == null) return;

        await MaterializeRecipientsAsync(broadcast);

        var pending = await _context.BroadcastRecipients
            .Include(r => r.Contact)
            .Where(r => r.BroadcastId == broadcastId && r.Status == RecipientStatus.Pending)
            .ToListAsync();

        foreach (var recipient in pending)
        {
            if (recipient.Contact == null) continue;

            // A contact can opt out between materialization and send — re-check right before
            // sending, not just at materialization time.
            if (recipient.Contact.OptedOut)
            {
                recipient.Status = RecipientStatus.OptedOut;
                continue;
            }

            bool sent;
            string? error = null;
            try
            {
                sent = broadcast.Channel switch
                {
                    BroadcastChannel.Email when !string.IsNullOrWhiteSpace(recipient.Contact.Email) =>
                        await _notificationService.SendEmailAsync(
                            broadcast.OrganizationId,
                            recipient.Contact.Email!,
                            broadcast.Subject ?? broadcast.Name,
                            AppendUnsubscribeFooter(broadcast.MessageBody, recipient.Contact.OptOutToken, isHtml: false),
                            isHtml: false),
                    BroadcastChannel.Sms when !string.IsNullOrWhiteSpace(recipient.Contact.Phone) =>
                        await _notificationService.SendSmsAsync(
                            broadcast.OrganizationId,
                            recipient.Contact.Phone!,
                            AppendUnsubscribeFooter(broadcast.MessageBody, recipient.Contact.OptOutToken, isHtml: false)),
                    BroadcastChannel.Telegram when !string.IsNullOrWhiteSpace(recipient.Contact.TelegramChatId) =>
                        await _notificationService.SendTelegramAsync(
                            broadcast.OrganizationId,
                            recipient.Contact.TelegramChatId!,
                            AppendUnsubscribeFooter(broadcast.MessageBody, recipient.Contact.OptOutToken, isHtml: false)),
                    BroadcastChannel.WhatsApp when !string.IsNullOrWhiteSpace(recipient.Contact.Phone) =>
                        await _notificationService.SendWhatsAppAsync(
                            broadcast.OrganizationId,
                            recipient.Contact.Phone!,
                            AppendUnsubscribeFooter(broadcast.MessageBody, recipient.Contact.OptOutToken, isHtml: false)),
                    _ => false
                };
                if (!sent) error = "No usable contact address for this channel, or the transport is disabled/unconfigured";
            }
            catch (Exception ex)
            {
                sent = false;
                error = ex.Message;
                _logger.LogError(ex, "Failed sending broadcast {BroadcastId} to contact {ContactId}", broadcastId, recipient.ContactId);
            }

            recipient.Status = sent ? RecipientStatus.Sent : RecipientStatus.Failed;
            recipient.SentAt = sent ? DateTime.UtcNow : null;
            recipient.ErrorMessage = error;
        }

        await _context.SaveChangesAsync();

        var stillPending = await _context.BroadcastRecipients
            .AnyAsync(r => r.BroadcastId == broadcastId && r.Status == RecipientStatus.Pending);

        if (!stillPending)
        {
            broadcast.SentCount = await _context.BroadcastRecipients.CountAsync(r => r.BroadcastId == broadcastId && r.Status == RecipientStatus.Sent);
            broadcast.FailedCount = await _context.BroadcastRecipients.CountAsync(r => r.BroadcastId == broadcastId && r.Status == RecipientStatus.Failed);
            broadcast.Status = BroadcastStatus.Sent;
            broadcast.SendCompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Broadcast {BroadcastId} completed: {Sent} sent, {Failed} failed of {Total} recipients",
                broadcastId, broadcast.SentCount, broadcast.FailedCount, broadcast.TotalRecipients);
        }
    }

    private async Task MaterializeRecipientsAsync(Broadcast broadcast)
    {
        var alreadyMaterialized = await _context.BroadcastRecipients.AnyAsync(r => r.BroadcastId == broadcast.Id);
        if (alreadyMaterialized) return;

        var contactsQuery = _context.Contacts
            .Where(c => c.OrganizationId == broadcast.OrganizationId && !c.OptedOut);

        if (!string.IsNullOrWhiteSpace(broadcast.AudienceTagFilter))
        {
            var tags = broadcast.AudienceTagFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            contactsQuery = contactsQuery.Where(c => c.Tags != null && tags.Any(tag => c.Tags.Contains(tag)));
        }

        var contacts = await contactsQuery.ToListAsync();

        var recipients = contacts.Select(c => new BroadcastRecipient
        {
            BroadcastId = broadcast.Id,
            ContactId = c.Id,
            Status = RecipientStatus.Pending
        }).ToList();

        _context.BroadcastRecipients.AddRange(recipients);
        broadcast.TotalRecipients = recipients.Count;
        await _context.SaveChangesAsync();
    }
}

public static class BroadcastJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Every minute, same cadence as RateLimitSyncJob — trades a small worst-case delay
        // (up to ~60s after a broadcast's scheduled time, or after clicking "Send Now") for a
        // simple, reliable polling model instead of a bespoke immediate-dispatch path.
        RecurringJob.AddOrUpdate<BroadcastSendJob>(
            "process-broadcasts",
            job => job.ProcessAsync(),
            Cron.Minutely);
    }
}
