using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// ---------------------------------------------------------------------------------------------
// Secure document sharing — the DTOs that cross the API/Web boundary. ONE copy, here, per the
// SSoT rule in CLAUDE.md. See docs/plans/SECURE_DOCUMENT_SHARING.md.
// ---------------------------------------------------------------------------------------------

/// <summary>A share link as the person managing it sees it. Never carries the slug: that is shown exactly once, on creation.</summary>
public record DocumentShareDto
{
    public Guid Id { get; init; }
    public Guid MediaContentId { get; init; }
    public string? Label { get; init; }
    public bool RequiresPasscode { get; init; }
    public bool RequireEmail { get; init; }
    public List<string> AllowedEmails { get; init; } = new();
    public DateTime? NotBefore { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public int? MaxViews { get; init; }
    public int ViewCount { get; init; }
    public bool AllowDownload { get; init; }
    public DocumentWatermarkMode Watermark { get; init; }
    public string? WatermarkText { get; init; }
    public bool NotifyOnFirstOpen { get; init; }
    public DocumentShareState State { get; init; }
    public DateTime? RevokedAt { get; init; }
    public string? RevokeReason { get; init; }
    public string? RevokedByName { get; init; }
    public DateTime? FirstOpenedAt { get; init; }
    public DateTime? LastOpenedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedByName { get; init; }
    public int UniqueViewers { get; init; }
    public int Downloads { get; init; }
    public int Denials { get; init; }
}

public record CreateDocumentShareRequest
{
    public string? Label { get; init; }
    /// <summary>Plain passcode, hashed on arrival and never stored or logged in clear. Null for none.</summary>
    public string? Passcode { get; init; }
    public bool RequireEmail { get; init; }
    public List<string>? AllowedEmails { get; init; }
    public DateTime? NotBefore { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public int? MaxViews { get; init; }
    public bool AllowDownload { get; init; }
    public DocumentWatermarkMode Watermark { get; init; } = DocumentWatermarkMode.ViewerIdentity;
    /// <summary>For the Custom / CustomAndViewer modes: the fixed text drawn across every page. Up to 200 characters.</summary>
    public string? WatermarkText { get; init; }
    public bool NotifyOnFirstOpen { get; init; } = true;

    /// <summary>Addresses to email the link to, through the tenant's (or the platform's) mailbox. The passcode is NEVER included in that email.</summary>
    public List<string>? SendTo { get; init; }
    /// <summary>The public origin of the Web app, so the emailed link is the one a browser can open — never the API's own address.</summary>
    public string? LinkBaseUrl { get; init; }
    public string? Message { get; init; }
}

public record UpdateDocumentShareRequest
{
    public string? Label { get; init; }
    public DateTime? NotBefore { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public int? MaxViews { get; init; }
    public bool AllowDownload { get; init; }
    public bool RequireEmail { get; init; }
    public List<string>? AllowedEmails { get; init; }
    public DocumentWatermarkMode Watermark { get; init; } = DocumentWatermarkMode.ViewerIdentity;
    public string? WatermarkText { get; init; }
    public bool NotifyOnFirstOpen { get; init; } = true;
    /// <summary>Set a new passcode. Null leaves the current one alone.</summary>
    public string? NewPasscode { get; init; }
    public bool ClearPasscode { get; init; }
}

public record RevokeDocumentShareRequest
{
    public string? Reason { get; init; }
}

/// <summary>Returned once, on creation: the only time the slug exists outside the viewer's address bar.</summary>
public record DocumentShareIssuedDto
{
    public DocumentShareDto Share { get; init; } = new();
    public string Slug { get; init; } = string.Empty;
    /// <summary>The full link, when the request carried a LinkBaseUrl.</summary>
    public string? Url { get; init; }
    public int EmailsSent { get; init; }
    public List<string> EmailFailures { get; init; } = new();
}

public record DocumentShareEventDto
{
    public Guid Id { get; init; }
    public Guid ShareId { get; init; }
    public string? ShareLabel { get; init; }
    public DocumentShareEventType Type { get; init; }
    public bool Success { get; init; }
    public string? Email { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public int? Page { get; init; }
    public int? DwellSeconds { get; init; }
    public string? ActorName { get; init; }
    public string? Detail { get; init; }
    public Guid? SessionId { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record DocumentPageDwellDto
{
    public int Page { get; init; }
    public int Views { get; init; }
    public int TotalDwellSeconds { get; init; }
}

/// <summary>Everything the activity view shows for one document.</summary>
public record DocumentActivityDto
{
    public Guid MediaContentId { get; init; }
    public string DocumentName { get; init; } = string.Empty;
    public int Opens { get; init; }
    public int UniqueViewers { get; init; }
    public int AnonymousOpens { get; init; }
    public int Downloads { get; init; }
    public int Denials { get; init; }
    public int LinksActive { get; init; }
    public int LinksTotal { get; init; }
    public DateTime? LastOpenedAt { get; init; }
    public List<DocumentPageDwellDto> Pages { get; init; } = new();
    public List<DocumentShareEventDto> RecentEvents { get; init; } = new();
    /// <summary>How long attribution (email, address, browser) is kept before it is blanked — stated on the page so the log reads as what it is.</summary>
    public int AttributionRetentionDays { get; init; }
}

/// <summary>Tenant-wide sharing policy, stored in Organization.Settings under "DocumentSharing".</summary>
public record DocumentSharingPolicyDto
{
    /// <summary>An organization-enforced cap on how far ahead any link may expire. Belongs to the tenant, not to the person creating the link.</summary>
    public int MaxLinkDays { get; init; } = 365;
    /// <summary>Days before a viewer's email, address and browser are blanked from the audit trail. 180 = the CNIL guidance for active logs holding personal data.</summary>
    public int AttributionRetentionDays { get; init; } = 180;
    public bool RequireEmailByDefault { get; init; }
    public bool AllowDownloadByDefault { get; init; }
    public bool NotifyOnFirstOpenByDefault { get; init; } = true;
}

/// <summary>Publishing decisions on a Library document. Gated on library.publish.</summary>
public record UpdateMediaPublishingRequest
{
    public bool IsShareable { get; init; }
    public string? Summary { get; init; }
    public string? PublishedFrom { get; init; }
}

// ---- The public viewer --------------------------------------------------------------------------

/// <summary>What an anonymous visitor to /s/{slug} learns before clearing any gate: enough to know what they are opening, never the content.</summary>
public record SharedDocumentGateDto
{
    public string DocumentName { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string? OrganizationName { get; init; }
    public string? PublishedFrom { get; init; }
    public DateTime? PublishedAt { get; init; }
    public DocumentShareState State { get; init; }
    public bool RequiresPasscode { get; init; }
    public bool RequiresEmail { get; init; }
    public bool AllowDownload { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? NotBefore { get; init; }
    public string? Message { get; init; }
}

public record OpenSharedDocumentRequest
{
    public string? Passcode { get; init; }
    public string? Email { get; init; }
    /// <summary>The one-time code that was emailed, with the challenge the server handed back when it sent it.</summary>
    public string? Code { get; init; }
    public string? Challenge { get; init; }
}

public static class SharedDocumentOpenStatus
{
    public const string Granted = "Granted";
    public const string PasscodeRequired = "PasscodeRequired";
    public const string PasscodeInvalid = "PasscodeInvalid";
    public const string EmailRequired = "EmailRequired";
    public const string CodeSent = "CodeSent";
    public const string CodeInvalid = "CodeInvalid";
    public const string EmailNotAllowed = "EmailNotAllowed";
    public const string Denied = "Denied";
    public const string Locked = "Locked";
}

public record OpenSharedDocumentResponse
{
    public string Status { get; init; } = SharedDocumentOpenStatus.Denied;
    public string? Message { get; init; }
    /// <summary>Returned with CodeSent; the viewer sends it back with the code.</summary>
    public string? Challenge { get; init; }
    /// <summary>Returned with Granted. Identifies the viewing session for page events and for resuming after a reload.</summary>
    public string? SessionToken { get; init; }
    /// <summary>Returned with Granted. Sixty seconds; the viewer fetches the bytes with it immediately.</summary>
    public string? ContentToken { get; init; }
    public int ContentTokenSeconds { get; init; }
    public bool AllowDownload { get; init; }
    public string? WatermarkText { get; init; }
    public string? ViewerEmail { get; init; }
    public string? DocumentName { get; init; }
    public string? MimeType { get; init; }
}

public record ResumeSharedDocumentRequest
{
    public string SessionToken { get; init; } = string.Empty;
}

public record SharedDocumentEventRequest
{
    public string SessionToken { get; init; } = string.Empty;
    public int Page { get; init; }
    public int DwellSeconds { get; init; }
}
