using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// One thing logged about a member of staff: attended a meeting, supervised an exam, was observed
/// teaching, ran the debate club, was recognised by a colleague, a conduct matter, a welfare-of-
/// staff note. The staff analogue of <c>WelfareRecord</c>, and the same rules:
///
///  - APPEND-ONLY. An "edit" is a <see cref="StaffPerformanceNote"/>. The subject's right of reply
///    is a note of kind Response. A void is <see cref="StaffRecordStatus.Annulled"/> with a note
///    saying why; the row stays and drops out of scoring.
///  - <see cref="Visibility"/> is the one mutable field, changed only through an endpoint that
///    writes a note recording who, from what, to what, and why. The type is
///    <see cref="WelfareVisibility"/> reused — the name is historical, the semantics are not.
///  - Points carry a sign enforced against the parameter's kind in the controller, never here.
///  - Wellbeing records carry no points and are excluded from scoring by construction.
///
/// Not a WelfareRecord: that table's StudentId is non-nullable and its guards, scope and alerting
/// are all about a child. Same shape, separate subject. Branch-scoped with no global EF query
/// filter; every by-ID action calls VerifyBranchOwnership and, for the subject, the staff scope.
/// </summary>
public class StaffPerformanceRecord : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>The member of staff the record is about.</summary>
    public Guid SubjectUserId { get; set; }

    public Guid ParameterId { get; set; }

    /// <summary>Set when the record was written by a register.</summary>
    public Guid? DutyId { get; set; }

    public DutyOutcome Outcome { get; set; } = DutyOutcome.NotApplicable;

    /// <summary>Signed points; magnitude capped by the parameter's MaxPointsPerEntry. Null for Wellbeing.</summary>
    public int? Points { get; set; }

    /// <summary>1..Parameter.RatingScale, observations only.</summary>
    public int? Rating { get; set; }

    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>When it happened — distinct from CreatedAt, when it was logged.</summary>
    public DateTime OccurredAt { get; set; }

    public RecordSource Source { get; set; } = RecordSource.Manual;

    public StaffRecordStatus Status { get; set; } = StaffRecordStatus.Final;

    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;

    public Guid LoggedByUserId { get; set; }

    /// <summary>When the subject marked it as seen. The subject-access trail's first entry.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Identity.User? Subject { get; set; }
    public virtual PerformanceParameter? Parameter { get; set; }
    public virtual StaffDuty? Duty { get; set; }
    public virtual ICollection<StaffPerformanceNote> Notes { get; set; } = new List<StaffPerformanceNote>();
    public virtual ICollection<StaffPerformanceAttachment> Attachments { get; set; } = new List<StaffPerformanceAttachment>();
}
