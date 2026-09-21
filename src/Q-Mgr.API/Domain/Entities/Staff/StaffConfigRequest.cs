using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// ONE QUEUE for everything a member of staff asks for and may not simply take: a period on the
/// timetable, a swap with a colleague, or a class they would teach.
///
/// WHY THIS EARNS A TABLE, when the standing constraint is to widen an existing row first. Three
/// things, and any one of them would be enough:
///   * it is queried ACROSS meetings of its own kind, by person and by branch, for a queue screen;
///   * it has a lifecycle that outlives whatever it is about — a request survives the draft being
///     republished, and "what happened to my request" must still have an answer afterwards;
///   * it needs a <see cref="ReminderStage"/> COLUMN. Every ladder in this module claims its stage
///     with a conditional ExecuteUpdateAsync BEFORE sending, and a stage buried in a jsonb blob
///     cannot be claimed that way, so two workers would both chase the same request.
///
/// THE DECISION IS NEVER THE ASKER'S. <see cref="DecidedByUserId"/> may never equal
/// <see cref="RequestedByUserId"/>, checked on the decision path itself rather than left to the
/// permission layer — a Director of Studies holds timetable.manage and also teaches, so the
/// permission says yes and the rule still has to say no. NIST SP 800-53 AC-5 is the external name
/// for it; "nobody marks their own register entry" and "a meeting cannot adopt its own minutes" are
/// the same rule already written down in this codebase.
/// </summary>
public class StaffConfigRequest : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    public ConfigRequestKind Kind { get; set; }
    public ConfigRequestState State { get; set; } = ConfigRequestState.Pending;

    public Guid RequestedByUserId { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The asker's own words. It is why the request exists, so the decider always sees it.</summary>
    public string? Reason { get; set; }

    // ---- What is being asked for. Flattened rather than jsonb so the queue screen needs no second
    // ---- query to be readable, and so the duplicate index below can exist at all.

    /// <summary>The timetable the slot belongs to. Null for a class assignment, which is not about one.</summary>
    public Guid? TimetableId { get; set; }

    public int? CycleDay { get; set; }
    public string? PeriodKey { get; set; }

    /// <summary>The class as configured, and its match form — Trim().ToLowerInvariant(), the rule everywhere.</summary>
    public string? ClassName { get; set; }
    public string? ClassNameNormalized { get; set; }

    public Guid? SubjectId { get; set; }
    public string? Room { get; set; }

    /// <summary>ClassAssignment: the planned load the teacher expects to need.</summary>
    public int? PeriodsPerWeek { get; set; }

    // ---- A swap needs two lessons and the other teacher's agreement.

    /// <summary>The asker's own lesson being offered.</summary>
    public Guid? MyLessonId { get; set; }

    /// <summary>The colleague's lesson being asked for.</summary>
    public Guid? TheirLessonId { get; set; }

    public Guid? CounterpartUserId { get; set; }

    /// <summary>
    /// When the colleague agreed. A swap cannot be approved before this is set: a decider moving a
    /// teacher's lesson without that teacher having agreed is exactly the corridor negotiation this
    /// feature exists to replace, not to automate.
    /// </summary>
    public DateTime? CounterpartAgreedAt { get; set; }

    // ---- The decision.

    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }

    /// <summary>Required on a refusal. A refusal with no reason sends people back to the corridor.</summary>
    public string? DecisionReason { get; set; }

    /// <summary>What approving it produced, so the row can say what it did and not merely that it was approved.</summary>
    public Guid? ResultLessonId { get; set; }
    public Guid? ResultAssignmentId { get; set; }

    /// <summary>
    /// ONE STRING THAT SAYS WHAT THIS REQUEST IS ABOUT, so "one open request per person per thing"
    /// can be a unique index rather than a handler check.
    ///
    /// IT EXISTS BECAUSE POSTGRESQL TREATS NULLS AS DISTINCT IN A UNIQUE INDEX. The first attempt
    /// indexed the seven payload columns directly, and it never fired for a ClassAssignment — which
    /// carries a null CycleDay and a null PeriodKey — so four simultaneous identical requests made
    /// four rows. Found by the e2e firing them at once, which is the only way that shows.
    ///
    /// A single never-null column is the fix rather than NULLS NOT DISTINCT: it needs no minimum
    /// PostgreSQL version, and it makes the dedupe rule something you can read instead of something
    /// that emerges from a seven-column index.
    /// </summary>
    public string DedupeKey { get; set; } = string.Empty;

    /// <summary>
    /// Claimed by the reminder ladder with a conditional update before anything is sent, so two
    /// workers cannot both chase the same request. Never read-modify-write.
    /// </summary>
    public int ReminderStage { get; set; }
    public DateTime? LastRemindedAt { get; set; }

    public bool IsOpen => State == ConfigRequestState.Pending;
}
