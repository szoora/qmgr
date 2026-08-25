using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Queue;

public class Counter : BaseAuditableEntity
{
    public Guid BranchId { get; set; }
    public string CounterNumber { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public CounterStatus Status { get; set; } = CounterStatus.Inactive;
    public Guid? CurrentTokenId { get; set; }
    public Guid? AssignedUserId { get; set; }

    // Navigation properties
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Token? CurrentToken { get; set; }
    public virtual Identity.User? AssignedUser { get; set; }
    public virtual ICollection<CounterServiceType> CounterServiceTypes { get; set; } = new List<CounterServiceType>();
    public virtual ICollection<Token> Tokens { get; set; } = new List<Token>();
}
