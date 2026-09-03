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

    /// <summary>
    /// The "nearly your turn" position threshold: a waiting customer is warned once their
    /// position in the queue reaches this number or lower. Despite the SMS-flavoured name (it
    /// predates queue notifications existing at all and had no consumer anywhere in the code
    /// until <c>IQueueCustomerNotifier</c>), it governs BOTH channels — the project convention
    /// is to widen an existing field rather than add a parallel one, and this field already
    /// meant exactly this.
    /// </summary>
    public int SmsLeadTokens { get; set; } = 3;

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

    // ── Queue customer notifications ────────────────────────────────────────────────────────
    // Whether the *customer holding a ticket* gets told what's happening (IQueueCustomerNotifier).
    // Defaults are ON so it works out of the box the moment a channel is configured — nothing is
    // ever sent through a channel that is itself disabled or missing credentials, and nothing is
    // ever sent to a customer who didn't supply that contact detail.

    /// <summary>Master switch for all customer-facing queue messages.</summary>
    public bool QueueNotificationsEnabled { get; set; } = true;

    /// <summary>Send queue messages by SMS (still requires <see cref="SmsEnabled"/>).</summary>
    public bool QueueNotifySms { get; set; } = true;

    /// <summary>Send queue messages by email (still requires <see cref="EmailEnabled"/>).</summary>
    public bool QueueNotifyEmail { get; set; } = true;

    /// <summary>Send the ticket-issued confirmation. Off is a common cost-saving choice.</summary>
    public bool QueueNotifyOnIssued { get; set; } = true;

    /// <summary>Send the "nearly your turn" warning at the <see cref="SmsLeadTokens"/> threshold.</summary>
    public bool QueueNotifyOnApproaching { get; set; } = true;

    /// <summary>Send the "you're being called to counter X" message.</summary>
    public bool QueueNotifyOnCalled { get; set; } = true;

    // ── Notification Templates ──────────────────────────────────────────────────────────────
    // Placeholders (case-insensitive), all substituted by QueueCustomerNotifier:
    //   {TicketNumber}  {CounterNumber}  {Position}  {BranchName}  {OrganizationName}
    //   {EstimatedWaitMinutes}  {ServiceType}  {CustomerName}
    // Legacy aliases still honoured so templates saved before queue notifications existed keep
    // working: {TokenNumber} = {TicketNumber}, {CounterName} = counter display name,
    // {PositionInQueue} = {Position}, {WaitTime} = {EstimatedWaitMinutes}. {TrackingUrl} has no
    // value (there is no public tracking page) and renders as empty.
    //
    // The three moments reuse the three template pairs that already existed rather than adding a
    // parallel set: created = ticket issued, reminder = nearly your turn, called = at the counter.
    // Email bodies are content only — one paragraph per line — and are wrapped in the standard
    // branded chrome by EmailTemplates.Layout at send time, so they must not carry their own
    // <html>/<body> markup.

    public const string DefaultSmsIssued =
        "{OrganizationName}: ticket {TicketNumber} issued at {BranchName}. You are #{Position} in line (about {EstimatedWaitMinutes} min).";
    public const string DefaultSmsApproaching =
        "{OrganizationName}: almost your turn. Ticket {TicketNumber} is #{Position} in line at {BranchName}. Please be ready.";
    public const string DefaultSmsCalled =
        "{OrganizationName}: it's your turn. Ticket {TicketNumber} - please proceed to counter {CounterNumber} at {BranchName}.";

    public const string DefaultEmailIssuedSubject = "Your ticket {TicketNumber} at {BranchName}";
    public const string DefaultEmailIssuedBody =
        "Your ticket number is <strong>{TicketNumber}</strong>.\nYou are currently number {Position} in the queue at {BranchName}.\nThe estimated wait is about {EstimatedWaitMinutes} minutes.\nWe'll message you again when your turn is close.";

    public const string DefaultEmailApproachingSubject = "Almost your turn - ticket {TicketNumber}";
    public const string DefaultEmailApproachingBody =
        "Ticket <strong>{TicketNumber}</strong> is now number {Position} in the queue at {BranchName}.\nPlease make your way to the waiting area so you don't miss your turn.";

    public const string DefaultEmailCalledSubject = "It's your turn - ticket {TicketNumber}";
    public const string DefaultEmailCalledBody =
        "Ticket <strong>{TicketNumber}</strong> is being called now at counter <strong>{CounterNumber}</strong>, {BranchName}.\nPlease proceed to the counter.";

    public string? SmsTokenCreatedTemplate { get; set; } = DefaultSmsIssued;
    public string? SmsTokenCalledTemplate { get; set; } = DefaultSmsCalled;
    public string? SmsReminderTemplate { get; set; } = DefaultSmsApproaching;

    public string? EmailTokenCreatedSubject { get; set; } = DefaultEmailIssuedSubject;
    public string? EmailTokenCreatedTemplate { get; set; } = DefaultEmailIssuedBody;

    /// <summary>
    /// "Nearly your turn" email. The SMS side of this moment already had a template
    /// (<see cref="SmsReminderTemplate"/>); the email side simply never did.
    /// </summary>
    public string? EmailReminderSubject { get; set; } = DefaultEmailApproachingSubject;
    public string? EmailReminderTemplate { get; set; } = DefaultEmailApproachingBody;

    public string? EmailTokenCalledSubject { get; set; } = DefaultEmailCalledSubject;
    public string? EmailTokenCalledTemplate { get; set; } = DefaultEmailCalledBody;

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
