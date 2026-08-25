using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Content;

public class Campaign : BaseAuditableEntity
{
    public Guid BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    // Navigation properties
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ICollection<PlaylistItem> PlaylistItems { get; set; } = new List<PlaylistItem>();
}
