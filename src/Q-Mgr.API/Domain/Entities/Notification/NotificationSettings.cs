using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Notification;

/// <summary>
/// Stores notification channel configuration for the organization.
/// Admin can configure SMS gateway credentials, Email SMTP settings, and enable/disable channels.
/// </summary>
public class NotificationSettings : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }

    // SMS Configuration
    public bool SmsEnabled { get; set; } = false;
    public string? SmsGatewayUrl { get; set; }
    public string? SmsApiKey { get; set; }
    public string? SmsUsername { get; set; }
    public string? SmsPassword { get; set; }
    public string? SmsSenderId { get; set; }
    public string? SmsCustomerId { get; set; } // Customer ID for CRM API
    public int SmsLeadTokens { get; set; } = 3; // Notify when X tokens ahead

    // Email Configuration
    public bool EmailEnabled { get; set; } = false;
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? EmailFromAddress { get; set; }
    public string? EmailFromName { get; set; }

    // Telegram Configuration — real Bot API integration (api.telegram.org), not a stub. Sending
    // requires the recipient to have started a conversation with this bot first (a Telegram Bot
    // API protocol requirement, not a code limitation) so their numeric chat ID can be captured —
    // see Contact.TelegramChatId.
    public bool TelegramEnabled { get; set; } = false;
    public string? TelegramBotToken { get; set; }

    // WhatsApp Configuration — real WhatsApp Cloud API integration (graph.facebook.com), not a
    // stub. Meta requires a verified phone number ID and permanent access token from a WhatsApp
    // Business Platform app.
    public bool WhatsAppEnabled { get; set; } = false;
    public string? WhatsAppPhoneNumberId { get; set; }
    public string? WhatsAppAccessToken { get; set; }

    // In-App Notification Configuration
    public bool InAppEnabled { get; set; } = true;
    public bool InAppPlaySound { get; set; } = true;
    public int InAppRetentionDays { get; set; } = 30;

    // Push Notification Configuration (for future mobile app)
    public bool PushEnabled { get; set; } = false;
    public string? FirebaseProjectId { get; set; }
    public string? FirebasePrivateKey { get; set; }
    public string? FirebaseClientEmail { get; set; }

    // Notification Templates
    public string? SmsTokenCreatedTemplate { get; set; } = "Your queue number is {TokenNumber}. Estimated wait time: {WaitTime} mins. Track: {TrackingUrl}";
    public string? SmsTokenCalledTemplate { get; set; } = "Your number {TokenNumber} is now being called at {CounterName}. Please proceed.";
    public string? SmsReminderTemplate { get; set; } = "Reminder: You are {PositionInQueue} position(s) away. Please be ready.";

    public string? EmailTokenCreatedSubject { get; set; } = "Your Queue Ticket - {TokenNumber}";
    public string? EmailTokenCreatedTemplate { get; set; } = @"<h2>Thank you for visiting!</h2>
<p>Your queue number is <strong>{TokenNumber}</strong></p>
<p>Service: {ServiceType}</p>
<p>Estimated wait time: {WaitTime} minutes</p>
<p>Track your position: <a href='{TrackingUrl}'>Click here</a></p>";

    public string? EmailTokenCalledSubject { get; set; } = "Your Number is Being Called - {TokenNumber}";
    public string? EmailTokenCalledTemplate { get; set; } = @"<h2>It's Your Turn!</h2>
<p>Your number <strong>{TokenNumber}</strong> is now being called at <strong>{CounterName}</strong>.</p>
<p>Please proceed to the counter immediately.</p>";

    // Navigation
    public virtual QMgr.Domain.Entities.Organization.Organization? Organization { get; set; }
}

/// <summary>
/// Types of notifications that can be sent
/// </summary>
public enum NotificationType
{
    TokenCreated,       // When a new token is issued
    TokenCalled,        // When a token is called to counter
    TokenReminder,      // Reminder when approaching turn
    TokenTransferred,   // When token is transferred to another counter
    TokenCancelled,     // When token is cancelled
    QueueUpdate,        // General queue status update
    SystemAlert,        // System alerts (for operators/admins)
    CounterAlert,       // Counter-specific alerts
    VisitorArrived,     // A visitor has checked in for a host
    Custom              // Custom notifications
}

/// <summary>
/// Notification delivery channels
/// </summary>
[Flags]
public enum NotificationChannel
{
    None = 0,
    InApp = 1,
    Sms = 2,
    Email = 4,
    Push = 8,
    All = InApp | Sms | Email | Push
}

/// <summary>
/// Priority levels for notifications
/// </summary>
public enum NotificationPriority
{
    Low,
    Normal,
    High,
    Urgent
}
