using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Queue;

public class TokenHistory : BaseEntity
{
    public Guid TokenId { get; set; }
    public TokenStatus? FromStatus { get; set; }
    public TokenStatus ToStatus { get; set; }
    public Guid? CounterId { get; set; }
    public Guid? UserId { get; set; }
    public string? Notes { get; set; }

    // Navigation properties
    public virtual Token? Token { get; set; }
    public virtual Counter? Counter { get; set; }
    public virtual Identity.User? User { get; set; }
}
