using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Identity;

namespace QMgr.Application.DTOs;

// LESSON PLANS AND SCHEMES OF WORK (plan docs/plans/LESSON_PLANS_AND_SCHEMES_OF_WORK.md, built 2026-09-26).
//
// A plan is written on the form (the main route, decision L2), pre-filled from the timetable, the roll and the scheme;
// a filled Word template is READ into the form and never stored; an uploaded PDF is the fallback, rebuilt in the browser
// and capped by the server. It goes teacher → head of the subject's department → an approver (the Director of Studies),
// and nobody takes two stages of one plan (DutySeparation).

public enum TeachingPlanKind { LessonPlan = 0, SchemeOfWork = 1 }

/// <summary>Values are appended, never inserted: they are stored.</summary>
public enum TeachingPlanStatus
{
    Draft = 0,
    /// <summary>With the head of department (stage 1).</summary>
    Submitted = 1,
    /// <summary>With an approver (stage 2).</summary>
    Forwarded = 2,
    Approved = 3,
    /// <summary>Back with the author, with a reason; a snapshot of what was returned is in the trail.</summary>
    Returned = 4,
    Withdrawn = 5
}

public enum PlanSectionKind
{
    /// <summary>Free text.</summary>
    Text = 0,
    /// <summary>One of <see cref="PlanSectionDto.Choices"/>.</summary>
    Choice = 1,
    /// <summary>Any of <see cref="PlanSectionDto.Choices"/> (generic skills, values, cross-cutting issues).</summary>
    MultiChoice = 2,
    /// <summary>The procedure table: a row per phase, with teacher activity, learner activity and minutes.</summary>
    Procedure = 3
}

/// <summary>What the school requires (decision L6).</summary>
public enum PlanRequirement { EveryLesson = 0, Weekly = 1, SchemesOnly = 2 }

public enum PlanTrailKind { Comment = 0, Submitted = 1, Forwarded = 2, Approved = 3, Returned = 4, Withdrawn = 5, StageSkipped = 6, Reflection = 7, FileAdded = 8, FileRemoved = 9, Revised = 10 }

public record PlanSectionDto
{
    [MaxLength(40)] public string Key { get; set; } = string.Empty;
    [MaxLength(100)] public string Title { get; set; } = string.Empty;
    [MaxLength(300)] public string? Hint { get; set; }
    public PlanSectionKind Kind { get; set; } = PlanSectionKind.Text;
    public List<string> Choices { get; set; } = new();
    public bool Required { get; set; }
    /// <summary>A section the product fills from the scheme or the curriculum (topic, sub-topic, competency, outcomes).
    /// Its key cannot change and it cannot be removed; its title and hint can.</summary>
    public bool System { get; set; }
    /// <summary>Written AFTER the lesson (NCDC's self-evaluation); the one section open once a plan is approved.</summary>
    public bool AfterLesson { get; set; }
}

public record PlanColumnDto
{
    [MaxLength(40)] public string Key { get; set; } = string.Empty;
    [MaxLength(100)] public string Title { get; set; } = string.Empty;
    [MaxLength(300)] public string? Hint { get; set; }
    public bool Required { get; set; }
    public bool System { get; set; }
}

