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
    Task<bool> SendEmailAsync(Guid organizationId, string email, string subject, string body, bool isHtml = true, CancellationToken cancellationToken = default);
    Task<bool> SendPushNotificationAsync(string deviceToken, string title, string body, Dictionary<string, string>? data = null, CancellationToken cancellationToken = default);

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
