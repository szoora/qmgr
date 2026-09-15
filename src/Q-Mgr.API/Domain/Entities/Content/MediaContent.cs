using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Content;

public class MediaContent : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ContentType ContentType { get; set; }
    public string? MimeType { get; set; }

    // Storage
    public StorageType StorageType { get; set; } = StorageType.Local;
    public string? FilePath { get; set; }
    public string? FileUrl { get; set; }
    public string? ThumbnailUrl { get; set; }

    // Metadata
    public long? FileSizeBytes { get; set; }
    public int? DurationSeconds { get; set; } // For video/audio
    public string? Dimensions { get; set; } // JSON: {"width": 1920, "height": 1080}

    // Content for text/scrolling messages
    public string? TextContent { get; set; }

    public string[]? Tags { get; set; }

    // ---- Document Library (2026-09-15) -------------------------------------------------------
    // The Library IS this table, enhanced rather than duplicated: an organisation-scoped row that
    // already carries ContentType.Pdf, a name, a description and the PlaylistItems navigation —
    // which is the signage path. "Flag it for signage" is playlist membership and needs nothing
    // new; "flag it for restricted share" is IsShareable plus one or more DocumentShare rows.
    //
    // Serving rule (UploadsController): a file is public unless it is shareable AND on no
    // playlist. Anything on a wall was put there by choice; a share-only document streams through
    // the share gate alone, and its raw path returns 401.

    /// <summary>May DocumentShare links be issued against this document?</summary>
    public bool IsShareable { get; set; }

    /// <summary>Short public description shown on the share page. Never the document's body.</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// What this document was generated from, when it is a snapshot of something live in Q-Mgr
    /// (a welfare report for one student over a period, say). Shown on the share page because a
    /// published document stops tracking its source: a report exported in March and shared in
    /// September shows March's figures, and the reader has to be told so.
    /// </summary>
    public string? PublishedFrom { get; set; }
    public DateTime? PublishedAt { get; set; }

    /// <summary>The publish audit: who put this into the Library. With PublishedAt and PublishedFrom, this row is how a document that turns up where it should not is traced.</summary>
    public Guid? PublishedByUserId { get; set; }

    // Navigation properties
    public virtual Organization.Organization? Organization { get; set; }
    public virtual ICollection<PlaylistItem> PlaylistItems { get; set; } = new List<PlaylistItem>();
    public virtual ICollection<DocumentShare> Shares { get; set; } = new List<DocumentShare>();
}
