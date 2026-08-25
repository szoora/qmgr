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
        ILogger<NotificationService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _hubService = hubService;
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

    public async Task<bool> SendEmailAsync(Guid organizationId, string email, string subject, string body, bool isHtml = true, CancellationToken cancellationToken = default)
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

            var mailMessage = new MailMessage
            {
                From = new MailAddress(settings.EmailFromAddress, settings.EmailFromName ?? "Q-Mgr"),
                Subject = subject,
                Body = body,
                IsBodyHtml = isHtml
            };
            mailMessage.To.Add(email);

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
            var emailSent = await SendEmailAsync(request.OrganizationId, request.Email, emailSubject, request.Message, true, cancellationToken);
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
