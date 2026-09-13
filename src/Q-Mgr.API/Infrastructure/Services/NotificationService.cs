using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Jobs;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Main notification service handling SMS, Email, In-App, and Push notifications.
/// SMS is sent via the CRM API gateway.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly QMgrDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly INotificationHubService _hubService;
    private readonly IMediaStorageService _mediaStorageService;
    private readonly INotificationPreferenceResolver _preferences;
    private readonly ISmtpProfileResolver _smtp;
    private readonly ILogger<NotificationService> _logger;
    // Keyed by OrganizationId — a single cached field previously returned whatever organization's
    // settings were fetched first for the lifetime of this (scoped) instance, meaning every other
    // organization processed in the same scope (e.g. a billing job looping over many orgs) sent
    // SMS/email through the wrong organization's gateway/SMTP credentials.
    private readonly Dictionary<Guid, (NotificationSettings? Settings, DateTime FetchedAt)> _settingsCache = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public NotificationService(
        QMgrDbContext context,
        IHttpClientFactory httpClientFactory,
        INotificationHubService hubService,
        IMediaStorageService mediaStorageService,
        INotificationPreferenceResolver preferences,
        ISmtpProfileResolver smtp,
        ILogger<NotificationService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _hubService = hubService;
        _mediaStorageService = mediaStorageService;
        _preferences = preferences;
        _smtp = smtp;
        _logger = logger;
    }

    private async Task<NotificationSettings?> GetSettingsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (_settingsCache.TryGetValue(organizationId, out var cached) && DateTime.UtcNow - cached.FetchedAt < _cacheExpiry)
        {
            return cached.Settings;
        }

        var settings = await _context.NotificationSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken);
        _settingsCache[organizationId] = (settings, DateTime.UtcNow);

        return settings;
    }

    #region SMS

    public async Task<ChannelSendResult> SendSmsAsync(Guid organizationId, string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null || !settings.SmsEnabled)
        {
            _logger.LogInformation("SMS notifications disabled or not configured");
            return ChannelSendResult.Skipped("SMS is not switched on for this organization.");
        }

        if (string.IsNullOrWhiteSpace(phoneNumber))
            return ChannelSendResult.Skipped("No phone number on file.");

        try
        {
            var client = _httpClientFactory.CreateClient("SmsGateway");

            // Configure client with gateway URL if specified
            if (!string.IsNullOrEmpty(settings.SmsGatewayUrl))
            {
                client.BaseAddress = new Uri(settings.SmsGatewayUrl);
            }

            // Prepare SMS request for CRM API
            var smsRequest = new
            {
                Message = message,
                Recipient = NormalizePhoneNumber(phoneNumber),
                Sender = settings.SmsSenderId ?? "Q-Mgr"
            };

            var customerId = settings.SmsCustomerId ?? "default";
            var response = await client.PostAsJsonAsync(
                $"api/sms/{customerId}/send",
                smsRequest,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SmsSendResponse>(cancellationToken: cancellationToken);
                if (result?.Success == true)
                {
                    _logger.LogInformation("SMS sent successfully to {PhoneNumber}: {Message}", phoneNumber, result.Message);
                    return ChannelSendResult.Sent();
                }

                // A 200 carrying Success=false is the gateway rejecting the message (bad number,
                // no credit). It used to be indistinguishable from a transport failure.
                _logger.LogWarning("SMS gateway accepted the request but reported failure: {Message}", result?.Message);
                return ChannelSendResult.Failed(result?.Message ?? "The SMS gateway rejected the message.");
            }

            _logger.LogWarning("SMS send failed: {StatusCode}", response.StatusCode);
            return ChannelSendResult.Failed($"The SMS gateway returned {(int)response.StatusCode} {response.StatusCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SMS to {PhoneNumber}", phoneNumber);
            return ChannelSendResult.Failed(ex.Message);
        }
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        // Remove spaces, dashes, and other non-numeric characters except +
        var normalized = new string(phoneNumber.Where(c => char.IsDigit(c) || c == '+').ToArray());

        // If starts with 0, assume local number and add default country code
        if (normalized.StartsWith("0"))
        {
            normalized = "256" + normalized.Substring(1);
        }

        return normalized;
    }

    #endregion

    #region Email

    public async Task<ChannelSendResult> SendEmailAsync(Guid organizationId, string email, string subject, string body, bool isHtml = true, IReadOnlyList<NotificationAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null || !settings.EmailEnabled)
        {
            _logger.LogInformation("Email notifications disabled or not configured");
            return ChannelSendResult.Skipped("Email is not switched on for this organization.");
        }

        // Falls back to the platform account when this organization has not configured its own —
        // see ISmtpProfileResolver. Before that fallback existed, "email is on" plus "no SMTP host"
        // meant every message was silently skipped, which is the state every tenant starts in.
        var profile = await _smtp.ResolveAsync(settings);
        if (profile == null)
        {
            _logger.LogWarning("Email SMTP settings not configured");
            return ChannelSendResult.Skipped("Neither this organization nor the platform has an SMTP host and from-address configured.");
        }

        if (string.IsNullOrWhiteSpace(email))
            return ChannelSendResult.Skipped("No email address on file.");

        try
        {
            using var smtpClient = new SmtpClient(profile.Host, profile.Port)
            {
                EnableSsl = profile.UseSsl,
                Credentials = !string.IsNullOrEmpty(profile.Username)
                    ? new NetworkCredential(profile.Username, profile.Password)
                    : null
            };

            using var mailMessage = new MailMessage
            {
                From = new MailAddress(profile.FromEmail, profile.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = isHtml
            };
            mailMessage.To.Add(email);

            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    var attachmentStream = await _mediaStorageService.DownloadAsync(attachment.FilePath, cancellationToken);
                    if (attachmentStream != null)
                    {
                        // Ownership passes to Attachment/MailMessage from here — the `using var
                        // mailMessage` above disposes it along with everything else on the way out.
                        mailMessage.Attachments.Add(new Attachment(attachmentStream, attachment.FileName, attachment.MimeType));
                    }
                    else
                    {
                        _logger.LogWarning("Attachment {FilePath} could not be read; sending {Email} without it", attachment.FilePath, email);
                    }
                }
            }

            await smtpClient.SendMailAsync(mailMessage, cancellationToken);
            _logger.LogInformation("Email sent successfully to {Email}", email);
            return ChannelSendResult.Sent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", email);
            return ChannelSendResult.Failed(ex.Message);
        }
    }

    #endregion

    #region Telegram

    public async Task<bool> SendTelegramAsync(Guid organizationId, string chatId, string message, IReadOnlyList<NotificationAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null || !settings.TelegramEnabled)
        {
            _logger.LogInformation("Telegram notifications disabled or not configured");
            return false;
        }

        if (string.IsNullOrEmpty(settings.TelegramBotToken))
        {
            _logger.LogWarning("Telegram bot token not configured");
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("TelegramApi");

            if (attachments == null || attachments.Count == 0)
            {
                return await TelegramSendMessageAsync(client, settings.TelegramBotToken, chatId, message, cancellationToken);
            }

            if (attachments.Count == 1)
            {
                // The one-attachment case carries the text as the attachment's own caption —
                // one API call, same as before this method supported more than one attachment.
                return await TelegramSendAttachmentAsync(client, settings.TelegramBotToken, chatId, attachments[0], message, cancellationToken);
            }

            // More than one: Telegram has no single call that sends several arbitrary
            // attachments with one shared caption, so the text goes out on its own first, and
            // the primary result is whether THAT succeeded — a later attachment failing is
            // logged but doesn't flip it, since the recipient already has the core message.
            var textSent = await TelegramSendMessageAsync(client, settings.TelegramBotToken, chatId, message, cancellationToken);
            foreach (var attachment in attachments)
            {
                var attachmentSent = await TelegramSendAttachmentAsync(client, settings.TelegramBotToken, chatId, attachment, caption: null, cancellationToken);
                if (!attachmentSent)
                {
                    _logger.LogWarning("Telegram attachment {FileName} failed to send to chat {ChatId} (text already sent)", attachment.FileName, chatId);
                }
            }
            return textSent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to chat {ChatId}", chatId);
            return false;
        }
    }

    private async Task<bool> TelegramSendMessageAsync(HttpClient client, string botToken, string chatId, string text, CancellationToken cancellationToken)
    {
        var request = new { chat_id = chatId, text };
        var response = await client.PostAsJsonAsync($"bot{botToken}/sendMessage", request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Telegram message sent successfully to chat {ChatId}", chatId);
            return true;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("Telegram send failed: {StatusCode} - {Error}", response.StatusCode, error);
        return false;
    }

    /// <summary>
    /// Sends one attachment via sendPhoto (image/* mime types) or sendDocument (everything
    /// else) — Telegram fetches attachment.Url itself server-side rather than receiving bytes.
    /// caption may be null (used when the text already went out as its own message).
    /// </summary>
    private async Task<bool> TelegramSendAttachmentAsync(HttpClient client, string botToken, string chatId, NotificationAttachment attachment, string? caption, CancellationToken cancellationToken)
    {
        var isImage = attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var endpoint = isImage ? "sendPhoto" : "sendDocument";
        object request = isImage
            ? new { chat_id = chatId, photo = attachment.Url, caption }
            : new { chat_id = chatId, document = attachment.Url, caption };

        var response = await client.PostAsJsonAsync($"bot{botToken}/{endpoint}", request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Telegram attachment {FileName} sent successfully to chat {ChatId}", attachment.FileName, chatId);
            return true;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("Telegram attachment send failed: {StatusCode} - {Error}", response.StatusCode, error);
        return false;
    }

    #endregion

    #region WhatsApp

    public async Task<bool> SendWhatsAppAsync(Guid organizationId, string phoneNumber, string message, IReadOnlyList<NotificationAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null || !settings.WhatsAppEnabled)
        {
            _logger.LogInformation("WhatsApp notifications disabled or not configured");
            return false;
        }

        if (string.IsNullOrEmpty(settings.WhatsAppPhoneNumberId) || string.IsNullOrEmpty(settings.WhatsAppAccessToken))
        {
            _logger.LogWarning("WhatsApp Cloud API credentials not configured");
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("WhatsAppApi");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.WhatsAppAccessToken);

            // WhatsApp Cloud API requires E.164 digits with no leading '+' in the "to" field.
            var normalizedNumber = new string(NormalizePhoneNumber(phoneNumber).Where(char.IsDigit).ToArray());

            if (attachments == null || attachments.Count == 0)
            {
                return await WhatsAppSendTextAsync(client, settings.WhatsAppPhoneNumberId, normalizedNumber, phoneNumber, message, cancellationToken);
            }

            if (attachments.Count == 1)
            {
                // One attachment carries the text as its own caption — a single API call, same
                // as before this method supported more than one attachment.
                return await WhatsAppSendAttachmentAsync(client, settings.WhatsAppPhoneNumberId, normalizedNumber, phoneNumber, attachments[0], message, cancellationToken);
            }

            // More than one: no single Cloud API call sends several arbitrary attachments with
            // one shared caption, so the text goes out as its own message first, and the primary
            // result is whether THAT succeeded — a later attachment failing is logged but
            // doesn't flip it, since the recipient already has the core message.
            var textSent = await WhatsAppSendTextAsync(client, settings.WhatsAppPhoneNumberId, normalizedNumber, phoneNumber, message, cancellationToken);
            foreach (var attachment in attachments)
            {
                var attachmentSent = await WhatsAppSendAttachmentAsync(client, settings.WhatsAppPhoneNumberId, normalizedNumber, phoneNumber, attachment, caption: null, cancellationToken);
                if (!attachmentSent)
                {
                    _logger.LogWarning("WhatsApp attachment {FileName} failed to send to {PhoneNumber} (text already sent)", attachment.FileName, phoneNumber);
                }
            }
            return textSent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp message to {PhoneNumber}", phoneNumber);
            return false;
        }
    }

    private async Task<bool> WhatsAppSendTextAsync(HttpClient client, string phoneNumberId, string normalizedNumber, string phoneNumberForLogging, string body, CancellationToken cancellationToken)
    {
        var request = new
        {
            messaging_product = "whatsapp",
            to = normalizedNumber,
            type = "text",
            text = new { body }
        };
        return await WhatsAppPostAsync(client, phoneNumberId, phoneNumberForLogging, request, cancellationToken);
    }

    /// <summary>
    /// Sends one attachment via type: image (image/* mime types) or type: document (everything
    /// else) — Cloud API fetches attachment.Url itself via its "link" field, same fetch-from-URL
    /// model as Telegram. caption may be null (used when the text already went out as its own
    /// message).
    /// </summary>
    private async Task<bool> WhatsAppSendAttachmentAsync(HttpClient client, string phoneNumberId, string normalizedNumber, string phoneNumberForLogging, NotificationAttachment attachment, string? caption, CancellationToken cancellationToken)
    {
        var isImage = attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        object request = isImage
            ? new
            {
                messaging_product = "whatsapp",
                to = normalizedNumber,
                type = "image",
                image = new { link = attachment.Url, caption }
            }
            : new
            {
                messaging_product = "whatsapp",
                to = normalizedNumber,
                type = "document",
                document = new { link = attachment.Url, filename = attachment.FileName, caption }
            };

        return await WhatsAppPostAsync(client, phoneNumberId, phoneNumberForLogging, request, cancellationToken);
    }

    private async Task<bool> WhatsAppPostAsync(HttpClient client, string phoneNumberId, string phoneNumberForLogging, object request, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync($"{phoneNumberId}/messages", request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("WhatsApp message sent successfully to {PhoneNumber}", phoneNumberForLogging);
            return true;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("WhatsApp send failed: {StatusCode} - {Error}", response.StatusCode, error);
        return false;
    }

    #endregion

    #region Push Notifications

    public Task<bool> SendPushNotificationAsync(string deviceToken, string title, string body, Dictionary<string, string>? data = null, CancellationToken cancellationToken = default)
    {
        // TODO: Implement Firebase Cloud Messaging integration
        _logger.LogWarning("Push notifications not yet implemented");
        return Task.FromResult(false);
    }

    #endregion

    #region In-App Notifications

    public async Task<Notification> CreateInAppNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default)
    {
        var notification = new Notification
        {
            UserId = request.UserId,
            TokenId = request.TokenId,
            BranchId = request.BranchId,
            OrganizationId = request.OrganizationId,
            Title = request.Title,
            Message = request.Message,
            Type = request.Type,
            Priority = request.Priority,
            IconClass = request.IconClass ?? GetDefaultIconClass(request.Type),
            ActionUrl = request.ActionUrl,
            MetaData = request.MetaData != null ? JsonSerializer.Serialize(request.MetaData) : null,
            ExpiresAt = request.ExpiresAt,
            DeliveredVia = NotificationChannel.InApp
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync(cancellationToken);

        // Send real-time notification via SignalR
        if (request.UserId.HasValue)
        {
            await _hubService.SendToUserAsync(request.UserId.Value, notification);
            var unreadCount = await GetUnreadCountAsync(request.UserId.Value, request.OrganizationId, cancellationToken);
            await _hubService.NotifyUnreadCountAsync(request.UserId.Value, unreadCount);
        }
        else if (request.BranchId.HasValue)
        {
            await _hubService.SendToBranchAsync(request.BranchId.Value, notification);
        }
        else
        {
            await _hubService.SendToAllAsync(notification);
        }

        // ── Out-of-band channels ────────────────────────────────────────────────────────────
        //
        // Until 2026-09-09 SMS and email were sent RIGHT HERE, awaited on the request thread, with
        // no retry: a slow SMTP server stalled whatever HTTP request had triggered the notification,
        // and a transient blip lost the message permanently. Now the row and the SignalR push above
        // stay synchronous — the bell is what "instant" means — and the rest is handed to Hangfire,
        // which in this project is backed by PostgreSQL (Hangfire.PostgreSql, see Program.cs) and
        // therefore survives a restart. No broker, no new server-side dependency.

        // Resolve the recipient's own contact details when the caller did not supply them. Every
        // caller used to have to do this itself, and forgetting meant the channel silently no-opped.
        var (phone, email) = await ResolveRecipientContactAsync(request, cancellationToken);

        // Then narrow to what this person and this tenant actually want. A caller asking for SMS
        // does not force SMS on somebody who has turned it off.
        var channels = await _preferences.ResolveAsync(
            request.UserId, request.OrganizationId, request.EventKey, request.Channels, cancellationToken);

        var subject = request.EmailSubject ?? request.Title;

        if (channels.HasFlag(NotificationChannel.Sms) && !string.IsNullOrWhiteSpace(phone))
        {
            EnqueueDispatch(notification.Id, NotificationChannel.Sms, request.OrganizationId, phone!, subject, request.Message);
            notification.DeliveredVia |= NotificationChannel.Sms;
        }

        if (channels.HasFlag(NotificationChannel.Email) && !string.IsNullOrWhiteSpace(email))
        {
            EnqueueDispatch(notification.Id, NotificationChannel.Email, request.OrganizationId, email!, subject, request.Message);
            notification.DeliveredVia |= NotificationChannel.Email;
        }

        // Push has no queue: there is no mobile app yet, so SendPushNotificationAsync is a stub and
        // enqueueing a job to call a stub would only make the queue harder to read.
        if (channels.HasFlag(NotificationChannel.Push) && !string.IsNullOrEmpty(request.DeviceToken))
        {
            var pushSent = await SendPushNotificationAsync(request.DeviceToken, request.Title, request.Message, null, cancellationToken);
            notification.PushSent = pushSent;
            notification.PushSentAt = pushSent ? DateTime.UtcNow : null;
            notification.DeliveredVia |= NotificationChannel.Push;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created notification {NotificationId} for user {UserId} (channels: {Channels})",
            notification.Id, request.UserId, notification.DeliveredVia);
        return notification;
    }

    /// <summary>
    /// Where to reach the recipient. An explicitly-supplied address always wins — that is how the
    /// queue notifier reaches a member of the public who has no user account — and otherwise the
    /// target user's own row is read. Before this, the caller had to do the lookup itself and
    /// forgetting silently dropped the channel.
    /// </summary>
    private async Task<(string? Phone, string? Email)> ResolveRecipientContactAsync(
        CreateNotificationRequest request, CancellationToken cancellationToken)
    {
        var phone = request.PhoneNumber;
        var email = request.Email;

        if ((!string.IsNullOrWhiteSpace(phone) && !string.IsNullOrWhiteSpace(email)) || request.UserId == null)
            return (phone, email);

        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == request.UserId)
            .Select(u => new { u.Phone, u.AlternatePhone, u.Email })
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null) return (phone, email);

        // AlternatePhone is a genuine fallback, not a second recipient — one message, to whichever
        // number the school actually filled in.
        return (
            string.IsNullOrWhiteSpace(phone) ? (user.Phone ?? user.AlternatePhone) : phone,
            string.IsNullOrWhiteSpace(email) ? user.Email : email
        );
    }

    /// <summary>
    /// Hands one channel send to Hangfire. Wrapped in try/catch because enqueueing runs AFTER the
    /// notification row is already committed: if the job store is unreachable, the bell has still
    /// rung and the request must not fail for it. Losing the email in that case is a degraded
    /// success, and it is logged as one.
    /// </summary>
    private void EnqueueDispatch(Guid notificationId, NotificationChannel channel, Guid organizationId,
        string recipient, string subject, string message)
    {
        try
        {
            BackgroundJob.Enqueue<NotificationDispatchJob>(job =>
                job.DispatchAsync(notificationId, channel, organizationId, recipient, subject, message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue {Channel} delivery for notification {NotificationId}", channel, notificationId);
        }
    }

    public async Task<IEnumerable<Notification>> GetUserNotificationsAsync(Guid userId, Guid organizationId, bool unreadOnly = false, int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = _context.Notifications
            .Where(n => n.OrganizationId == organizationId)
            .Where(n => n.UserId == userId || n.UserId == null)
            .Where(n => n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow)
            .AsNoTracking();

        if (unreadOnly)
        {
            query = query.Where(n => !n.IsRead);
        }

        return await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .Where(n => n.OrganizationId == organizationId)
            .Where(n => (n.UserId == userId || n.UserId == null) && !n.IsRead)
            .Where(n => n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow)
            .CountAsync(cancellationToken);
    }

    public async Task<bool> MarkAsReadAsync(Guid notificationId, Guid callerId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var notification = await _context.Notifications.FindAsync(new object[] { notificationId }, cancellationToken);
        if (notification == null || notification.OrganizationId != organizationId || (notification.UserId.HasValue && notification.UserId != callerId))
            return false;

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            if (notification.UserId.HasValue)
            {
                var unreadCount = await GetUnreadCountAsync(notification.UserId.Value, organizationId, cancellationToken);
                await _hubService.NotifyUnreadCountAsync(notification.UserId.Value, unreadCount);
            }
        }

        return true;
    }

    public async Task MarkAllAsReadAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await _context.Notifications
            .Where(n => n.OrganizationId == organizationId)
            .Where(n => (n.UserId == userId || n.UserId == null) && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), cancellationToken);

        await _hubService.NotifyUnreadCountAsync(userId, 0);
    }

    public async Task<bool> DeleteNotificationAsync(Guid notificationId, Guid callerId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var notification = await _context.Notifications.FindAsync(new object[] { notificationId }, cancellationToken);
        if (notification == null || notification.OrganizationId != organizationId || (notification.UserId.HasValue && notification.UserId != callerId))
            return false;

        _context.Notifications.Remove(notification);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task CleanupOldNotificationsAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        await _context.Notifications
            .Where(n => n.CreatedAt < cutoff || (n.ExpiresAt != null && n.ExpiresAt < DateTime.UtcNow))
            .ExecuteDeleteAsync(cancellationToken);

        _logger.LogInformation("Cleaned up notifications older than {RetentionDays} days", retentionDays);
    }

    #endregion

    #region Helpers

    private static string GetDefaultIconClass(NotificationType type) => type switch
    {
        NotificationType.TokenCreated => "bi-ticket-perforated",
        NotificationType.TokenCalled => "bi-bell-fill",
        NotificationType.TokenReminder => "bi-clock",
        NotificationType.TokenTransferred => "bi-arrow-left-right",
        NotificationType.TokenCancelled => "bi-x-circle",
        NotificationType.QueueUpdate => "bi-people",
        NotificationType.SystemAlert => "bi-exclamation-triangle",
        NotificationType.CounterAlert => "bi-display",
        NotificationType.VisitorArrived => "bi-person-check",
        _ => "bi-info-circle"
    };

    #endregion

    #region Response Models

    private class SmsSendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    #endregion
}
