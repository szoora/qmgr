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
    /// attachments is optional — SMTP is the only channel here with no per-recipient size ceiling
    /// of its own beyond the mail server's, so it's the one channel that actually attaches the
    /// file bytes (via IMediaStorageService.DownloadAsync per attachment) rather than links, and
    /// the one channel where "several attachments" costs nothing extra — they all ride in the
    /// same message.
    /// </summary>
    Task<bool> SendEmailAsync(Guid organizationId, string email, string subject, string body, bool isHtml = true, IReadOnlyList<NotificationAttachment>? attachments = null, CancellationToken cancellationToken = default);
    Task<bool> SendPushNotificationAsync(string deviceToken, string title, string body, Dictionary<string, string>? data = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// chatId is the recipient's numeric Telegram chat ID (Contact.TelegramChatId), not a phone
    /// number — see NotificationSettings.TelegramBotToken doc comment for why. A single
    /// attachment rides with the text as its caption via Bot API sendPhoto (image/* mime types)
    /// or sendDocument (everything else); with more than one, the text goes out first via
    /// sendMessage and each attachment follows as its own uncaptioned sendPhoto/sendDocument call
    /// — Telegram has no single-call way to send arbitrary mixed attachments with one caption.
    /// Every attachment.Url must be publicly reachable — Telegram's servers fetch it themselves,
    /// which works in a real deployment but not from a local dev machine. Returns true if the
    /// text/first send succeeds; a later attachment failing is logged but doesn't flip the
    /// overall result, since the recipient did receive the core message.
    /// </summary>
    Task<bool> SendTelegramAsync(Guid organizationId, string chatId, string message, IReadOnlyList<NotificationAttachment>? attachments = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same one-attachment-per-message shape as Telegram above (WhatsApp Cloud API's "link"
    /// field, type: image or document) — a single attachment carries the text as its caption;
    /// more than one sends the text as its own message first, then each attachment uncaptioned.
    /// </summary>
    Task<bool> SendWhatsAppAsync(Guid organizationId, string phoneNumber, string message, IReadOnlyList<NotificationAttachment>? attachments = null, CancellationToken cancellationToken = default);

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
