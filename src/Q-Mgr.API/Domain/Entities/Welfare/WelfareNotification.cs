using QMgr.Domain.Common;
using QMgr.Domain.Entities.Notification;

namespace QMgr.Domain.Entities.Welfare;

/// <summary>
/// One row per guardian per send — never implicit, never automatic. A staff member reviews the
/// message and explicitly triggers this (WelfareController.NotifyGuardian); nothing here fires on
/// its own the way, say, a queue-position reminder does. Kept even when the underlying send fails
/// (Success = false) so "was this guardian actually told" is always answerable from the record's
/// own history, not just trusted from a toast that appeared once in someone's browser.
/// </summary>
public class WelfareNotification : BaseEntity
{
    public Guid RecordId { get; set; }
    public Guid GuardianVisitorProfileId { get; set; }

    public NotificationChannel Channel { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool Success { get; set; }

    public Guid SentByUserId { get; set; }

    public virtual WelfareRecord? Record { get; set; }
}
