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
    /// SECURITY: for CaseType == Welfare this is always forced true server-side regardless of
    /// what the client sends — see WelfareController.CreateRecord. A safeguarding concern and a
    /// tardy slip do not belong to the same audience (the CPOMS/MyConcern lesson the whole
    /// confidentiality-tier design is built around).
    /// </summary>
    public bool Confidential { get; set; }

    public Guid ReportedByUserId { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Visitor.Student? Student { get; set; }
    public virtual WelfareCategory? Category { get; set; }
    public virtual ICollection<WelfareAttachment> Attachments { get; set; } = new List<WelfareAttachment>();
    public virtual ICollection<WelfareNote> Notes { get; set; } = new List<WelfareNote>();
    public virtual ICollection<WelfareNotification> Notifications { get; set; } = new List<WelfareNotification>();
}
