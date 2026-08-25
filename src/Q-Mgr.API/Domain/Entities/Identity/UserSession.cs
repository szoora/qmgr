using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Identity;

public class UserSession : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? CounterId { get; set; }

    public DateTime LoginTime { get; set; } = DateTime.UtcNow;
    public DateTime? LogoutTime { get; set; }

    public int TokensServed { get; set; } = 0;
    public int? AverageServiceTimeSeconds { get; set; }

    // Navigation properties
    public virtual User? User { get; set; }
    public virtual Queue.Counter? Counter { get; set; }
}