/// <summary>The school's lesson-planning rules, in the staff policy blob (read only through IStaffPerformancePolicyService).</summary>
public record TeachingPlanSettingsDto
{
    /// <summary>Empty means <see cref="TeachingPlanDefaults.LessonPlanSections"/>.</summary>
    public List<PlanSectionDto> LessonPlanTemplate { get; set; } = new();
    /// <summary>Empty means <see cref="TeachingPlanDefaults.Phases"/>.</summary>
    public List<string> ProcedurePhases { get; set; } = new();
    /// <summary>Empty means <see cref="TeachingPlanDefaults.SchemeColumns"/>.</summary>
    public List<PlanColumnDto> SchemeColumns { get; set; } = new();
    public PlanRequirement Requirement { get; set; } = PlanRequirement.EveryLesson;
    /// <summary>Decision L4: a lesson plan is approved by the head of department (1), or also goes to an approver (2).
    /// A scheme of work always takes both stages.</summary>
    public int LessonPlanStages { get; set; } = 1;
    /// <summary>A lesson's plan is due by this branch-local hour the day before (decision: 18).</summary>
    public int DeadlineHourDayBefore { get; set; } = 18;
    /// <summary>A scheme for a term is due by the end of this week of the term.</summary>
    public int SchemeDueWeek { get; set; } = 1;
    /// <summary>Decision L12: approved plans readable by the department's teachers. Off by default.</summary>
    public bool ShareApprovedWithDepartment { get; set; }
    public bool UploadsEnabled { get; set; } = true;
    public int LessonPlanTargetKb { get; set; } = TeachingPlanLimits.LessonPlanTargetKb;
    public int LessonPlanCapKb { get; set; } = TeachingPlanLimits.LessonPlanCapKb;
    public int LessonPlanMaxPages { get; set; } = TeachingPlanLimits.LessonPlanMaxPages;
    public int SchemeTargetKb { get; set; } = TeachingPlanLimits.SchemeTargetKb;
    public int SchemeCapKb { get; set; } = TeachingPlanLimits.SchemeCapKb;
    public int SchemeMaxPages { get; set; } = TeachingPlanLimits.SchemeMaxPages;
}

/// <summary>Decision L3. The server clamps any stored value into these bounds.</summary>
public static class TeachingPlanLimits
{
    public const int LessonPlanTargetKb = 50, LessonPlanCapKb = 64, LessonPlanMaxPages = 4;
    public const int SchemeTargetKb = 150, SchemeCapKb = 200, SchemeMaxPages = 30;
    /// <summary>The largest file the browser will even open to shrink (the entry gate). Never what is stored.</summary>
    public const int ReadLimitMb = 10;
    /// <summary>No school can raise a cap past these.</summary>
    public const int CeilingLessonPlanKb = 256, CeilingSchemeKb = 1024, CeilingPages = 60;
}

/// <summary>One step of the procedure (a phase: introduction, development, evaluation, conclusion).</summary>
public record ProcedureStepDto
{
    [MaxLength(60)] public string Phase { get; set; } = string.Empty;
    public int? Minutes { get; set; }
    [MaxLength(2000)] public string? Teacher { get; set; }
    [MaxLength(2000)] public string? Learner { get; set; }
}

/// <summary>Filled in by the product when a plan is started, and shown in the plan's header. A snapshot: a later change
/// to the timetable does not rewrite a plan somebody submitted.</summary>
public record PlanHeaderDto
{
    public string? SchoolName { get; set; }
    public string? TeacherName { get; set; }
    public string? SubjectName { get; set; }
    public string? ClassText { get; set; }
    public string? TermName { get; set; }
    public int? Week { get; set; }
    public string? Time { get; set; }
    public int? DurationMinutes { get; set; }
    public int? Learners { get; set; }
    public string? Room { get; set; }
}

/// <summary>A lesson plan's content (SectionsJson).</summary>
public record LessonPlanContentDto
{
    public PlanHeaderDto Header { get; set; } = new();
    /// <summary>{ sectionKey: text } — a MultiChoice answer is its choices joined with "; ".</summary>
    public Dictionary<string, string> Answers { get; set; } = new();
    public List<ProcedureStepDto> Procedure { get; set; } = new();
}

/// <summary>One week line of a scheme of work (RowsJson). <see cref="Key"/> is stable: a lesson plan points at it.</summary>
public record SchemeRowDto
{
    [MaxLength(40)] public string Key { get; set; } = string.Empty;
    public int Week { get; set; }
    public int? Periods { get; set; }
    public Dictionary<string, string> Cells { get; set; } = new();
}

