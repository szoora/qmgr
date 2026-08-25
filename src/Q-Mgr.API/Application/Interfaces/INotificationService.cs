using QMgr.Domain.Entities.Notification;

namespace QMgr.Application.Interfaces;

/// <summary>
/// Base notification service for sending notifications via different channels
/// </summary>
public interface INotificationService
{
    // Channel-specific sending — organizationId selects whose SMS gateway/SMTP settings to use;
    // without it, callers processing more than one organization (e.g. billing jobs) would silently
    // send using an arbitrary organization's credentials (see NotificationService for detail).
    Task<bool> SendSmsAsync(Guid organizationId, string phoneNumber, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// attachment is optional — SMTP is the only channel here with no per-recipient size ceiling
    /// of its own beyond the mail server's, so it's the one channel that actually attaches the
    /// file bytes (via IMediaStorageService.DownloadAsync(attachment.FilePath)) rather than a link.
    /// </summary>
    Task<bool> SendEmailAsync(Guid organizationId, string email, string subject, string body, bool isHtml = true, NotificationAttachment? attachment = null, CancellationToken cancellationToken = default);
    Task<bool> SendPushNotificationAsync(string deviceToken, string title, string body, Dictionary<string, string>? data = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// chatId is the recipient's numeric Telegram chat ID (Contact.TelegramChatId), not a phone
    /// number — see NotificationSettings.TelegramBotToken doc comment for why. When attachment is
    /// set, sends via Bot API sendPhoto (image/* mime types) or sendDocument (everything else)
    /// with attachment.Url as the source — Telegram's servers fetch that URL themselves, so it
    /// must be publicly reachable (true in a real deployment; not from a local dev machine).
    /// </summary>
    Task<bool> SendTelegramAsync(Guid organizationId, string chatId, string message, NotificationAttachment? attachment = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// attachment.Url is passed as WhatsApp Cloud API's "link" field (type: image or document,
    /// picked the same way as Telegram above) — same public-reachability requirement.
    /// </summary>
    Task<bool> SendWhatsAppAsync(Guid organizationId, string phoneNumber, string message, NotificationAttachment? attachment = null, CancellationToken cancellationToken = default);

    // In-App notifications
    Task<Notification> CreateInAppNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns notifications for <paramref name="userId"/>, restricted to broadcasts and
    /// notifications belonging to <paramref name="organizationId"/> — a broadcast notification
    /// (UserId == null) from another tenant must never surface here.
    /// </summary>
    Task<IEnumerable<Notification>> GetUserNotificationsAsync(Guid userId, Guid organizationId, bool unreadOnly = false, int limit = 50, CancellationToken cancellationToken = default);
    Task<int> GetUnreadCountAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a notification read. Only succeeds if it belongs to <paramref name="organizationId"/>
    /// AND is either a broadcast (UserId == null) or belongs to <paramref name="callerId"/> —
    /// returns false (treat as 404) otherwise, so a caller can never touch another user's private
    /// notification by guessing its ID, nor another tenant's broadcast notification.
    /// </summary>
    Task<bool> MarkAsReadAsync(Guid notificationId, Guid callerId, Guid organizationId, CancellationToken cancellationToken = default);
    Task MarkAllAsReadAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a notification. Same ownership rule as <see cref="MarkAsReadAsync"/>.
    /// </summary>
    Task<bool> DeleteNotificationAsync(Guid notificationId, Guid callerId, Guid organizationId, CancellationToken cancellationToken = default);
    Task CleanupOldNotificationsAsync(int retentionDays, CancellationToken cancellationToken = default);
}

/// <summary>
/// Token-specific notification service for queue management
/// </summary>
public interface ITokenNotificationService
{
    Task SendTokenCreatedNotificationAsync(Guid tokenId, CancellationToken cancellationToken = default);
    Task SendTokenCalledNotificationAsync(Guid tokenId, CancellationToken cancellationToken = default);
    Task SendTokenReminderNotificationAsync(Guid tokenId, int positionInQueue, CancellationToken cancellationToken = default);
    Task SendTokenTransferredNotificationAsync(Guid tokenId, string newCounterName, CancellationToken cancellationToken = default);
    Task SendTokenCancelledNotificationAsync(Guid tokenId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Notification settings management service
/// </summary>
public interface INotificationSettingsService
{
    Task<NotificationSettings?> GetSettingsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<NotificationSettings> CreateOrUpdateSettingsAsync(NotificationSettings settings, CancellationToken cancellationToken = default);
    Task<bool> TestSmsConnectionAsync(Guid organizationId, string testPhoneNumber, CancellationToken cancellationToken = default);
    Task<bool> TestEmailConnectionAsync(Guid organizationId, string testEmailAddress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real-time notification hub interface
/// </summary>
public interface INotificationHubService
{
    Task SendToUserAsync(Guid userId, Notification notification);
    Task SendToBranchAsync(Guid branchId, Notification notification);
    Task SendToAllAsync(Notification notification);
    Task NotifyUnreadCountAsync(Guid userId, int count);
}

/// <summary>
/// A file attached to an outbound notification (currently only broadcasts populate this).
/// FilePath is the storage-internal path IMediaStorageService uses to read the bytes back
/// (DownloadAsync) — needed for Email, which attaches actual bytes rather than a link. Url is
/// the publicly-fetchable address used by Telegram/WhatsApp, which fetch it themselves rather
/// than receiving bytes directly.
/// </summary>
public record NotificationAttachment(string FilePath, string Url, string FileName, string MimeType);

/// <summary>
/// Request model for creating notifications
/// </summary>
public class CreateNotificationRequest
{
    public Guid? UserId { get; set; }
    public Guid? TokenId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid OrganizationId { get; set; }

    public required string Title { get; set; }
    public required string Message { get; set; }
    public NotificationType Type { get; set; }
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;

    public string? IconClass { get; set; }
    public string? ActionUrl { get; set; }
    public Dictionary<string, object>? MetaData { get; set; }

    public NotificationChannel Channels { get; set; } = NotificationChannel.InApp;
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string? DeviceToken { get; set; }
    public string? EmailSubject { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
