using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Content;

public class PlaylistItem : BaseEntity
{
    public Guid PlaylistId { get; set; }
    public Guid MediaContentId { get; set; }
    public int Position { get; set; }
    public int? DurationSeconds { get; set; } // Override default
    public string? Conditions { get; set; } // JSON: e.g., {"queue_length_gt": 10}
    public Guid? CampaignId { get; set; }

    // Navigation properties
    public virtual Playlist? Playlist { get; set; }
    public virtual MediaContent? MediaContent { get; set; }
    public virtual Campaign? Campaign { get; set; }
}
