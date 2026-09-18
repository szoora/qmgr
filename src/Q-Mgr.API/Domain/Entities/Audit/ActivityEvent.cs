using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Audit;

/// <summary>
/// Who did what, to which record, from where. The first general activity log in this codebase;
/// <c>DocumentShareEvent</c> is the template, generalised with an entity type and an actor.
///
/// Written by EXPLICIT calls to IActivityLogger, not by an EF interceptor: an interceptor captures
/// before/after state indiscriminately, which for this subject means copying confidential text
/// into a second table by default. <see cref="Summary"/> is written at the actor's visibility, so a
/// Restricted record's creation reads "Restricted record created for J. Okello" to a reader without
/// the rung, never with its content. <see cref="DetailJson"/> holds changed fields, never a full dump.
///
/// Two tiers, as DocumentShareEvent: the event is kept indefinitely; the attribution columns
/// (IpAddress, UserAgent) are personal data and are blanked after the tenant's retention window by
/// StaffPerformanceJobs, leaving the row. Append-only.
/// </summary>
public class ActivityEvent : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }

    /// <summary>Who did it. Null for a system job.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Whom it was about, when the entity is about a person (a staff record's subject).</summary>
    public Guid? SubjectUserId { get; set; }

    /// <summary>
    /// Whom it was about when the subject is a STUDENT rather than a member of staff — a welfare
    /// report published to the Library, a welfare export. Nullable and additive (2026-09-18): this
    /// table was built for the Staff Performance module, where every subject is a user, and welfare
    /// exports were consequently logged nowhere at all.
    ///
    /// <para>Exactly one of <see cref="SubjectUserId"/> and this is set on a subject-bearing event.
    /// A welfare reader is gated on <c>welfare.reports.view</c> AND <c>IStudentScopeService</c>, so a
    /// class teacher sees only events about students in their own classes — the staff log's own
    /// <c>staff.records.view</c> + staff scope would be the wrong gate entirely.</para>
    /// </summary>
    public Guid? SubjectStudentId { get; set; }

    /// <summary>A constant from ActivityActions. Wire format; never renamed.</summary>
    [MaxLength(80)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(60)]
    public string EntityType { get; set; } = string.Empty;

    public Guid? EntityId { get; set; }

    [MaxLength(500)]
    public string Summary { get; set; } = string.Empty;

    public string? DetailJson { get; set; }

    /// <summary>
    /// The rung of the thing the event is about (a staff record's visibility at the time, the higher
    /// of the two on a visibility change; Confidential for anything about an appraisal). NOT a gate on
    /// the administrator's log — a reader without the rung still sees the redacted Summary there, as
    /// the plan requires — but it IS the gate on the subject's own trail: a person must never learn
    /// from "Restricted record viewed" that a Restricted record about them exists (found 2026-09-17).
    /// </summary>
    public QMgr.Domain.Enums.WelfareVisibility Visibility { get; set; } = QMgr.Domain.Enums.WelfareVisibility.Standard;

    /// <summary>Truncated (IPv4 /24, IPv6 /48). Blanked after retention.</summary>
    [MaxLength(64)]
    public string? IpAddress { get; set; }

    /// <summary>Coarse — browser family and OS. Blanked after retention.</summary>
    [MaxLength(120)]
    public string? UserAgent { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
