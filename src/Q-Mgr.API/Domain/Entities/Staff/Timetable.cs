using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// One version of a branch's timetable for a term (duty rota plan §3.2, §6). A table because a Draft and a Published
/// version must coexist while the timetable master edits — a status column on lessons cannot say "the published one".
///
/// A Published timetable is immutable: a change is a new Draft copied from it, published over it. At most one
/// Published version covers any date of a branch; publish enforces that under an advisory lock, backed by a partial
/// unique index on the start date (overlap itself cannot be a btree index, and a gist exclusion constraint would
/// need an extension this project does not install). Since 2026-09-22 publishing over a live version must be ASKED
/// for (<c>PublishTimetableRequest.Replace</c>) rather than happening silently — see that property for why.
///
/// A one-day departure from it is a <see cref="TimetableLessonException"/>, not an edit.
/// </summary>
public class Timetable : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The policy period (term) key it is for, e.g. "2026-T3".</summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>Teaching days in one cycle: the bell schedule's teaching weekdays × 1 (a week) or × 2 (an A/B cycle). Fixed at creation.</summary>
    public int CycleDays { get; set; } = 5;

    public TimetableStatus Status { get; set; } = TimetableStatus.Draft;
    public DateTime? PublishedAt { get; set; }
    public Guid? PublishedByUserId { get; set; }

    /// <summary>The branch-local dates the version is in force. Cycle day 1 is the teaching weekday on or after <see cref="EffectiveFrom"/>'s week start.</summary>
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }

    /// <summary>
    /// The integrity sweep's last reported set of hard issues, one short key each (plan §6.3). Stored as the set rather
    /// than one hash so the sweep can say how many are NEW: a clash fixed and another appearing is still one new clash.
    /// </summary>
    public string[] ReportedIssueKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// THE APPOINTED TIMETABLE MASTER(S) OF THIS VERSION (2026-09-22). Empty means nobody is named and only
    /// <c>timetable.manage</c> opens it, which is how every version behaved before this column existed.
    ///
    /// WHY IT EXISTS. <c>timetable.manage</c> is one global permission, so any holder could edit, publish or
    /// archive ANY version of ANY branch — and a school's actual model is "one teacher is appointed timetable
    /// master, and somebody else writes the exam supervision". That had no representation at all, and
    /// <c>CreatedBy</c> was written and never read for authorisation. NIST SP 800-53 AC-6 (least privilege)
    /// and AC-5 (separation of duties, which exists to address abuse of AUTHORISED privileges).
    ///
    /// The rule has ONE home, <c>TimetableAccess.MayWrite</c>: a named manager, or a permission holder.
    /// Appointing is the permission holder's act alone — a manager may not add or remove managers, including
    /// themselves, or the appointment is self-serve and the control is decoration.
    ///
    /// A permission holder who is NOT named may still write it, deliberately: a school whose timetable master
    /// leaves mid-term, is ill, or leaves a draft locked must not be shut out of its own timetable, because a
    /// system that can be bricked by one person's absence gets worked around with a shared login. The abuse
    /// concern is answered by visibility instead — every such write is an <c>ActivityActions.TimetableOverridden</c>
    /// event AND a notification to every named manager. Nothing silent.
    ///
    /// This is the same delegation idiom <see cref="StaffDuty.RecorderUserIds"/> has had all along; the
    /// timetable was the outlier.
    /// </summary>
    public Guid[] ManagerUserIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Claimed by the expiry reminder ladder with a conditional update before anything is sent, so two workers
    /// cannot both send the same stage. Every ladder in this module works this way; a stage in a jsonb blob
    /// could not be claimed at all.
    /// </summary>
    public int ReminderStage { get; set; }

    public ICollection<TimetableLesson> Lessons { get; set; } = new List<TimetableLesson>();

    /// <summary>
    /// True when this version is Published and its last day has passed. DERIVED, never stored — the status
    /// column only ever holds Draft, Published or Archived, and a stored "expired" would disagree with the
    /// dates the moment somebody edited them. Same call as StaffEmploymentStatus.
    /// </summary>
    public TimetableStatus StatusOn(DateOnly today)
        => Status == TimetableStatus.Published && EffectiveTo < today ? TimetableStatus.Expired : Status;
}

