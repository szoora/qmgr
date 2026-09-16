using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// A follow-up on an otherwise-immutable <see cref="StaffPerformanceRecord"/> — how an "edit"
/// works here, exactly as <c>WelfareNote</c> does for a welfare record. A <see cref="StaffNoteKind.Response"/>
/// may only be written by the record's subject: that is the right of reply the Data Protection and
/// Privacy Act's participation principle asks for. Append-only.
/// </summary>
public class StaffPerformanceNote : BaseEntity
{
    public Guid RecordId { get; set; }

    [MaxLength(4000)]
    public string Body { get; set; } = string.Empty;

    public Guid AuthorUserId { get; set; }

    public StaffNoteKind Kind { get; set; } = StaffNoteKind.Note;

    public virtual StaffPerformanceRecord? Record { get; set; }
}
