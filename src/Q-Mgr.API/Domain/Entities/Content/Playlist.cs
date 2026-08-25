using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Content;

public class Playlist : BaseAuditableEntity
{
    public Guid BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Scheduling
    public string ScheduleType { get; set; } = "always"; // always, scheduled, conditional
    public string? Schedule { get; set; } // JSON scheduling rules

    // Settings
    public string TransitionType { get; set; } = "fade";
    public int DefaultDurationSeconds { get; set; } = 10;
    public bool Loop { get; set; } = true;
    public bool Shuffle { get; set; } = false;

    // Background music — a Spotify playlist ID sourced from the platform-wide
    // Spotify connection (see PlatformSpotifyConnection); tenants pick from
    // that account's playlists, they don't connect their own account.
    public string? SpotifyPlaylistId { get; set; }
    public string? SpotifyPlaylistName { get; set; }

    // Navigation properties
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ICollection<PlaylistItem> Items { get; set; } = new List<PlaylistItem>();
    public virtual ICollection<DisplayZone> DisplayZones { get; set; } = new List<DisplayZone>();
}
