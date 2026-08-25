namespace QMgr.Domain.Enums;

public enum BroadcastChannel
{
    Email = 0,
    Sms = 1,
    // Telegram requires the recipient to have started a chat with the bot first (a Bot API
    // protocol requirement) so their numeric chat ID can be captured — see Contact.TelegramChatId.
    Telegram = 2,
    // WhatsApp requires real WhatsApp Business Platform credentials (phone number ID + access
    // token) configured in NotificationSettings before this channel will actually send anything.
    WhatsApp = 3
}

public enum BroadcastStatus
{
    Draft = 0,
    Scheduled = 1,
    Sending = 2,
    Sent = 3,
    Cancelled = 4,
    Failed = 5
}

public enum RecipientStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    OptedOut = 3
}

public enum ContactSource
{
    Manual = 0,
    Token = 1,
    Feedback = 2,
    Visitor = 3
}
