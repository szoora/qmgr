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
    /// <summary>
    /// Returns a <see cref="ChannelSendResult"/>, not a bool, so a caller can tell "SMS is switched
    /// off for this tenant" from "the gateway refused it" — the two used to be indistinguishable.
    /// It converts implicitly to bool for the many call sites that only care whether it went out.
    /// </summary>
    Task<ChannelSendResult> SendSmsAsync(Guid organizationId, string phoneNumber, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// attachments is optional — SMTP is the only channel here with no per-recipient size ceiling
    /// of its own beyond the mail server's, so it's the one channel that actually attaches the
    /// file bytes (via IMediaStorageService.DownloadAsync per attachment) rather than links, and
    /// the one channel where "several attachments" costs nothing extra — they all ride in the
    /// same message.
    /// </summary>
    Task<ChannelSendResult> SendEmailAsync(Guid organizationId, string email, string subject, string body, bool isHtml = true, IReadOnlyList<NotificationAttachment>? attachments = null, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Writes ONE person's notification and pushes it to them. <see cref="CreateNotificationRequest.UserId"/>
    /// is REQUIRED and this throws without it (2026-09-23): a recipient-less row used to be read by the
    /// whole tenant and pushed to the whole platform.
    /// </summary>
    Task<Notification> CreateInAppNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same notification to several people — one row, one push and one preference decision EACH.
    /// Build the list with <c>NotificationAudience.HoldersAsync</c>, never by hand. The template's own
    /// UserId is ignored. Returns how many were written; one failure does not stop the rest.
    /// </summary>
    Task<int> NotifyManyAsync(IEnumerable<Guid> recipients, CreateNotificationRequest template, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <paramref name="userId"/>'s OWN notifications in <paramref name="organizationId"/>, and
    /// nothing else: there are no broadcast rows (2026-09-23).
    /// </summary>
    /// <param name="eventKey">Narrows to one NotificationEventKeys category (the notification centre's per-group view). Null = every kind.</param>
    /// <param name="offset">Rows to skip, for paging the full centre. 0 = the bell's behaviour.</param>
    Task<IEnumerable<Notification>> GetUserNotificationsAsync(Guid userId, Guid organizationId, bool unreadOnly = false, int limit = 50, CancellationToken cancellationToken = default, string? eventKey = null, int offset = 0);
    Task<int> GetUnreadCountAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a notification read. Only succeeds if it belongs to <paramref name="organizationId"/>
    /// AND to <paramref name="callerId"/> — false (treat as 404) otherwise, so nobody can touch
    /// anybody else's notification by guessing its ID, and there is no shared row whose read flag
    /// one person could set for everybody.
    /// </summary>
    Task<bool> MarkAsReadAsync(Guid notificationId, Guid callerId, Guid organizationId, CancellationToken cancellationToken = default);
    Task MarkAllAsReadAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Marks one event category read (the notification centre's per-group "mark all read"). Returns the caller's remaining unread count.</summary>
    Task<int> MarkAllAsReadAsync(Guid userId, Guid organizationId, string? eventKey, CancellationToken cancellationToken = default);

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
    /// <summary>
    /// The ONLY way a notification is pushed: to its one recipient's group. There is deliberately
    /// no branch-wide or platform-wide push for a notification — see NotificationAudience.
    /// </summary>
    Task SendToUserAsync(Guid userId, Notification notification);
    Task NotifyUnreadCountAsync(Guid userId, int count, DateTime countedAtUtc);

    /// <summary>
    /// Tells a signed-in user's open circuits that their role or role permissions changed, so
    /// the Web client can re-fetch its permission set instead of showing stale buttons until the
    /// next login.
    /// </summary>
    Task NotifyPermissionsChangedAsync(Guid userId);

    /// <summary>
    /// Staff Performance: a record about this person was finalised, and here is their new score for
    /// the period. Consumed by the portal's score tile; the bell is told separately, through a
    /// notification, when the person wants it.
    /// </summary>
    Task SendStaffScoreUpdatedAsync(Guid userId, QMgr.Application.DTOs.StaffScoreUpdatedEvent update);
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

    /// <summary>
    /// Which event category this is, from <c>NotificationEventKeys</c>. Supplying one makes the
    /// send PREFERENCE-AWARE: the recipient's own per-event channel choices and the organization's
    /// staff-notification defaults are applied before anything goes out.
    ///
    /// Null (the default) keeps the behaviour everything had before preferences existed — deliver
    /// on exactly the channels the caller asked for — so no existing caller changes behaviour by
    /// not knowing about this field.
    /// </summary>
    public string? EventKey { get; set; }

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
    public string? EmailSubject { get; set; }

    /// <summary>
    /// A ready-made HTML email body (2026-09-16, Staff Performance digests). When set, the EMAIL
    /// channel sends this verbatim instead of wrapping <see cref="Message"/> in the transactional
    /// layout — NotificationDispatchJob's WrapEmailBody HTML-encodes the message line by line, which
    /// is right for staff-authored free text and wrong for a report with tables in it. The bell,
    /// SMS and the stored row still carry <see cref="Message"/> as plain text, so keep that a
    /// readable one-line summary. The caller is responsible for having encoded every piece of user
    /// data inside the HTML (EmailTemplates.P / ReportTable do this).
    /// </summary>
    public string? EmailHtmlBody { get; set; }

    public DateTime? ExpiresAt { get; set; }

    /// <summary>A copy of this request addressed to <paramref name="userId"/>. Used by NotifyManyAsync.</summary>
    public CreateNotificationRequest For(Guid userId)
    {
        var copy = (CreateNotificationRequest)MemberwiseClone();
        copy.UserId = userId;
        return copy;
    }
}

/// <summary>
/// What actually happened on one channel. Replaces a bare <c>bool</c>, which conflated two
/// completely different situations — "this organization has email switched off" and "the SMTP
/// server refused the message" both returned false, so no caller could tell a configuration
/// problem from an outage and nothing was ever surfaced to an administrator.
/// </summary>
public enum ChannelSendOutcome
{
    /// <summary>It went out.</summary>
    Sent = 0,

    /// <summary>Nothing was attempted: the channel is disabled for this tenant, has no credentials, or the recipient has no address on that channel. Not a failure, and not worth alarming anyone about.</summary>
    Skipped = 1,

    /// <summary>It was attempted and did not work. This is the one worth retrying and worth telling somebody about.</summary>
    Failed = 2
}

/// <summary>
/// The outcome of one channel send, with the reason attached. <c>Reason</c> is written to
/// <c>NotificationLog.ErrorMessage</c> for a failure and shown to administrators, so keep it
/// legible to a person and free of credentials.
/// </summary>
public readonly record struct ChannelSendResult(ChannelSendOutcome Outcome, string? Reason = null)
{
    public bool IsSent => Outcome == ChannelSendOutcome.Sent;
    public bool IsFailure => Outcome == ChannelSendOutcome.Failed;

    public static ChannelSendResult Sent() => new(ChannelSendOutcome.Sent);
    public static ChannelSendResult Skipped(string reason) => new(ChannelSendOutcome.Skipped, reason);
    public static ChannelSendResult Failed(string reason) => new(ChannelSendOutcome.Failed, reason);

    /// <summary>For the many existing call sites that only care whether it went out.</summary>
    public static implicit operator bool(ChannelSendResult r) => r.IsSent;
}
