using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Welfare;

/// <summary>
/// Evidence attached to a WelfareRecord — an apology letter, a photo of damage, a signed
/// statement. Stored via the same IMediaStorageService every other upload in this app already
/// uses (visitor photos, Docs CMS cover images); this table is just the join between a record and
/// the files it has, not a new storage mechanism.
/// </summary>
public class WelfareAttachment : BaseEntity
{
    public Guid RecordId { get; set; }

    public string FileUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public Guid UploadedByUserId { get; set; }

    public virtual WelfareRecord? Record { get; set; }
}
