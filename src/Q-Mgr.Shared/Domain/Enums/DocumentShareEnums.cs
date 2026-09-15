namespace QMgr.Domain.Enums;

/// <summary>
/// One row per real thing that happened to a share link. Append-only. Follows NotificationLog's
/// rule: a non-event (a gate that was not configured) writes nothing — logging non-events as
/// failures is how a log stops being read.
/// </summary>
public enum DocumentShareEventType
{
    /// <summary>The link was created.</summary>
    Issued = 0,
    /// <summary>A viewer cleared every gate and the document was shown.</summary>
    Opened = 1,
    /// <summary>A wrong passcode.</summary>
    PasscodeFailed = 2,
    /// <summary>A one-time verification code was emailed.</summary>
    EmailCodeSent = 3,
    /// <summary>The emailed code was entered correctly; the viewer is now attributed.</summary>
    EmailVerified = 4,
    /// <summary>A wrong or expired emailed code, or an address not on the allow-list.</summary>
    EmailRejected = 5,
    /// <summary>A page was read; DwellSeconds says for how long.</summary>
    PageViewed = 6,
    /// <summary>The original file was downloaded (only possible when the share allows it).</summary>
    Downloaded = 7,
    /// <summary>Refused: expired, not yet open, revoked, view limit reached, or download not allowed.</summary>
    Denied = 8,
    /// <summary>The link was revoked by a person.</summary>
    Revoked = 9,
    /// <summary>Too many failed attempts; the link is locked for a while.</summary>
    Locked = 10,
    /// <summary>The share's rules were edited (expiry, download, label…).</summary>
    Updated = 11
}

/// <summary>
/// What a share link's state is right now, evaluated per request — never cached, so "revoke"
/// means now.
/// </summary>
public enum DocumentShareState
{
    Active = 0,
    NotYetOpen = 1,
    Expired = 2,
    Revoked = 3,
    ViewLimitReached = 4,
    Locked = 5,
    /// <summary>The document behind it is gone, deactivated, or no longer shareable. Fails closed.</summary>
    Unavailable = 6
}

public enum DocumentWatermarkMode
{
    None = 0,
    /// <summary>The viewer's verified email (or "Anonymous viewer") and the timestamp, drawn across every page.</summary>
    ViewerIdentity = 1,
    /// <summary>Fixed text chosen when the link is created ("CONFIDENTIAL — Board only"), the same for every viewer.</summary>
    Custom = 2,
    /// <summary>The custom text followed by the viewer's identity and the timestamp.</summary>
    CustomAndViewer = 3
}