/// <summary>
/// ONE LESSON, ONE DATE, ONE DEPARTURE from the published timetable (2026-09-22). The other half of a swap:
/// a permanent trade between two teachers is a re-published version, and a one-off is a pair of these.
///
/// WHY THIS EARNS A TABLE, when the standing constraint is to widen an existing row first. Three things were
/// considered and rejected before it:
///   * a column on the generated <see cref="StaffDuty"/> — but duties exist only 14 days ahead
///     (<c>StaffLessons.WindowDays</c>), and a cover agreed three weeks out has no duty to carry it;
///   * the approved <see cref="StaffConfigRequest"/> — which does hold almost this shape, but NOT every
///     exception comes from a request: a timetable master records cover directly, and there would then be two
///     mechanisms for one concept;
///   * <c>Branch.Settings</c> JSON — queried by date on every materialisation run, high churn, and that column
///     already has three writers and a standing warning about a fourth.
/// It is also queried by (branch, date) rather than by parent, which is what a table is for. Same reasoning as
/// ClassTeacherAssignment being its own table rather than a field on a class.
///
/// A CANCELLED EXCEPTION IS NOT A DELETED LESSON. The timetable still says the lesson exists; the exception says
/// it did not happen that day. Withdrawing the exception puts it back.
/// </summary>
public class TimetableLessonException : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>The version it belongs to. An exception against a replaced version means nothing, so it dies with it.</summary>
    public Guid TimetableId { get; set; }

    public Guid TimetableLessonId { get; set; }

    /// <summary>The BRANCH-LOCAL date. Not a UTC instant: "Thursday" is a school's Thursday, not the server's.</summary>
    public DateOnly Date { get; set; }

    public LessonExceptionKind Kind { get; set; }

    /// <summary>
    /// Who teaches it instead. Set for a Cover and null for a Cancelled, enforced by the controller and by a
    /// check constraint — a cover with nobody covering is a cancellation wearing the wrong label.
    /// </summary>
    public Guid? CoverUserId { get; set; }

    public string? Reason { get; set; }

    /// <summary>The self-service request it came out of, when a teacher arranged it rather than a master recording it.</summary>
    public Guid? SourceRequestId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Timetable Timetable { get; set; } = null!;
    public TimetableLesson Lesson { get; set; } = null!;
}

/// <summary>
/// One teacher teaching one class one subject in one period of the cycle. One row per TEACHER so a unique index can
/// refuse a teacher double-booking in the database, even when two masters' checks both passed.
///
/// A joint lesson — an elective taught to S5A and S5B together, or two teachers co-teaching — is several rows sharing
/// a <see cref="GroupId"/>, and is not a class or teacher clash with itself.
/// </summary>
public class TimetableLesson : BaseAuditableEntity
{
    public Guid TimetableId { get; set; }

    /// <summary>1..Timetable.CycleDays.</summary>
    public int CycleDay { get; set; }

    /// <summary>A lesson period's key in the bell schedule ("P3").</summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>The class as configured, and its match form — <c>Trim().ToLowerInvariant()</c>, the rule everywhere a class is matched.</summary>
    public string ClassName { get; set; } = string.Empty;
    public string ClassNameNormalized { get; set; } = string.Empty;

    public Guid SubjectId { get; set; }
    public Guid TeacherUserId { get; set; }

    public string? Room { get; set; }
    public string? RoomNormalized { get; set; }

    public Guid? GroupId { get; set; }

    public Timetable Timetable { get; set; } = null!;
    public Subject Subject { get; set; } = null!;
}
