using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Organization;

public class Branch : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string Timezone { get; set; } = "UTC";
    public string? OperatingHours { get; set; } // JSON: {"monday": {"open": "08:00", "close": "17:00"}}
    public string? Settings { get; set; } // JSON

    // Navigation properties
    public virtual Organization? Organization { get; set; }
    public virtual ICollection<Queue.ServiceType> ServiceTypes { get; set; } = new List<Queue.ServiceType>();
    public virtual ICollection<Queue.Counter> Counters { get; set; } = new List<Queue.Counter>();
    public virtual ICollection<Queue.Token> Tokens { get; set; } = new List<Queue.Token>();
    public virtual ICollection<Content.Display> Displays { get; set; } = new List<Content.Display>();
}
