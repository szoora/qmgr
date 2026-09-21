using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// A meeting, an exam session, a prep slot, a lesson: when, where, who is expected, and — the
/// minutes-taker delegation — who may take the register. A table because it has its own lifecycle
/// (scheduled, register open, closed), is foreign-keyed by records, and reminders and reports hang
/// off it. Expected attendees and named recorders are <c>uuid[]</c> columns, not join tables;
/// nothing is stored per link. Minutes are a Library document (<see cref="MinutesMediaContentId"/>),
/// which is what lets them also go on a playlist or a share link with no new code.
///
/// Taking the register is authorised by <c>me ∈ RecorderUserIds</c> (or staff.duties.manage), and
/// the records it writes are for <see cref="ExpectedUserIds"/> only. A teacher named recorder for
/// Tuesday's staff meeting can mark the Director of Studies absent from that meeting and nothing
/// else. Delegation is not scope.
///
/// No recurrence rule in v1: a "duplicate to next week" action covers the real use, and an RRULE
/// column is the appended upgrade if a school asks.
///
/// Branch-scoped with no global EF query filter; every by-ID action calls VerifyBranchOwnership.
/// </summary>
public class StaffDuty : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ParameterId { get; set; }

    /// <summary>
    /// Session (a meeting, an invigilation — every duty before the rota), Rota (on duty for a span) or Lesson (one
    /// occurrence from the published timetable). Duty rota plan §3.1: one table, because all three are "a person
    /// expected somewhere, with an outcome", and the register, reminders, portal, scoring and reports already work.
    /// </summary>
    public DutyKind Kind { get; set; } = DutyKind.Session;

    /// <summary>
    /// The highest pre-start reminder stage sent (plan §8.1). Written only by a conditional
    /// <c>UPDATE … WHERE "ReminderStage" &lt; @stage</c>, so two sweeps can never send a stage twice; reset to 0 when
    /// the duty is rescheduled into the future.
    /// </summary>
    public int ReminderStage { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }

    /// <summary>Null means every active staff member in the branch is expected.</summary>
    public Guid[]? ExpectedUserIds { get; set; }

    /// <summary>Who may take the register. This is the delegation.</summary>
    public Guid[] RecorderUserIds { get; set; } = Array.Empty<Guid>();

    public DateTime? RegisterOpenedAt { get; set; }
    public DateTime? RegisterClosedAt { get; set; }
    public Guid? RegisterClosedByUserId { get; set; }

    /// <summary>
    /// The ADOPTED minutes as a Library document. Until 2026-09-20 this was the whole feature: a PDF
    /// written somewhere else and attached by hand. It is now the archival snapshot of the record
    /// below, attached automatically when the minutes are approved — which is what keeps the share
    /// links, the retention window and the read log working with no new code.
    /// </summary>
    public Guid? MinutesMediaContentId { get; set; }

    // ---- Minutes of the meeting (2026-09-20) -----------------------------------------------------
    // The standards these follow are set out at the top of Q-Mgr.Shared/Application/DTOs/MinutesDto.cs:
    // Robert's Rules for the content, open-meeting/records law for the lifecycle, ISO 15489 for the
    // qualities. Columns on this row rather than a minutes table, per the standing constraint: a set
    // of minutes belongs to exactly one meeting, has no life without it, and is only ever read one
    // meeting at a time. Its ACTION POINTS are the exception and have their own table — see
    // StaffMinuteAction for the three reasons.

    /// <summary>
    /// The minutes document: section answers, motions and post-adoption corrections, as jsonb.
    /// Read and written ONLY through the minutes controller's serializer — never parsed elsewhere,
    /// the same rule as Branch.Settings and Organization.Settings.
    /// </summary>
    public string? MinutesJson { get; set; }

    /// <summary>None until somebody starts them. Draft → Circulated → Approved, and never backwards.</summary>
    public MinutesStatus MinutesStatus { get; set; } = MinutesStatus.None;

    public DateTime? MinutesCirculatedAt { get; set; }

    /// <summary>When the body ADOPTED them. This is the moment they become the official record.</summary>
    public DateTime? MinutesApprovedAt { get; set; }
    public Guid? MinutesApprovedByUserId { get; set; }

    /// <summary>
    /// The meeting that adopted them, which is normally the NEXT one. The date alone does not say
    /// which meeting did it, and "adopted at the meeting of 4 October" is what the record has to be
    /// able to state.
    /// </summary>
    public Guid? MinutesApprovedAtDutyId { get; set; }

    public DateTime? MinutesUpdatedAt { get; set; }
    public Guid? MinutesUpdatedByUserId { get; set; }

    /// <summary>The ladder's claim column for "these minutes have not been circulated yet".</summary>
    public int MinutesReminderStage { get; set; }

    /// <summary>Set when the ahead-of-time reminder went out. One reminder per row, gated by a timestamp.</summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>Set when the recorders were chased for a register not taken after EndsAt.</summary>
    public DateTime? RegisterChaseSentAt { get; set; }

    public Guid CreatedByUserId { get; set; }

    // ---- Duty rota (plan §3.2, Phase 1 migration AddDutyRota) -------------------------------------------

    /// <summary>
    /// The slots one "Generate a rota" wrote share this id. The rotation pattern itself is not stored — a pattern is
    /// an action, not a thing — so "Extend" reads the order back from the series and "Cancel the series" finds it here.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// The administrator(s) on duty: they supervise the people on duty, take the close-out register, read the
    /// on-duty reports and write their own (plan §4.3). Empty on a Session duty.
    /// </summary>
    public Guid[] SupervisorUserIds { get; set; } = Array.Empty<Guid>();

    /// <summary>How often the people on a rota slot write a report (plan §4.3). None on a Session duty.</summary>
    public ReportCadence ReportCadence { get; set; } = ReportCadence.None;

    /// <summary>Branch-local due time of each report period ("18:00"). Null uses the policy default.</summary>
    public TimeOnly? ReportDueLocalTime { get; set; }

    // ---- Lessons (plan §3.2, §7): set only on a Kind = Lesson duty. -------------------------------------------

    /// <summary>The timetable lesson this occurrence was materialised from; null for a recovery lesson scheduled by hand.</summary>
    public Guid? TimetableLessonId { get; set; }

    /// <summary>The class (or "S5A + S5B" for a joint lesson), copied so a later rename or re-publish cannot rewrite history.</summary>
    public string? ClassName { get; set; }

    public Guid? SubjectId { get; set; }

    public string? Room { get; set; }

    /// <summary>A recovery lesson names the missed lesson it makes up for (MoES's Lesson Recovery Schedule).</summary>
    public Guid? RecoversDutyId { get; set; }

    /// <summary>
    /// jsonb: { "userId": "2026-09-22T05:12:00Z", … } — "Seen — I'm on duty" (plan §4.2). Stops the pre-duty ladder
    /// for that person. Written only by one atomic <c>||</c> UPDATE guarded by <c>jsonb_exists</c>, never
    /// read-modify-write (the notice acknowledgement race, found 2026-09-16). Reset when the slot is rescheduled.
    /// </summary>
    public string Acknowledgements { get; set; } = "{}";

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual PerformanceParameter? Parameter { get; set; }
    public virtual ICollection<StaffPerformanceRecord> Records { get; set; } = new List<StaffPerformanceRecord>();
}
