using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Queue;

public class CounterServiceType : BaseEntity
{
    public Guid CounterId { get; set; }
    public Guid ServiceTypeId { get; set; }
    public int Priority { get; set; } = 0;

    // Navigation properties
    public virtual Counter? Counter { get; set; }
    public virtual ServiceType? ServiceType { get; set; }
}
