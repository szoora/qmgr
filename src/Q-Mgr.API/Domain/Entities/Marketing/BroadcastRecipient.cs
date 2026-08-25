using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Marketing;

/// <summary>
/// One row per (Broadcast, Contact) — the actual per-recipient delivery record the send job
/// works through and the status dashboard reads from.
/// </summary>
public class BroadcastRecipient : BaseEntity
{
    public Guid BroadcastId { get; set; }
    public Guid ContactId { get; set; }

    public RecipientStatus Status { get; set; } = RecipientStatus.Pending;
    public DateTime? SentAt { get; set; }
    public string? ErrorMessage { get; set; }

    public virtual Broadcast? Broadcast { get; set; }
    public virtual Contact? Contact { get; set; }
}
