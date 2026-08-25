using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Marketing;

/// <summary>
/// One row per file attached to a Broadcast — replaces the earlier single-attachment fields
/// that used to live directly on Broadcast, so a broadcast can now carry more than one file.
/// FilePath is the storage-internal path (IMediaStorageService.DownloadAsync/DeleteAsync); Url
/// is the publicly-fetchable address Telegram/WhatsApp fetch directly.
/// </summary>
public class BroadcastAttachment : BaseEntity
{
    public Guid BroadcastId { get; set; }

    public string FilePath { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public virtual Broadcast? Broadcast { get; set; }
}
