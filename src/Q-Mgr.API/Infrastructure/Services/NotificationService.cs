using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Infrastructure.Data;

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
        ILogger<NotificationService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _hubService = hubService;
        _mediaStorageService = mediaStorageService;
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

    public async Task<bool> SendSmsAsync(Guid organizationId, string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null || !settings.SmsEnabled)
        {
            _logger.LogInformation("SMS notifications disabled or not configured");
            return false;
        }

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
                _logger.LogInformation("SMS sent successfully to {PhoneNumber}: {Message}", phoneNumber, result?.Message);
                return result?.Success ?? false;
            }

            _logger.LogWarning("SMS send failed: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SMS to {PhoneNumber}", phoneNumber);
            return false;
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

    public async Task<bool> SendEmailAsync(Guid organizationId, string email, string subject, string body, bool isHtml = true, IReadOnlyList<NotificationAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null || !settings.EmailEnabled)
        {
            _logger.LogInformation("Email notifications disabled or not configured");
            return false;
        }

        if (string.IsNullOrEmpty(settings.SmtpHost) || string.IsNullOrEmpty(settings.EmailFromAddress))
        {
            _logger.LogWarning("Email SMTP settings not configured");
            return false;
        }

        try
        {
            using var smtpClient = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.SmtpUseSsl,
                Credentials = !string.IsNullOrEmpty(settings.SmtpUsername)
                    ? new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword)
                    : null
            };

            using var mailMessage = new MailMessage
            {
                From = new MailAddress(settings.EmailFromAddress, settings.EmailFromName ?? "Q-Mgr"),
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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", email);
            return false;
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

        // Send via additional channels if requested
        if (request.Channels.HasFlag(NotificationChannel.Sms) && !string.IsNullOrEmpty(request.PhoneNumber))
        {
            var smsSent = await SendSmsAsync(request.OrganizationId, request.PhoneNumber, request.Message, cancellationToken);
            notification.SmsSent = smsSent;
            notification.SmsSentAt = smsSent ? DateTime.UtcNow : null;
            notification.DeliveredVia |= NotificationChannel.Sms;
        }

        if (request.Channels.HasFlag(NotificationChannel.Email) && !string.IsNullOrEmpty(request.Email))
        {
            var emailSubject = request.EmailSubject ?? request.Title;
            var emailSent = await SendEmailAsync(request.OrganizationId, request.Email, emailSubject, request.Message, true, cancellationToken: cancellationToken);
            notification.EmailSent = emailSent;
            notification.EmailSentAt = emailSent ? DateTime.UtcNow : null;
            notification.DeliveredVia |= NotificationChannel.Email;
        }

        if (request.Channels.HasFlag(NotificationChannel.Push) && !string.IsNullOrEmpty(request.DeviceToken))
        {
            var pushSent = await SendPushNotificationAsync(request.DeviceToken, request.Title, request.Message, null, cancellationToken);
            notification.PushSent = pushSent;
            notification.PushSentAt = pushSent ? DateTime.UtcNow : null;
            notification.DeliveredVia |= NotificationChannel.Push;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created notification {NotificationId} for user {UserId}", notification.Id, request.UserId);
        return notification;
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
