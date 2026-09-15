using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Content;

/// <summary>
/// One share link against one Library document (<see cref="MediaContent"/>). A document needs
/// many concurrent links with different rules and its own lifecycle — issued, gated, revoked,
/// expired — which is why this is a table and not a few more columns on the document.
///
/// THE LINK IS NOT A SIGNED TOKEN. The badge-QR pattern (<c>VisitorBadgeTokenService</c>) looks
/// tempting and is wrong here: a self-contained token cannot be revoked, and revocation is a hard
/// requirement. The slug is 160 bits of random entropy and the database stores only its SHA-256
/// hash (API-key discipline), so a database leak does not hand over working links.
/// </summary>
public class DocumentShare : BaseAuditableEntity
{
    public Guid MediaContentId { get; set; }

    /// <summary>SHA-256 (hex) of the slug in the URL. Looked up by equality; the slug itself is never stored.</summary>
    public string SlugHash { get; set; } = string.Empty;

    /// <summary>A name for the person managing links ("Governors, Sept minutes"). Never shown to a viewer.</summary>
    public string? Label { get; set; }

    /// <summary>ASP.NET Core PasswordHasher output, or null when no passcode is set. Never stored or logged in clear.</summary>
    public string? PasscodeHash { get; set; }

    /// <summary>Availability window. Both optional; a link with neither is open until revoked.</summary>
    public DateTime? NotBefore { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Stop serving after this many successful opens. 1 makes it a one-time link.</summary>
    public int? MaxViews { get; set; }
    public int ViewCount { get; set; }

    /// <summary>
    /// When false the original file is never sent: the viewer renders pages to images and the
    /// download endpoint returns 403. Withheld SERVER-SIDE, not hidden in markup. Note what this
    /// does not do — a page on a screen can be photographed; the honest promise is attribution
    /// through the watermark, not prevention.
    /// </summary>
    public bool AllowDownload { get; set; }

    /// <summary>Require the viewer to prove an email address with a one-time code before the document is shown.</summary>
    public bool RequireEmail { get; set; }

    /// <summary>
    /// Optional JSON array of addresses that may open the link (implies RequireEmail). Kept as a
    /// column rather than a recipients table: per-person revocation is not needed, only "is this
    /// address on the list", and the verified address is written onto every event anyway.
    /// </summary>
    public string? AllowedEmailsJson { get; set; }

    public DocumentWatermarkMode Watermark { get; set; } = DocumentWatermarkMode.ViewerIdentity;

    /// <summary>The fixed text for the Custom / CustomAndViewer modes ("CONFIDENTIAL — Board only"). Ignored otherwise.</summary>
    public string? WatermarkText { get; set; }

    /// <summary>Tell the person who created the link (bell, and email if they chose it) the first time it is opened.</summary>
    public bool NotifyOnFirstOpen { get; set; } = true;

    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokeReason { get; set; }

    /// <summary>Per-share attempt lockout, on top of the IP rate limiter already in the pipeline. A four-digit passcode with unlimited attempts is not a passcode.</summary>
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }

    public DateTime? FirstOpenedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }

    public virtual MediaContent? MediaContent { get; set; }
    public virtual ICollection<DocumentShareEvent> Events { get; set; } = new List<DocumentShareEvent>();

    public bool RequiresPasscode => !string.IsNullOrEmpty(PasscodeHash);
    public bool RequiresEmailVerification => RequireEmail || !string.IsNullOrWhiteSpace(AllowedEmailsJson);
}

/// <summary>
/// The audit trail of a share link: one row per real attempt or action. Append-only.
///
/// Two tiers of content, with different retention (see <c>DocumentShareRetentionJob</c>): the
/// event itself — type, time, share, outcome, internal actor — is kept indefinitely; the
/// attribution columns (Email, IpAddress, UserAgent) are personal data and are blanked after the
/// organization's retention window, leaving the row. An access log kept for ever "just in case" is
/// a liability, not diligence.
/// </summary>
public class DocumentShareEvent : BaseEntity
{
    public Guid ShareId { get; set; }
    public DocumentShareEventType Type { get; set; }
    public bool Success { get; set; } = true;

    /// <summary>The verified viewer address, when the share required one.</summary>
    public string? Email { get; set; }

    /// <summary>Truncated (IPv4 /24, IPv6 /48): enough to spot "opened from three countries", not enough to identify a household.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Coarse — browser family and OS, not the full string.</summary>
    public string? UserAgent { get; set; }

    /// <summary>For PageViewed: which page, and how long it was on screen.</summary>
    public int? Page { get; set; }
    public int? DwellSeconds { get; set; }

    /// <summary>The staff member, for Issued / Revoked / Updated. Internal id, not personal data in the retention sense.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>A short reason or note ("expired", "download not allowed", the revoke reason).</summary>
    public string? Detail { get; set; }

    /// <summary>Groups the events of one viewing session so "which pages did this reader see" is one query.</summary>
    public Guid? SessionId { get; set; }

    public virtual DocumentShare? Share { get; set; }
}
