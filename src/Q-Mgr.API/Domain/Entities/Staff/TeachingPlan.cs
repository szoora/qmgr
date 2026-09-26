using QMgr.Application.DTOs;
using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// A lesson plan or a scheme of work (plan LESSON_PLANS_AND_SCHEMES_OF_WORK, built 2026-09-26). THE ONE NEW TABLE the
/// plan adds, and §5.1 says why no existing row could carry it: a duty report must belong to a duty and a scheme has
/// none; a self-service request has one decision slot and no document; a performance record is a scoring event; the
/// Library is gated by the Communications module and read organisation-wide. A plan is its own resource with its own
/// life, queried by person, department, term and state, with its own reminder stage — the same argument that made
/// StaffMinuteAction a table.
///
/// Nothing else was added: the one uploaded file is columns, the trail is JSON on the row, templates and the curriculum
/// live in settings blobs, and records of work are derived from lesson flags.
///
/// <list type="bullet">
/// <item>A submitted plan is FROZEN; the author withdraws it, or it is returned with a snapshot in the trail.</item>
/// <item>An approved plan is never edited. A change is a new version (<see cref="SupersedesId"/>); the approved one stays
/// current until the new one is approved.</item>
/// <item>The one field open after approval is <see cref="Reflection"/>, written after the lesson.</item>
/// <item>Who may read or act is <c>TeachingPlanAccess</c> and nothing else; nobody takes two stages (DutySeparation).</item>
/// </list>
/// </summary>
public class TeachingPlan : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public TeachingPlanKind Kind { get; set; }
    public Guid AuthorUserId { get; set; }
    public Guid SubjectId { get; set; }

    /// <summary>The school's own spelling of each class; one plan may cover parallel streams.</summary>
    public string[] ClassNames { get; set; } = Array.Empty<string>();
    /// <summary><see cref="TeachingPlanRules.ClassKey"/> of <see cref="ClassNames"/>: what is compared.</summary>
    public string ClassKey { get; set; } = string.Empty;

    /// <summary>The term (the staff policy's period key, "2026-T3").</summary>
    public string PeriodKey { get; set; } = string.Empty;

    // A lesson plan's lesson.
    public DateOnly? LessonDate { get; set; }
    public Guid? DutyId { get; set; }
    public Guid? TimetableLessonId { get; set; }
    /// <summary>The scheme this lesson plan teaches from, and the week line.</summary>
    public Guid? SchemeId { get; set; }
    public string? SchemeRowKey { get; set; }

    public string Title { get; set; } = string.Empty;
    /// <summary>jsonb: <see cref="LessonPlanContentDto"/> for a lesson plan.</summary>
    public string SectionsJson { get; set; } = "{}";
    /// <summary>jsonb: a list of <see cref="SchemeRowDto"/> for a scheme of work.</summary>
    public string RowsJson { get; set; } = "[]";

    public TeachingPlanStatus Status { get; set; } = TeachingPlanStatus.Draft;
    /// <summary>1 or 2, fixed when the plan is submitted, so changing the setting never strands a plan mid-chain.</summary>
    public int Stages { get; set; } = 2;
    public DateTime? SubmittedAt { get; set; }
    public Guid? ForwardedByUserId { get; set; }
    public DateTime? ForwardedAt { get; set; }
    /// <summary>Why stage 1 was skipped (the author heads the department and there is no deputy; no head at all).</summary>
    public string? StageSkippedReason { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ReturnedByUserId { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public string? ReturnReason { get; set; }
    /// <summary>When it reached whoever has it now — the review ladder's anchor.</summary>
    public DateTime? WaitingSince { get; set; }

    /// <summary>jsonb: an append-only list of <see cref="PlanTrailEntryDto"/>.</summary>
    public string TrailJson { get; set; } = "[]";

    public string? Reflection { get; set; }
    public DateTime? ReflectionAt { get; set; }

    // An uploaded PDF — one per plan, rebuilt in the browser and capped by the server.
    public string? FileUrl { get; set; }
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public long? OriginalSizeBytes { get; set; }
    public int? FilePages { get; set; }

    public int Version { get; set; } = 1;
    public Guid? SupersedesId { get; set; }
    /// <summary>The version that counts: the latest approved one, or the only one. A revision in progress is not current.</summary>
    public bool IsCurrent { get; set; } = true;

    /// <summary>A client-generated id, so a double press on "Plan this lesson" makes one plan.</summary>
    public Guid? ClientRequestId { get; set; }

    /// <summary>The due-plan ladder (the author) and the review ladder (the reviewer). Each claimed by a conditional UPDATE.</summary>
    public int ReminderStage { get; set; }
    public int ReviewReminderStage { get; set; }
    public DateTime? LastRemindedAt { get; set; }

    /// <summary>xmin: two editors of one draft cannot overwrite each other silently.</summary>
    public uint RowVersion { get; set; }
}
