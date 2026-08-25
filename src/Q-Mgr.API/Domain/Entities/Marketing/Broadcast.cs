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

    // Optional file attachment, uploaded separately via POST .../broadcasts/{id}/attachment
    // (see BroadcastsController) and stored through the same IMediaStorageService Digital
    // Signage media uploads already use. AttachmentFilePath is the storage-internal path
    // (IMediaStorageService.DownloadAsync/DeleteAsync); AttachmentUrl is the publicly-fetchable
    // address Telegram/WhatsApp fetch directly. SMS has no attachment concept — BroadcastSendJob
    // appends AttachmentUrl as plain text to the SMS body instead when one is present.
    public string? AttachmentFilePath { get; set; }
    public string? AttachmentUrl { get; set; }
    public string? AttachmentFileName { get; set; }
    public string? AttachmentMimeType { get; set; }
    public long? AttachmentFileSizeBytes { get; set; }

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
