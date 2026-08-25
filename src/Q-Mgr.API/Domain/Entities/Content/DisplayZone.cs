using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Content;

public class DisplayZone : BaseEntity
{
    public Guid DisplayId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ZoneType ZoneType { get; set; }

    // Layout (percentage-based)
    public int PositionX { get; set; } = 0;
    public int PositionY { get; set; } = 0;
    public int Width { get; set; } = 100;
    public int Height { get; set; } = 100;
    public int ZIndex { get; set; } = 0;

    // Content assignment
    public Guid? PlaylistId { get; set; }
    public string? Settings { get; set; } // JSON

    // Navigation properties
    public virtual Display? Display { get; set; }
    public virtual Playlist? Playlist { get; set; }
}
