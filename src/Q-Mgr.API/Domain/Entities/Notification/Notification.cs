using QMgr.Domain.Common;
using QMgr.Domain.Entities.Identity;

namespace QMgr.Domain.Entities.Notification;

/// <summary>
/// Stores in-app notifications for users (operators, admins, customers).
/// Used by the notification bell in the UI.
/// </summary>
public class Notification : BaseEntity
{
    public Guid? UserId { get; set; }           // Target user (null for broadcast)
    public Guid? TokenId { get; set; }          // Related token if applicable
    public Guid? BranchId { get; set; }         // Branch scope
    public Guid OrganizationId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; }
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;

    public string? IconClass { get; set; }      // Bootstrap icon class
    public string? ActionUrl { get; set; }      // URL to navigate to on click
    public string? MetaData { get; set; }       // JSON metadata for additional context

    public bool IsRead { get; set; } = false;
    public DateTime? ReadAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    // Delivery tracking
    public NotificationChannel DeliveredVia { get; set; } = NotificationChannel.InApp;
    public bool SmsSent { get; set; } = false;
    public DateTime? SmsSentAt { get; set; }
    public bool EmailSent { get; set; } = false;
    public DateTime? EmailSentAt { get; set; }
    public bool PushSent { get; set; } = false;
    public DateTime? PushSentAt { get; set; }

    // Navigation
    public virtual User? User { get; set; }
}

/// <summary>
/// Notification log for tracking sent notifications and delivery status
/// </summary>
public class NotificationLog : BaseEntity
{
    public Guid NotificationId { get; set; }
    public NotificationChannel Channel { get; set; }

    public string Recipient { get; set; } = string.Empty; // Phone/Email/DeviceToken
    public string? RequestPayload { get; set; }
    public string? ResponsePayload { get; set; }

    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; } = 0;
    public DateTime? LastRetryAt { get; set; }

    public virtual Notification? Notification { get; set; }
}
