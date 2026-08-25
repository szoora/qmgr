using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Content;

public class Display : BaseAuditableEntity
{
    public Guid BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DisplayType DisplayType { get; set; }

    // Hardware info
    public string? DeviceId { get; set; }
    public string? Resolution { get; set; } // JSON: {"width": 1920, "height": 1080}
    public string Orientation { get; set; } = "landscape";

    // Status
    public string Status { get; set; } = "offline"; // online, offline, maintenance
    public DateTime? LastHeartbeat { get; set; }

    public string? Settings { get; set; } // JSON

    // Navigation properties
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ICollection<DisplayZone> DisplayZones { get; set; } = new List<DisplayZone>();
}
