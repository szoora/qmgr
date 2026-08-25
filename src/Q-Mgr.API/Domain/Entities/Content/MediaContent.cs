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

    // Navigation properties
    public virtual Organization.Organization? Organization { get; set; }
    public virtual ICollection<PlaylistItem> PlaylistItems { get; set; } = new List<PlaylistItem>();
}
