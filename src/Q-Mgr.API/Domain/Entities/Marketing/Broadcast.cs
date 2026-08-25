using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Marketing;

public class Broadcast : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }

    public string Name { get; set; } = string.Empty;
    public BroadcastChannel Channel { get; set; }
    public string? Subject { get; set; } // Email only
    public string MessageBody { get; set; } = string.Empty;

    // Comma-separated tag filter — empty/null means "all contacts not opted out".
    public string? AudienceTagFilter { get; set; }

    public BroadcastStatus Status { get; set; } = BroadcastStatus.Draft;
    public DateTime? ScheduledAt { get; set; }
    public DateTime? SendStartedAt { get; set; }
    public DateTime? SendCompletedAt { get; set; }

    public int TotalRecipients { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }

    public Guid CreatedByUserId { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ICollection<BroadcastRecipient> Recipients { get; set; } = new List<BroadcastRecipient>();
}
