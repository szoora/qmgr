namespace QMgr.Domain.Enums;

// Telegram/WhatsApp are deliberately not in this enum — this codebase has no real Bot API /
// Business API integration for either (see IntegrationsSetup.razor's own IsBuilt=false flags).
// Only channels with a real, working transport (SMTP email, the SMS gateway) are offered here;
// adding fake channel options that silently no-op would be worse than not offering them.
public enum BroadcastChannel
{
    Email = 0,
    Sms = 1
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
