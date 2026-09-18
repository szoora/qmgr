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

/// <summary>
/// How sensitive a Library document is, and therefore what its share links may do. Added 2026-09-18
/// (plan §11 decision D7, §14 finding 4).
///
/// <para><b>Why a label on the document rather than rules on each link.</b> Until this existed the
/// tenant policy was the only constraint, so a signed appraisal and a car-park notice got the same
/// link cap, the same retention and the same download default; the only signal that a document
/// concerned a named person was free text in <c>Summary</c>, which nothing read. Microsoft Purview
/// and Google Drive both put the policy on the document for the same reason — a careless sharer then
/// inherits a safe default instead of having to remember one.</para>
///
/// <para><b>Ordered, most restrictive last</b>, so "is this at least Internal?" is a comparison.
/// Raising is free; LOWERING needs <c>documents.share.manage</c> and a written reason, which is the
/// lock Google describes as keeping a label from being edited away to escape a restriction.</para>
/// </summary>
public enum DocumentClassification
{
    /// <summary>Anyone's to see — a newsletter, a foyer poster, a policy everybody already has.</summary>
    General = 0,
    /// <summary>For the school's own people. Shared outside only deliberately, and never anonymously.</summary>
    Internal = 1,
    /// <summary>About a named person, or otherwise sensitive: an appraisal, a welfare report, a duty-report pack.</summary>
    Confidential = 2
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