public record PlanTrailEntryDto
{
    public PlanTrailKind Kind { get; set; }
    public Guid? ByUserId { get; set; }
    public string? ByName { get; set; }
    public DateTime At { get; set; }
    public string? Body { get; set; }
    /// <summary>The section a comment is about, so a reviewer's note sits beside what it is about.</summary>
    public string? SectionKey { get; set; }
    /// <summary>On a return: what was returned, so the resubmitted version can be compared.</summary>
    public string? SnapshotJson { get; set; }
}

public record TeachingPlanDto
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public TeachingPlanKind Kind { get; set; }
    public Guid AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public List<string> ClassNames { get; set; } = new();
    public string PeriodKey { get; set; } = string.Empty;
    public string? PeriodName { get; set; }
    public DateOnly? LessonDate { get; set; }
    public Guid? DutyId { get; set; }
    public Guid? SchemeId { get; set; }
    public string? SchemeRowKey { get; set; }
    public string Title { get; set; } = string.Empty;
    public LessonPlanContentDto? Content { get; set; }
    public List<SchemeRowDto>? Rows { get; set; }
    public TeachingPlanStatus Status { get; set; }
    /// <summary>How many stages this plan takes (1 or 2), decided when it was submitted.</summary>
    public int Stages { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public string? ForwardedByName { get; set; }
    public DateTime? ForwardedAt { get; set; }
    public string? StageSkippedReason { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ReturnReason { get; set; }
    public string? Reflection { get; set; }
    public List<PlanTrailEntryDto> Trail { get; set; } = new();
    public string? FileUrl { get; set; }
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public long? OriginalSizeBytes { get; set; }
    public int? FilePages { get; set; }
    public int Version { get; set; }
    public Guid? SupersedesId { get; set; }
    public bool IsCurrent { get; set; }
    /// <summary>Who has the plan now, in words ("With Grace Nansubuga (head of Sciences)").</summary>
    public string? WaitingOn { get; set; }
    public uint RowVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public bool CanIEdit { get; set; }
    public bool CanISubmit { get; set; }
    public bool CanIWithdraw { get; set; }
    public bool CanIForward { get; set; }
    public bool CanIApprove { get; set; }
    public bool CanIReturn { get; set; }
    public bool CanIComment { get; set; }
    public bool CanIReflect { get; set; }
    public bool CanIRevise { get; set; }
    /// <summary>Why the reader cannot act, when they are a reviewer who is refused (DutySeparation's sentence).</summary>
    public string? WhyNot { get; set; }

    /// <summary>The school's template as it stands: the form draws itself from these.</summary>
    public List<PlanSectionDto> Sections { get; set; } = new();
    public List<string> Phases { get; set; } = new();
    public List<PlanColumnDto> Columns { get; set; } = new();
    /// <summary>The school's file rules for this kind of plan (decision L3).</summary>
    public bool UploadsEnabled { get; set; }
    public int FileTargetKb { get; set; }
    public int FileCapKb { get; set; }
    public int FileMaxPages { get; set; }
}

/// <summary>A row in a queue or a teacher's list: no content.</summary>
public record TeachingPlanSummaryDto
{
    public Guid Id { get; set; }
    public TeachingPlanKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string? AuthorSortName { get; set; }
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
    public List<string> ClassNames { get; set; } = new();
    public string PeriodKey { get; set; } = string.Empty;
    public DateOnly? LessonDate { get; set; }
    public Guid? DutyId { get; set; }
    public TeachingPlanStatus Status { get; set; }
    public int Stages { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public bool HasFile { get; set; }
    public int Version { get; set; }
    public string? WaitingOn { get; set; }
    public bool AwaitsMe { get; set; }
}

/// <summary>One of the teacher's lessons in a week, with its plan's state (the planbook).</summary>
public record PlanbookLessonDto
{
    public Guid DutyId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? ClassName { get; set; }
    public Guid? SubjectId { get; set; }
    public string? SubjectName { get; set; }
    public string? Room { get; set; }
    /// <summary>The lesson's own outcome, from the lesson flags: taught, missed, …; null before it is recorded.</summary>
    public string? Outcome { get; set; }
    public Guid? PlanId { get; set; }
    public TeachingPlanStatus? PlanStatus { get; set; }
    public string? PlanTitle { get; set; }
    /// <summary>True when a plan is required for this lesson and none is submitted by the deadline.</summary>
    public bool Due { get; set; }
    public DateTime? DueAt { get; set; }
}

public record PlanbookDto
{
    public DateOnly WeekStart { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodName { get; set; }
    public int? Week { get; set; }
    public List<PlanbookLessonDto> Lessons { get; set; } = new();
    public List<TeachingPlanSummaryDto> Schemes { get; set; } = new();
    /// <summary>The subjects and classes the teacher may plan for (their live teaching assignments).</summary>
    public List<PlanTeachingDto> Teaching { get; set; } = new();
    public PlanRequirement Requirement { get; set; }
    public int LessonPlanStages { get; set; }
    public bool UploadsEnabled { get; set; }
    /// <summary>The branch's time zone, so the page shows lesson times on the school's clock (BranchClock).</summary>
    public string? TimeZone { get; set; }
}

public record PlanTeachingDto
{
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
}

public record TeachingPlanQueueDto
{
    public List<TeachingPlanSummaryDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int AwaitingMe { get; set; }
    public bool CanReview { get; set; }
    public bool CanApprove { get; set; }
}

public record CreateTeachingPlanRequest
{
    public TeachingPlanKind Kind { get; set; }
    /// <summary>A lesson plan for a timetabled lesson: everything else is taken from it.</summary>
    public Guid? DutyId { get; set; }
    public Guid? SubjectId { get; set; }
    public List<string> ClassNames { get; set; } = new();
    public string? PeriodKey { get; set; }
    public DateOnly? LessonDate { get; set; }
    /// <summary>Copy forward: start from this earlier plan's content (the reader's own, or one they can read).</summary>
    public Guid? CopyFromId { get; set; }
    /// <summary>Copy forward from the author's own latest plan for the same subject and classes.</summary>
    public bool CopyFromLast { get; set; }
    /// <summary>Optional: the scheme line this lesson teaches. Defaults to this week's line of the approved scheme.</summary>
    public string? SchemeRowKey { get; set; }
    /// <summary>A client-generated id: a double press makes one plan.</summary>
    public Guid? ClientRequestId { get; set; }
}

public record SaveTeachingPlanRequest
{
    [MaxLength(200)] public string? Title { get; set; }
    public List<string>? ClassNames { get; set; }
    public LessonPlanContentDto? Content { get; set; }
    public List<SchemeRowDto>? Rows { get; set; }
    public Guid? SchemeId { get; set; }
    [MaxLength(40)] public string? SchemeRowKey { get; set; }
    public uint RowVersion { get; set; }
}

public record PlanDecisionRequest
{
    [MaxLength(2000)] public string? Note { get; set; }
    [MaxLength(40)] public string? SectionKey { get; set; }
}

public record CopyTeachingPlanRequest
{
    /// <summary>Parallel streams: the same plan also covers these classes (one plan, several class names).</summary>
    public List<string> AlsoClassNames { get; set; } = new();
}

public record BumpTeachingPlanRequest
{
    /// <summary>The lesson this plan now belongs to — a missed lesson's plan carried to its recovery, or the next lesson.</summary>
    public Guid ToDutyId { get; set; }
}

/// <summary>What a filled Word template was read as. Nothing is stored; the page puts it in the form for the teacher to check.</summary>
public record ReadTemplateResultDto
{
    public TeachingPlanKind Kind { get; set; }
    public LessonPlanContentDto? Content { get; set; }
    public List<SchemeRowDto>? Rows { get; set; }
    public List<string> Warnings { get; set; } = new();
    public int SectionsRead { get; set; }
}

/// <summary>The school's own format, for the printable blank sheets (2026-09-26). Readable by any member of staff: it
/// carries nothing a teacher could not already see on a plan.</summary>
public record PlanTemplateDto
{
    public List<PlanSectionDto> Sections { get; set; } = new();
    public List<string> Phases { get; set; } = new();
    public List<PlanColumnDto> Columns { get; set; } = new();
    public string? PeriodName { get; set; }
    /// <summary>How many weeks the current term runs — a blank scheme gets one line per week.</summary>
    public int Weeks { get; set; }
    public string? TeacherName { get; set; }
}

// ---- The curriculum list (decision L9): Organization.Settings["Curriculum"] -------------------------------------------

public record CurriculumTopicDto
{
    [MaxLength(40)] public string Id { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    /// <summary>A class LEVEL ("S1"), matched against a class name's key by prefix: "S1" covers S1A, S1 B and S1-East.</summary>
    [MaxLength(40)] public string ClassLevel { get; set; } = string.Empty;
    public int Term { get; set; }
    public int Order { get; set; }
    [MaxLength(200)] public string? Theme { get; set; }
    [MaxLength(200)] public string Topic { get; set; } = string.Empty;
    [MaxLength(1000)] public string? SubTopics { get; set; }
    public int? Periods { get; set; }
    [MaxLength(2000)] public string? Competency { get; set; }
    [MaxLength(4000)] public string? Outcomes { get; set; }
    [MaxLength(4000)] public string? Activities { get; set; }
    [MaxLength(2000)] public string? Assessment { get; set; }
}

public record CurriculumDto
{
    public List<CurriculumTopicDto> Topics { get; set; } = new();
}

public record CurriculumImportResultDto
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public List<string> Refused { get; set; } = new();
}

// ---- Reports ---------------------------------------------------------------------------------------------------------

public record PlanCoverageRowDto
{
    public string Group { get; set; } = string.Empty;
    public int LessonsTaught { get; set; }
    public int WithApprovedPlan { get; set; }
    public int WithAnyPlan { get; set; }
    public double? CoveragePercent { get; set; }
}

public record PlanReviewStatsDto
{
    public int Reviewed { get; set; }
    public int ReviewedWithComment { get; set; }
    public double? MedianHoursToDecision { get; set; }
    public int Returned { get; set; }
    public int Waiting { get; set; }
    public int WaitingOverTwoDays { get; set; }
}

/// <summary>Where one teacher's scheme for one subject stands this term. "Not started" is a row with no plan at all.</summary>
public enum SchemeProgress { NotStarted = 0, Draft = 1, WithHeadOfDepartment = 2, WithApprover = 3, Returned = 4, Approved = 5 }

public record SchemeStatusRowDto
{
    public Guid TeacherUserId { get; set; }
    public string TeacherName { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
    /// <summary>The classes the teacher is assigned in this subject.</summary>
    public List<string> ClassNames { get; set; } = new();
    public SchemeProgress Progress { get; set; }
    public Guid? PlanId { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedByName { get; set; }
    /// <summary>Submitted after the school's due week, or still not submitted once it has passed.</summary>
    public bool Late { get; set; }
}

public record SchemeSummaryDto
{
    public string? PeriodName { get; set; }
    public DateOnly? DueBy { get; set; }
    public int Expected { get; set; }
    public int NotStarted { get; set; }
    public int Draft { get; set; }
    public int WithHeadOfDepartment { get; set; }
    public int WithApprover { get; set; }
    public int Returned { get; set; }
    public int Approved { get; set; }
    public int Late { get; set; }
    public List<SchemeStatusRowDto> Rows { get; set; } = new();
}

/// <summary>One teacher's lesson plans over the report's window.</summary>
public record TeacherPlanRowDto
{
    public Guid TeacherUserId { get; set; }
    public string TeacherName { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
    public int LessonsTaught { get; set; }
    public int WithApprovedPlan { get; set; }
    public int Submitted { get; set; }
    public int Returned { get; set; }
    /// <summary>Lessons taught with no plan submitted at all.</summary>
    public int NoPlan { get; set; }
    public double? CoveragePercent { get; set; }
}

public record TeachingPlanReportDto
{
    /// <summary>Every teacher's scheme for every subject they teach, this term, with its state (2026-09-26).</summary>
    public SchemeSummaryDto Schemes { get; set; } = new();
    public List<TeacherPlanRowDto> ByTeacher { get; set; } = new();
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public List<PlanCoverageRowDto> ByDepartment { get; set; } = new();
    public List<PlanCoverageRowDto> BySubject { get; set; } = new();
    public PlanReviewStatsDto Review { get; set; } = new();
    public int SchemesExpected { get; set; }
    public int SchemesSubmitted { get; set; }
    public int SchemesApproved { get; set; }
}

public record RecordOfWorkRowDto
{
    public string? RowKey { get; set; }
    public int Week { get; set; }
    public string? Topic { get; set; }
    public string? SubTopic { get; set; }
    public int? PeriodsPlanned { get; set; }
    public int LessonsTaught { get; set; }
    public int LessonsMissed { get; set; }
    public List<DateOnly> TaughtOn { get; set; } = new();
    public string? Remarks { get; set; }
}

public record RecordOfWorkDto
{
    public Guid SchemeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public List<string> ClassNames { get; set; } = new();
    public string? PeriodName { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public List<RecordOfWorkRowDto> Rows { get; set; } = new();
}

// ---- Defaults and the rules both sides run -----------------------------------------------------------------------------

/// <summary>
/// The default templates, aligned to NCDC's lower-secondary competency-based format as far as it could be confirmed
/// (plan §2: the exact official field list was found only in secondary copies, so a school edits these).
/// </summary>
public static class TeachingPlanDefaults
{
    public const string TopicKey = "topic", SubTopicKey = "subtopic", CompetencyKey = "competency", OutcomesKey = "outcomes", ReflectionKey = "reflection";

    public static readonly IReadOnlyList<string> Phases = new[] { "Introduction", "Development", "Evaluation", "Conclusion" };

    public static readonly IReadOnlyList<PlanSectionDto> LessonPlanSections = new[]
    {
        new PlanSectionDto { Key = TopicKey, Title = "Topic", Required = true, System = true },
        new PlanSectionDto { Key = SubTopicKey, Title = "Sub-topic", System = true },
        new PlanSectionDto { Key = CompetencyKey, Title = "Competency", Hint = "From the syllabus for this topic.", System = true },
        new PlanSectionDto { Key = OutcomesKey, Title = "Learning outcomes", Hint = "By the end of the lesson, learners should be able to… Tag each (k), (u) or (s).", Required = true, System = true },
        new PlanSectionDto { Key = "skills", Title = "Generic skills", Kind = PlanSectionKind.MultiChoice,
            Choices = new() { "Critical thinking and problem-solving", "Creativity and innovation", "Communication", "Cooperation and self-direction", "Mathematical computation", "ICT proficiency" } },
        new PlanSectionDto { Key = "values", Title = "Values", Kind = PlanSectionKind.MultiChoice,
            Choices = new() { "Respect", "Honesty", "Responsibility", "Integrity", "Hard work", "Patriotism", "Unity", "Sharing" } },
        new PlanSectionDto { Key = "crosscutting", Title = "Cross-cutting issues", Kind = PlanSectionKind.MultiChoice,
            Choices = new() { "Environmental awareness", "Health awareness", "Life skills", "Mixed abilities and inclusion", "Financial literacy", "Patriotism", "Gender", "Human rights" } },
        new PlanSectionDto { Key = "prior", Title = "Prior knowledge" },
        new PlanSectionDto { Key = "materials", Title = "Learning materials", Hint = "Teaching and learning aids." },
        new PlanSectionDto { Key = "references", Title = "References" },
        new PlanSectionDto { Key = "procedure", Title = "Procedure", Kind = PlanSectionKind.Procedure, Required = true,
            Hint = "What you do and what the learners do, phase by phase." },
        new PlanSectionDto { Key = "assessment", Title = "Assessment", Hint = "How you will know the outcomes were met." },
        new PlanSectionDto { Key = ReflectionKey, Title = "Self-evaluation", Hint = "Written after the lesson: what worked, what to change.", AfterLesson = true, System = true },
    };

    public static readonly IReadOnlyList<PlanColumnDto> SchemeColumns = new[]
    {
        new PlanColumnDto { Key = TopicKey, Title = "Topic", Required = true, System = true },
        new PlanColumnDto { Key = SubTopicKey, Title = "Sub-topic", System = true },
        new PlanColumnDto { Key = CompetencyKey, Title = "Competency", System = true },
        new PlanColumnDto { Key = OutcomesKey, Title = "Learning outcomes", Required = true, System = true },
        new PlanColumnDto { Key = "methods", Title = "Methods and activities" },
        new PlanColumnDto { Key = "aids", Title = "Teaching aids" },
        new PlanColumnDto { Key = "references", Title = "References" },
        new PlanColumnDto { Key = "assessment", Title = "Assessment" },
        new PlanColumnDto { Key = "remarks", Title = "Remarks" },
    };

    public static IReadOnlyList<PlanSectionDto> SectionsOf(TeachingPlanSettingsDto s)
        => s.LessonPlanTemplate is { Count: > 0 } t ? t : LessonPlanSections;

    public static IReadOnlyList<string> PhasesOf(TeachingPlanSettingsDto s)
        => s.ProcedurePhases is { Count: > 0 } p ? p : Phases;

    public static IReadOnlyList<PlanColumnDto> ColumnsOf(TeachingPlanSettingsDto s)
        => s.SchemeColumns is { Count: > 0 } c ? c : SchemeColumns;
}

/// <summary>What the page refuses before sending and the server refuses again, in the same words.</summary>
public static class TeachingPlanRules
{
    /// <summary>The comparison key of a set of classes: each through <see cref="ClassName.Key"/>, sorted, joined.</summary>
    public static string ClassKey(IEnumerable<string> classNames)
        => string.Join("|", classNames.Select(ClassName.Key).Where(k => k.Length > 0).Distinct().OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>The problems that stop a plan being SUBMITTED. Empty = it may be submitted. A draft may be saved with any.</summary>
    public static List<string> SubmitProblems(TeachingPlanKind kind, LessonPlanContentDto? content, List<SchemeRowDto>? rows,
        IReadOnlyList<PlanSectionDto> sections, IReadOnlyList<PlanColumnDto> columns, bool hasFile)
    {
        var problems = new List<string>();
        if (kind == TeachingPlanKind.LessonPlan)
        {
            // A plan submitted as a file carries its own content; the form's required sections are for a typed plan.
            if (hasFile) return problems;
            content ??= new LessonPlanContentDto();
            foreach (var s in sections.Where(s => s.Required && !s.AfterLesson))
            {
                if (s.Kind == PlanSectionKind.Procedure)
                {
                    if (!content.Procedure.Any(p => !string.IsNullOrWhiteSpace(p.Teacher) || !string.IsNullOrWhiteSpace(p.Learner)))
                        problems.Add($"{s.Title}: write at least one step.");
                }
                else if (!content.Answers.TryGetValue(s.Key, out var v) || string.IsNullOrWhiteSpace(v))
                    problems.Add($"{s.Title} is required.");
            }
        }
        else
        {
            if (hasFile && (rows == null || rows.Count == 0)) return problems;
            if (rows == null || rows.Count == 0) problems.Add("Add at least one week.");
            else
            {
                foreach (var c in columns.Where(c => c.Required))
                {
                    var missing = rows.Where(r => !r.Cells.TryGetValue(c.Key, out var v) || string.IsNullOrWhiteSpace(v)).Select(r => r.Week).ToList();
                    if (missing.Count > 0) problems.Add($"{c.Title} is missing in week{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? "…" : "")}.");
                }
                var duplicateKeys = rows.GroupBy(r => r.Key).Where(g => g.Key.Length == 0 || g.Count() > 1).Any();
                if (duplicateKeys) problems.Add("Two lines share one key; reload the scheme and try again.");
            }
        }
        return problems;
    }

    /// <summary>Clamps a stored or submitted settings blob into the allowed bounds.</summary>
    public static TeachingPlanSettingsDto Clamp(TeachingPlanSettingsDto s) => s with
    {
        LessonPlanStages = s.LessonPlanStages is 1 or 2 ? s.LessonPlanStages : 1,
        DeadlineHourDayBefore = Math.Clamp(s.DeadlineHourDayBefore, 0, 23),
        SchemeDueWeek = Math.Clamp(s.SchemeDueWeek, 1, 6),
        LessonPlanTargetKb = Math.Clamp(s.LessonPlanTargetKb, 8, TeachingPlanLimits.CeilingLessonPlanKb),
        LessonPlanCapKb = Math.Clamp(Math.Max(s.LessonPlanCapKb, s.LessonPlanTargetKb), 8, TeachingPlanLimits.CeilingLessonPlanKb),
        LessonPlanMaxPages = Math.Clamp(s.LessonPlanMaxPages, 1, TeachingPlanLimits.CeilingPages),
        SchemeTargetKb = Math.Clamp(s.SchemeTargetKb, 16, TeachingPlanLimits.CeilingSchemeKb),
        SchemeCapKb = Math.Clamp(Math.Max(s.SchemeCapKb, s.SchemeTargetKb), 16, TeachingPlanLimits.CeilingSchemeKb),
        SchemeMaxPages = Math.Clamp(s.SchemeMaxPages, 1, TeachingPlanLimits.CeilingPages),
    };

    /// <summary>Checks an edited template: keys present, unique, short; every system key kept. Null = fine.</summary>
    public static string? TemplateProblem(IReadOnlyList<PlanSectionDto> sections, IReadOnlyList<PlanColumnDto> columns, IReadOnlyList<string> phases)
    {
        if (sections.Count == 0) return null;
        if (sections.Any(s => string.IsNullOrWhiteSpace(s.Key) || string.IsNullOrWhiteSpace(s.Title))) return "Every lesson plan section needs a key and a title.";
        if (sections.GroupBy(s => s.Key.Trim().ToLowerInvariant()).Any(g => g.Count() > 1)) return "Two lesson plan sections share a key.";
        foreach (var sys in TeachingPlanDefaults.LessonPlanSections.Where(s => s.System))
            if (!sections.Any(s => s.Key == sys.Key)) return $"The \"{sys.Title}\" section is filled by the product and cannot be removed; rename it instead.";
        if (sections.Count(s => s.Kind == PlanSectionKind.Procedure) > 1) return "A lesson plan has one procedure table.";
        if (columns.Count > 0)
        {
            if (columns.Any(c => string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.Title))) return "Every scheme column needs a key and a title.";
            if (columns.GroupBy(c => c.Key.Trim().ToLowerInvariant()).Any(g => g.Count() > 1)) return "Two scheme columns share a key.";
            foreach (var sys in TeachingPlanDefaults.SchemeColumns.Where(c => c.System))
                if (!columns.Any(c => c.Key == sys.Key)) return $"The \"{sys.Title}\" column is used to fill lesson plans and cannot be removed; rename it instead.";
        }
        if (phases.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 60)) return "Name every phase of the procedure (60 characters at most).";
        return null;
    }

    /// <summary>A plan's status in words.</summary>
    public static string StatusText(TeachingPlanStatus s) => s switch
    {
        TeachingPlanStatus.Draft => "Draft",
        TeachingPlanStatus.Submitted => "With head of department",
        TeachingPlanStatus.Forwarded => "With approver",
        TeachingPlanStatus.Approved => "Approved",
        TeachingPlanStatus.Returned => "Returned",
        TeachingPlanStatus.Withdrawn => "Withdrawn",
        _ => s.ToString()
    };
}
