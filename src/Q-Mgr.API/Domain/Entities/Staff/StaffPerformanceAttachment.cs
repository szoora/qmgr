using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// Evidence on a staff record — a signed register page, an observation sheet, a certificate. Exactly
/// <c>WelfareAttachment</c>'s columns against a different record. A separate table on purpose: a
/// second nullable FK on WelfareAttachment would make the one table that gates photographs of
/// injured children polymorphic, with a discriminator in the security path. UploadAuthorizer
/// classifies by "which table points at this file" (UploadOwnerKind.StaffEvidence); keep that true.
/// </summary>
public class StaffPerformanceAttachment : BaseEntity
{
    public Guid RecordId { get; set; }

    public string FileUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public Guid UploadedByUserId { get; set; }

    public virtual StaffPerformanceRecord? Record { get; set; }
}
