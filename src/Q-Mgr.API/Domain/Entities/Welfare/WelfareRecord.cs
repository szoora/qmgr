using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Welfare;

/// <summary>
/// One achievement, behavior incident, or welfare concern logged against a Student. Branch-scoped
/// like Student itself (no global EF query filter — see QMgrDbContext's TenantIsolationEnabled
/// list, which deliberately excludes operational/branch-scoped entities such as this one), so
/// every controller action reaching one by ID must call VerifyBranchOwnership explicitly, exactly
/// like StudentsController and VisitorsController already do.
///
/// Deliberately append-only: an edit adds a WelfareNote rather than mutating Description/Tier/
/// etc. after creation — the same reasoning CPOMS uses for its safeguarding chronology, and the
/// cheapest way to satisfy FERPA's "never destroy a record under review" rule (nothing here is
/// ever overwritten in place, so there's nothing to accidentally destroy).
/// </summary>
public class WelfareRecord : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid StudentId { get; set; }
    public Guid CategoryId { get; set; }

    public WelfareCaseType CaseType { get; set; }
    public WelfareTier Tier { get; set; } = WelfareTier.Low;

    /// <summary>Signed merit/demerit points. Sign must match CaseType — enforced in WelfareController, not here (entities stay dumb).</summary>
    public int? Points { get; set; }

    public string Description { get; set; } = string.Empty;
    public string? Location { get; set; }

    /// <summary>When it actually happened — distinct from CreatedAt (when it was logged), which can be later the same day.</summary>
    public DateTime OccurredAt { get; set; }

    public WelfareStatus Status { get; set; } = WelfareStatus.Resolved;

    /// <summary>Free-text description of the intervention/consequence (detention, parental meeting, restorative conversation, referral, internal exclusion, ...). Deliberately not a foreign key into an admin-managed table like Category — intervention types are far more standardized across schools than achievement/behavior categories are, so a small in-code suggestion list (client-side autocomplete) covers it without a new table.</summary>
    public string? ActionTaken { get; set; }

    /// <summary>Staff member responsible for following up while Status is still open. Drives the "my open actions" view and overdue reminders — never implies escalation of visibility by itself, that's still governed by Tier + the existing permission table.</summary>
    public Guid? AssignedToUserId { get; set; }

    public DateTime? ActionDueDate { get; set; }

    /// <summary>
    /// Where this response sits on the graduated ladder. Alongside <c>ActionTaken</c>, never
    /// instead of it: the free text keeps the detail a school wants to read back, this makes it
    /// something a report can group by — which is the only way to answer "are we escalating to
    /// punishment too early, and on whom".
    /// </summary>
    public WelfareResponseStage? ResponseStage { get; set; }

    /// <summary>
    /// What immediately preceded it. With the existing <c>Location</c> and <c>OccurredAt</c> this
    /// is enough to surface "six of nine incidents are in the dining hall after games" from a
    /// plain GROUP BY — no model, no inference.
    /// </summary>
    [MaxLength(500)]
    public string? Antecedent { get; set; }

    /// <summary>Staff-perceived, never authoritative and never displayed as a diagnosis.</summary>
    public WelfarePerceivedFunction? PerceivedFunction { get; set; }

    /// <summary>
    /// When the overdue-action reminder job last notified AssignedToUserId that this record is
    /// open, assigned, and past ActionDueDate (see WelfareReminderJob). Null means never reminded.
    /// Gates re-notification to at most once every 24h per record, rather than a boolean "already
    /// warned" flag, so a follow-up that's still ignored the next day nags again instead of going
    /// silent forever after the first notice.
    /// </summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>
    /// Other students this same incident also applies to, beyond the canonical StudentId above
    /// (e.g. a fight involving several students) — a native Postgres array column rather than a
    /// join table, since the only need is "also show this on these other students' timelines,"
    /// not per-student metadata. StudentId remains "who this was primarily filed against"; the
    /// chronology query adds these via ANY(). Revisit as a real join table only if a school later
    /// needs distinct per-student roles (victim/witness/co-participant) — nobody has asked for
    /// that yet.
    /// </summary>
    public Guid[]? AdditionalStudentIds { get; set; }

    /// <summary>
    /// How widely this record may be seen. Replaced the old <c>Confidential</c> bool on
    /// 2026-09-09; the migration mapped <c>true → Confidential</c>, <c>false → Standard</c>.
    ///
    /// SECURITY, unchanged from the bool it replaces: for <c>CaseType == Welfare</c> this is forced
    /// to at least <see cref="WelfareVisibility.Confidential"/> server-side regardless of what the
    /// client sends — see WelfareController.CreateRecord. A safeguarding concern and a tardy slip
    /// do not belong to the same audience (the CPOMS/MyConcern lesson the whole confidentiality
    /// design is built around).
    ///
    /// <see cref="WelfareVisibility.Restricted"/> is never forced: something reaches that rung only
    /// because a holder of <c>welfare.restricted.view</c> put it there. It CAN be set on a Welfare
    /// case — raising a safeguarding record to administrator-only is a legitimate act — but never
    /// lowered below Confidential for one.
    ///
    /// Note this is the ONE mutable field on an otherwise append-only record, and deliberately so:
    /// a record whose sensitivity is discovered later must be raisable in place. Every change goes
    /// through WelfareController.UpdateVisibility, which writes a WelfareNote saying who changed it
    /// and why — so the chronology still records it even though the field itself moved.
    /// </summary>
    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;

    public Guid ReportedByUserId { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Student? Student { get; set; }
    public virtual WelfareCategory? Category { get; set; }
    public virtual ICollection<WelfareAttachment> Attachments { get; set; } = new List<WelfareAttachment>();
    public virtual ICollection<WelfareNote> Notes { get; set; } = new List<WelfareNote>();
    public virtual ICollection<WelfareNotification> Notifications { get; set; } = new List<WelfareNotification>();
}
