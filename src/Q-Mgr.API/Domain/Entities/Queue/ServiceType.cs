using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Queue;

public class ServiceType : BaseAuditableEntity
{
    public Guid BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public int AverageServiceTimeMinutes { get; set; } = 10;
    public int Priority { get; set; } = 0;
    public string? IconUrl { get; set; }
    public string? Color { get; set; } // Hex color for UI

    // Navigation properties
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ICollection<CounterServiceType> CounterServiceTypes { get; set; } = new List<CounterServiceType>();
    public virtual ICollection<Token> Tokens { get; set; } = new List<Token>();
}
