using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// Duty rota, duty reports, subjects, the timetable and lessons — the DTO contract between Q-Mgr.API and
// Q-Mgr.Web. Plan: docs/plans/DUTY_ROTA_AND_TIMETABLE.md. Onboarding lives in StaffOnboardingDto.cs.
// =====================================================================================================

// ---- Policy additions (plan §3.1: StaffPerformancePolicyDto gains these) ---------------------------------

/// <summary>What an escalating reminder is about (plan §8.1). Persisted inside the policy blob: append only.</summary>
public enum ReminderSubject
{
    /// <summary>A rota slot is approaching (plan §4.2).</summary>
    RotaStart = 0,
    /// <summary>A duty report has reached, or passed, its due time (plan §4.4).</summary>
    ReportDue = 1,
    /// <summary>A lesson is about to start (plan §7.2).</summary>
    LessonStart = 2,
    /// <summary>A published timetable has a new clash (plan §6.3).</summary>
    TimetableClash = 3,
    /// <summary>A Session duty (meeting, invigilation) is approaching — the pre-existing single-shot reminder, moved onto the ladder.</summary>
    SessionStart = 4,
    /// <summary>A duty has ended with its register not taken — the pre-existing chase, moved onto the ladder.</summary>
    RegisterChase = 5,
    /// <summary>An action point out of a meeting's minutes is approaching, or past, its date (2026-09-20).</summary>
    MinuteActionDue = 6,
    /// <summary>
    /// A published timetable is running out and nothing follows it (2026-09-22). Reaches the version's
    /// own named managers as well as the timetable masters — the point of appointing somebody is that the
    /// chase goes to them.
    /// </summary>
    TimetableExpiring = 7
}

/// <summary>Where a stage goes. Flags: a stage may ring the bell AND send an email.</summary>
[Flags]
public enum ReminderChannels
{
    None = 0,
    /// <summary>Only a line in the person's next digest (plan §4.2 stage 1).</summary>
    Digest = 1,
    Bell = 2,
    Email = 4,
    /// <summary>Only where the tenant has SMS on and the person allows it; never carries a student's name.</summary>
    Sms = 8
}

/// <summary>Who a stage reaches besides the person it is about.</summary>
[Flags]
public enum ReminderAudience
{
    /// <summary>The person on duty, the report's author, the teacher of the lesson.</summary>
    Subject = 1,
    /// <summary>The slot's administrator on duty (a to-do line), or a lesson supervisor.</summary>
    Supervisor = 2,
    /// <summary>Head and director of studies: a line in their daily digest.</summary>
    Heads = 4,
    /// <summary>Timetable masters (holders of timetable.manage).</summary>
    TimetableMasters = 8,
    /// <summary>
    /// The named managers of the version a reminder is about (2026-09-22). Distinct from
    /// <see cref="TimetableMasters"/> on purpose: an appointed manager may hold no permission at all, and a
    /// permission holder may own no version, so "the people whose job this is" cannot be derived from either
    /// one alone.
    /// </summary>
    TimetableManagers = 16
}

public record ReminderStageDto
{
    /// <summary>1-based, ascending; the row stores the highest stage sent.</summary>
    public int Stage { get; set; }
    /// <summary>Minutes relative to the anchor: negative before a start, positive after a due time.</summary>
    public int OffsetMinutes { get; set; }
    /// <summary>When set, the stage is sent at this branch-local hour on the day the offset lands ("1 day before, at the morning hour").</summary>
    public int? AtLocalHour { get; set; }
    public ReminderChannels Channels { get; set; } = ReminderChannels.Bell;
    public ReminderAudience Audience { get; set; } = ReminderAudience.Subject;
    /// <summary>May break quiet hours — by definition the next hour or two (plan §4.2 stage 4).</summary>
    public bool Interruptive { get; set; }
}

public record ReminderLadderDto
{
    public ReminderSubject Subject { get; set; }
    public List<ReminderStageDto> Stages { get; set; } = new();
}

public record QuietHoursDto
{
    public bool Enabled { get; set; } = true;
    /// <summary>Branch-local hour quiet time begins (default 20:00).</summary>
    public int StartHour { get; set; } = 20;
    /// <summary>Branch-local hour it ends and held stages go out (default 06:00).</summary>
    public int EndHour { get; set; } = 6;
    /// <summary>"The policy's morning hour" a stage may be pinned to (default 07:00).</summary>
    public int MorningHour { get; set; } = 7;
}

public enum DutyReportSectionKind { Text = 0, Choice = 1 }

public record DutyReportSectionDto
{
    [MaxLength(40)] public string Key { get; set; } = string.Empty;
    [MaxLength(100)] public string Title { get; set; } = string.Empty;
    [MaxLength(300)] public string? Hint { get; set; }
    public DutyReportSectionKind Kind { get; set; } = DutyReportSectionKind.Text;
    public List<string> Choices { get; set; } = new();
    public bool Required { get; set; }
}

public record DutyReportDefaultsDto
{
    /// <summary>Default report cadence for a slot of one day (plan §15 decision 1: daily).</summary>
    public ReportCadence DayLongCadence { get; set; } = ReportCadence.Daily;
    /// <summary>For a slot of about a week (decision 1: weekly).</summary>
    public ReportCadence WeekLongCadence { get; set; } = ReportCadence.Weekly;
    /// <summary>For a slot of about a month (decision 1: monthly).</summary>
    public ReportCadence MonthLongCadence { get; set; } = ReportCadence.Monthly;
    /// <summary>Branch-local due time of each report period ("18:00").</summary>
    public string DueLocalTime { get; set; } = "18:00";
    /// <summary>Plan §4.3 / decision 2: off — the teacher on duty does not read the supervisor's report about them.</summary>
    public bool OnDutyMayReadSupervisorReport { get; set; }
    /// <summary>Fairness warning: more rota slots than this in a term for one person (plan §4.1).</summary>
    public int MaxRotaSlotsPerTerm { get; set; } = 6;
}

public record TeachingLoadNormsDto
{
    /// <summary>MoES: 20–24 lessons a week.</summary>
    public int MinLessonsPerWeek { get; set; } = 20;
    public int MaxLessonsPerWeek { get; set; } = 24;
    /// <summary>The minimum for a teacher on both A- and O-Level (plan §15 decision 7: 18).</summary>
    public int MinLessonsPerWeekMixedLevel { get; set; } = 18;
    public int MaxPeriodsPerDay { get; set; } = 8;
    public int MaxConsecutivePeriods { get; set; } = 4;
}

// ---- Subjects (plan §5.2) --------------------------------------------------------------------------------

public record SubjectDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public Guid? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public string? Color { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
    /// <summary>Live subject-teacher assignments in the current branch.</summary>
    public int TeacherCount { get; init; }
}

public record SaveSubjectRequest
{
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(20)] public string Code { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    [MaxLength(9)] public string? Color { get; set; }
    public int SortOrder { get; set; }
}

public record AssignSubjectTeacherRequest
{
    [Required, MaxLength(100)] public string ClassName { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public Guid SubjectId { get; set; }
    [Range(0, 60)] public int? PeriodsPerWeek { get; set; }
}

/// <summary>
/// The planned periods on a live subject-teacher assignment — the only field that changes in place. Its own type
/// because the endpoint used to bind <see cref="AssignSubjectTeacherRequest"/>, whose [Required] ClassName made a
/// body of just the periods a 400 before the action ran.
/// </summary>
public record UpdateSubjectPeriodsRequest
{
    [Range(0, 60)] public int? PeriodsPerWeek { get; set; }
}

/// <summary>A teacher's teaching, for a profile and the portal: "Mathematics — S2A, S2B (12 periods)".</summary>
public record TeachingSummaryDto
{
    public Guid SubjectId { get; init; }
    public string SubjectName { get; init; } = string.Empty;
    public string SubjectCode { get; init; } = string.Empty;
    public List<string> Classes { get; init; } = new();
    public int PlannedPeriodsPerWeek { get; init; }
    public int SortOrder { get; init; }
    /// <summary>Periods the published timetable places for these assignments (0 until a timetable is published).</summary>
    public int TimetabledPeriodsPerWeek { get; init; }
}

// ---- The default reminder ladders ----------------------------------------------------------------------

/// <summary>
/// The plan's default ladders (§4.2, §4.4, §7.2, §8.1), in Shared so the API's
/// <c>IStaffPerformancePolicyService.LadderFor</c> and the policy editor's "restore defaults" read ONE copy —
/// an editor that showed a hand-typed twin would show a schedule the sweep does not run. Two derive from other
/// policy fields (the session lead hours, the lesson reminder minutes) until a tenant customises them.
/// </summary>
public static class ReminderLadderDefaults
{
    private const int Hour = 60, Day = 24 * 60;

    public static IReadOnlyList<ReminderLadderDto> For(StaffPerformancePolicyDto policy) => new List<ReminderLadderDto>
    {
        // Plan §4.2: before a rota slot. Stage 4 may break quiet hours; SMS only where the tenant and the person allow it.
        new() { Subject = ReminderSubject.RotaStart, Stages = new()
        {
            new() { Stage = 1, OffsetMinutes = -7 * Day, Channels = ReminderChannels.Digest },
            new() { Stage = 2, OffsetMinutes = -3 * Day, Channels = ReminderChannels.Bell },
            new() { Stage = 3, OffsetMinutes = -1 * Day, AtLocalHour = policy.QuietHours?.MorningHour ?? 7, Channels = ReminderChannels.Bell | ReminderChannels.Email },
            new() { Stage = 4, OffsetMinutes = -2 * Hour, Channels = ReminderChannels.Bell | ReminderChannels.Email | ReminderChannels.Sms, Audience = ReminderAudience.Subject | ReminderAudience.Supervisor, Interruptive = true },
        } },
        // Plan §4.4: after a report's due time, escalating to the supervisor and then the heads' digest.
        new() { Subject = ReminderSubject.ReportDue, Stages = new()
        {
            new() { Stage = 1, OffsetMinutes = 0, Channels = ReminderChannels.Bell },
            new() { Stage = 2, OffsetMinutes = 12 * Hour, Channels = ReminderChannels.Bell | ReminderChannels.Email },
            new() { Stage = 3, OffsetMinutes = 24 * Hour, Channels = ReminderChannels.Bell | ReminderChannels.Email, Audience = ReminderAudience.Subject | ReminderAudience.Supervisor },
            new() { Stage = 4, OffsetMinutes = 48 * Hour, Channels = ReminderChannels.Bell | ReminderChannels.Email, Audience = ReminderAudience.Subject | ReminderAudience.Supervisor | ReminderAudience.Heads },
        } },
        // Plan §7.2: one in-app, time-sensitive reminder before each lesson; email and SMS off by default.
        new() { Subject = ReminderSubject.LessonStart, Stages = new()
        {
            new() { Stage = 1, OffsetMinutes = -Math.Max(0, policy.LessonReminderMinutes), Channels = ReminderChannels.Bell, Interruptive = true },
        } },
        // Plan §6.3: a new clash in a published timetable, once, to the timetable masters.
        new() { Subject = ReminderSubject.TimetableClash, Stages = new()
        {
            new() { Stage = 1, OffsetMinutes = 0, Channels = ReminderChannels.Bell | ReminderChannels.Email, Audience = ReminderAudience.TimetableMasters },
        } },
        // The single-shot reminder Session duties always had (DutyReminderLeadHours), now a one-stage ladder that
        // also reaches recorders who are not expected (plan §2's first finding).
        new() { Subject = ReminderSubject.SessionStart, Stages = new()
        {
            new() { Stage = 1, OffsetMinutes = -Math.Max(1, policy.DutyReminderLeadHours) * Hour, Channels = ReminderChannels.Bell | ReminderChannels.Email },
        } },
        // The register chase: an hour after a duty ends with its register not taken, to its recorders.
        new() { Subject = ReminderSubject.RegisterChase, Stages = new()
        {
            new() { Stage = 1, OffsetMinutes = 1 * Hour, Channels = ReminderChannels.Bell | ReminderChannels.Email },
        } },
        // An action minuted for somebody: a day before it is due, then on the day, then once after.
        // Deliberately gentle and never Interruptive — an action point is a commitment, not an
        // emergency, and a ladder that shouts about one trains people to ignore the ones that matter.
        new() { Subject = ReminderSubject.MinuteActionDue, Stages = new()
        {
            new() { Stage = 1, OffsetMinutes = -1 * Day, AtLocalHour = policy.QuietHours?.MorningHour ?? 7, Channels = ReminderChannels.Bell },
            new() { Stage = 2, OffsetMinutes = 0, Channels = ReminderChannels.Bell | ReminderChannels.Email },
            new() { Stage = 3, OffsetMinutes = 3 * Day, Channels = ReminderChannels.Bell | ReminderChannels.Email },
        } },
        // A published timetable running out with nothing to follow it (2026-09-22). The offsets are BEFORE
        // EffectiveTo, and the reminder reaches the version's own managers first: a fortnight out is enough
        // notice to build the next one, three days out is a problem, and the day it ends the heads hear too.
        // Never Interruptive — a term ending is a date everybody already knows, not an emergency.
        new() { Subject = ReminderSubject.TimetableExpiring, Stages = new()
        {
            new() { Stage = 1, OffsetMinutes = -14 * Day, AtLocalHour = policy.QuietHours?.MorningHour ?? 7, Channels = ReminderChannels.Bell, Audience = ReminderAudience.TimetableManagers | ReminderAudience.TimetableMasters },
            new() { Stage = 2, OffsetMinutes = -3 * Day, AtLocalHour = policy.QuietHours?.MorningHour ?? 7, Channels = ReminderChannels.Bell | ReminderChannels.Email, Audience = ReminderAudience.TimetableManagers | ReminderAudience.TimetableMasters },
            new() { Stage = 3, OffsetMinutes = 0, Channels = ReminderChannels.Bell | ReminderChannels.Email, Audience = ReminderAudience.TimetableManagers | ReminderAudience.TimetableMasters | ReminderAudience.Heads },
        } },
    };
}

// ---- The duty rota (plan §4.1) -------------------------------------------------------------------------

/// <summary>The length of one generated slot.</summary>
public enum RotaSpan { Day = 0, Week = 1, Month = 2 }

/// <summary>What a rota check found. Warnings, never refusals (plan §4.1): the duty manager decides.</summary>
public enum RotaWarningKind
{
    /// <summary>The person is already on a rota slot that overlaps.</summary>
    OverlappingRota = 0,
    /// <summary>The person is expected at a Session duty (a meeting, an invigilation) during the slot.</summary>
    OverlappingSession = 1,
    /// <summary>The account is inactive.</summary>
    Inactive = 2,
    /// <summary>More rota slots this term than the policy's fairness limit.</summary>
    OverFairnessLimit = 3,
    /// <summary>The same person is both on duty and supervising the slot.</summary>
    SupervisesSelf = 4,
    /// <summary>The slot falls outside every defined term (a holiday).</summary>
    OutsideTerm = 5
}

public record RotaWarningDto
{
    public RotaWarningKind Kind { get; init; }
    public Guid? UserId { get; init; }
    public string? FullName { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// "Generate a rota" (plan §4.1): one slot per span from a start date, rotating an ordered list of staff (and,
/// optionally, of supervisors). With <see cref="Preview"/> nothing is written and the planned slots come back with
/// their warnings; without it the slots are written under one SeriesId.
/// </summary>
public record GenerateRotaRequest
{
    public Guid ParameterId { get; set; }
    [Required, MaxLength(200)] public string Title { get; set; } = "Teacher on duty";
    [MaxLength(200)] public string? Location { get; set; }
    public RotaSpan Span { get; set; } = RotaSpan.Week;
    /// <summary>The first slot's first day (branch-local date).</summary>
    public DateOnly StartDate { get; set; }
    [Range(1, 60)] public int Slots { get; set; } = 4;
    /// <summary>"HH:mm", branch-local: when duty starts on the first day of a slot (default 07:00).</summary>
    [MaxLength(5)] public string DayStartLocalTime { get; set; } = "07:00";
    /// <summary>"HH:mm", branch-local: when duty ends on the last day of a slot (default 18:00).</summary>
    [MaxLength(5)] public string DayEndLocalTime { get; set; } = "18:00";
    /// <summary>The staff to rotate through, in order. The list wraps.</summary>
    public List<Guid> StaffUserIds { get; set; } = new();
    [Range(1, 10)] public int PeoplePerSlot { get; set; } = 1;
    /// <summary>Administrators on duty to rotate through, in order. Empty = none.</summary>
    public List<Guid> SupervisorUserIds { get; set; } = new();
    [Range(0, 5)] public int SupervisorsPerSlot { get; set; } = 1;
    /// <summary>Null picks the policy default for the span.</summary>
    public ReportCadence? ReportCadence { get; set; }
    [MaxLength(5)] public string? ReportDueLocalTime { get; set; }
    /// <summary>Day slots only: skip Saturdays and Sundays (a boarding school leaves this off).</summary>
    public bool SkipWeekends { get; set; }
    /// <summary>Skip slots that start outside every term the policy defines (plan §4.1: "skipping school holidays").</summary>
    public bool SkipHolidays { get; set; } = true;
    public bool Preview { get; set; }
}

public record RotaSlotPreviewDto
{
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
    public List<Guid> UserIds { get; init; } = new();
    public List<string> Names { get; init; } = new();
    public List<Guid> SupervisorUserIds { get; init; } = new();
    public List<string> SupervisorNames { get; init; } = new();
    public List<RotaWarningDto> Warnings { get; init; } = new();
}

public record RotaGenerateResultDto
{
    /// <summary>Null on a preview.</summary>
    public Guid? SeriesId { get; init; }
    public bool Preview { get; init; }
    public List<RotaSlotPreviewDto> Slots { get; init; } = new();
    /// <summary>Slots not written because they fell on a skipped day or outside a term.</summary>
    public int Skipped { get; init; }
    public ReportCadence ReportCadence { get; init; }
}

/// <summary>Continues a series' rotation from its last slot (plan §4.1 "Extend").</summary>
public record ExtendRotaRequest
{
    [Range(1, 60)] public int Slots { get; set; } = 4;
    public bool Preview { get; set; }
}

/// <summary>
/// Two people exchange rota slots (plan §4.1). Carried out by the duty manager — that is the approval — as two
/// expected-list edits in one transaction, one activity event, and a notice to both and to the supervisors.
/// </summary>
public record SwapRotaRequest
{
    public Guid FirstDutyId { get; set; }
    public Guid FirstUserId { get; set; }
    public Guid SecondDutyId { get; set; }
    public Guid SecondUserId { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
}

/// <summary>Warnings for a slot being edited, before it is saved.</summary>
public record CheckRotaSlotRequest
{
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public List<Guid> UserIds { get; set; } = new();
    public List<Guid> SupervisorUserIds { get; set; } = new();
    /// <summary>The slot itself, when editing, so it does not overlap with itself.</summary>
    public Guid? ExcludeDutyId { get; set; }
}

/// <summary>The fairness strip (plan §4.1): rota slots per person this term, against the policy's limit.</summary>
/// <summary>What cancelling a rota did: upcoming slots cancelled, slots under way ended now, finished ones kept.</summary>
public record RotaCancelResult(int Cancelled, int Ended, int Kept);

public record RotaFairnessDto
{
    public string PeriodKey { get; init; } = string.Empty;
    public string PeriodName { get; init; } = string.Empty;
    public int Limit { get; init; }
    public List<RotaFairnessRowDto> Rows { get; init; } = new();
}

public record RotaFairnessRowDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public int OnDutySlots { get; init; }
    public int SupervisingSlots { get; init; }
    public int Acknowledged { get; init; }
    public bool OverLimit { get; init; }
}

// ---- Duty reports (plan §4.3, §4.4) ----------------------------------------------------------------------

/// <summary>A report in a list: the review queue, the portal's "Reports to write", a slot's reports.</summary>
public record DutyReportSummaryDto
{
    public Guid Id { get; init; }
    public Guid DutyId { get; init; }
    public string DutyTitle { get; init; } = string.Empty;
    public Guid AuthorUserId { get; init; }
    public string AuthorName { get; init; } = string.Empty;
    public DutyReportAuthorRole AuthorRole { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public DateTime DueAt { get; init; }
    public DutyReportStatus Status { get; init; }
    /// <summary>Not submitted and past its due time.</summary>
    public bool IsOverdue { get; init; }
    /// <summary>Submitted after its due time.</summary>
    public bool SubmittedLate { get; init; }
    public DateTime? SubmittedAt { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public int CommentCount { get; init; }
    public WelfareVisibility Visibility { get; init; }
}

public record DutyReportNoteDto
{
    public Guid Id { get; init; }
    public Guid AuthorUserId { get; init; }
    public string AuthorName { get; init; } = string.Empty;
    public DutyReportNoteKind Kind { get; init; }
    public string Body { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    /// <summary>For a Return: the sections as they were when the report was sent back.</summary>
    public Dictionary<string, string>? SnapshotSections { get; init; }
    public string? SnapshotSummary { get; init; }
}

/// <summary>A linked welfare record the CALLER may see. Anything else is only counted (<see cref="DutyReportDto.HiddenLinkedCount"/>).</summary>
public record DutyReportLinkedRecordDto
{
    public Guid Id { get; init; }
    public Guid StudentId { get; init; }
    public string StudentName { get; init; } = string.Empty;
    public string CategoryName { get; init; } = string.Empty;
    public string CaseType { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
}

public record DutyReportDto : DutyReportSummaryDto
{
    public Guid BranchId { get; init; }
    public DateTime DutyStartsAt { get; init; }
    public DateTime DutyEndsAt { get; init; }
    public List<string> SupervisorNames { get; init; } = new();
    public List<DutyReportSectionDto> Template { get; init; } = new();
    public Dictionary<string, string> Sections { get; init; } = new();
    public string? Summary { get; init; }
    public List<DutyReportLinkedRecordDto> LinkedRecords { get; init; } = new();
    public int HiddenLinkedCount { get; init; }
    public List<Guid> LinkedWelfareRecordIds { get; init; } = new();
    public string? ReviewedByName { get; init; }
    public List<DutyReportNoteDto> Notes { get; init; } = new();
    public List<StaffPerformanceAttachmentDto> Attachments { get; init; } = new();

    // What the caller may do.
    public bool CanEdit { get; init; }
    public bool CanComment { get; init; }
    public bool CanRespond { get; init; }
    public bool CanReview { get; init; }
    public bool CanMarkNoDuty { get; init; }
}

/// <summary>Saving a draft. Mutable: bound as a form model.</summary>
public record SaveDutyReportRequest
{
    public Dictionary<string, string> Sections { get; set; } = new();
    [MaxLength(8000)] public string? Summary { get; set; }
    public List<Guid> LinkedWelfareRecordIds { get; set; } = new();
    /// <summary>Standard or Confidential.</summary>
    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;
}

/// <summary>A note, return reason or review remark. Not [Required]: "Mark reviewed" takes no text, and a required body would
/// answer 400 before the action could refuse an author with 403. Each action checks the body it needs.</summary>
public record DutyReportNoteRequest
{
    [MaxLength(4000)] public string Body { get; set; } = string.Empty;
}

/// <summary>The review queue (plan §4.3): the reports the caller may read, with the figures a head reads first.</summary>
public record DutyReportQueueDto
{
    public List<DutyReportSummaryDto> Items { get; init; } = new();
    public int Total { get; init; }
    /// <summary>Of the reports now due, the share submitted by their due time. Null when none were due.</summary>
    public int? OnTimePercent { get; init; }
    public int Overdue { get; init; }
    public int AwaitingReview { get; init; }
    public int Returned { get; init; }
    /// <summary>Departments a scoped caller's view covers; empty when unscoped.</summary>
    public List<string> ScopedToDepartments { get; init; } = new();
}

/// <summary>
/// The default duty report sections (plan §15 decision 1), in Shared so the API's
/// <c>IStaffPerformancePolicyService.ReportTemplate</c> and the policy editor read one copy.
/// </summary>
public static class DutyReportTemplateDefaults
{
    public static IReadOnlyList<DutyReportSectionDto> Sections => new List<DutyReportSectionDto>
    {
        new() { Key = "arrival", Title = "Arrival and assembly", Hint = "Punctuality of learners and staff, how assembly went.", Required = true },
        new() { Key = "attendance", Title = "Attendance", Hint = "Learners and staff absent or late, and anything unusual." },
        new() { Key = "meals", Title = "Meals", Hint = "Breakfast, lunch and supper: served on time, enough, any complaints." },
        new() { Key = "cleanliness", Title = "Cleanliness", Kind = DutyReportSectionKind.Choice, Choices = new() { "Good", "Fair", "Poor" }, Hint = "Classrooms, compound, dormitories, latrines." },
        new() { Key = "boarding", Title = "Boarding (if any)", Hint = "Prep, roll call, lights out, dormitory issues." },
        new() { Key = "incidents", Title = "Incidents", Hint = "Link a welfare or discipline record for anything about a child — do not describe children here." },
        new() { Key = "recommendations", Title = "Recommendations", Hint = "What the administration should act on." },
    };
}

// ---- The timetable (plan §6) ----------------------------------------------------------------------------

public enum BellPeriodKind
{
    /// <summary>A teaching period a lesson may be placed in.</summary>
    Lesson = 0,
    /// <summary>Break or lunch: drawn across the week, never a slot.</summary>
    Break = 1,
    /// <summary>Assembly, registration: not a teaching period.</summary>
    Assembly = 2
}

public record BellPeriodDto
{
    /// <summary>Stable within the bell schedule ("P3", "BRK1"): what a lesson stores. Renaming a key strands the lessons on it.</summary>
    [MaxLength(20)] public string Key { get; set; } = string.Empty;
    [MaxLength(40)] public string Label { get; set; } = string.Empty;
    /// <summary>Branch-local "HH:mm".</summary>
    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "08:40";
    public BellPeriodKind Kind { get; set; } = BellPeriodKind.Lesson;
}

/// <summary>A set of teaching days sharing one bell schedule: "Monday to Friday", "Saturday morning".</summary>
public record BellDayTypeDto
{
    [MaxLength(40)] public string Key { get; set; } = string.Empty;
    [MaxLength(60)] public string Name { get; set; } = string.Empty;
    public List<DayOfWeek> Days { get; set; } = new();
    public List<BellPeriodDto> Periods { get; set; } = new();
}

/// <summary>A teacher who cannot be timetabled then (plan §6.2, declared availability). A null period means the whole day.</summary>
public record TeacherUnavailabilityDto
{
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
    /// <summary>1-based cycle day.</summary>
    public int CycleDay { get; set; } = 1;
    [MaxLength(20)] public string? PeriodKey { get; set; }
    [MaxLength(200)] public string? Note { get; set; }

    /// <summary>
    /// Why, from a SHORT LIST rather than free text. "Hospital appointment, Thursdays" typed into the
    /// note above is health information about a member of staff, and once it exists it has to be
    /// gated, retained and eventually blanked like any other. A category tells the timetable
    /// everything it needs and tells a casual reader nothing it does not.
    /// </summary>
    public UnavailabilityReason Reason { get; set; } = UnavailabilityReason.Unspecified;

    /// <summary>
    /// True when the teacher declared it themselves rather than the timetable master recording it.
    /// It is what lets a self-service write replace ONLY the caller's own lines while leaving every
    /// line the master entered exactly as it was.
    /// </summary>
    public bool DeclaredBySelf { get; set; }

    /// <summary>Stamped by the server. A teacher cannot backdate their own declaration.</summary>
    public DateTime? DeclaredAt { get; set; }
}

/// <summary>
/// Branch.Settings["Timetable"], read and written through ITimetableSettingsService only (plan §3.1).
/// The teaching days are the weekdays the day types cover, in week order; a cycle is one week or two (A/B).
/// </summary>
public record TimetableSettingsDto
{
    /// <summary>1 = a week; 2 = a two-week A/B cycle (plan §15 decision 6).</summary>
    public int CycleWeeks { get; set; } = 1;
    public int DefaultPeriodMinutes { get; set; } = 40;
    public List<BellDayTypeDto> DayTypes { get; set; } = new();
    public List<TeacherUnavailabilityDto> Unavailability { get; set; } = new();

    /// <summary>
    /// Per-teacher soft preferences, beside the unavailability above and in the same blob for the
    /// same reason: TimetableChecker already loads this to do its work, so they cost no extra query
    /// and they are written under the one lock this column has.
    /// </summary>
    public List<StaffTeachingPreferenceDto> Preferences { get; set; } = new();
    /// <summary>False when nothing is stored yet and these are the defaults on offer.</summary>
    public bool IsSaved { get; set; }
}

public record TimetableDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string PeriodKey { get; init; } = string.Empty;
    public string? PeriodName { get; init; }
    public int CycleDays { get; init; }
    public TimetableStatus Status { get; init; }
    public DateTime? PublishedAt { get; init; }
    public string? PublishedByName { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly EffectiveTo { get; init; }
    public int LessonCount { get; init; }
    public DateTime CreatedAt { get; init; }

    // ---- Ownership (2026-09-22). See TimetableAccess for the one write rule these describe. ----

    /// <summary>The appointed timetable master(s) of THIS version. Empty means nobody is named and only the permission opens it.</summary>
    public List<Guid> ManagerUserIds { get; init; } = new();

    /// <summary>Their names, in the same order. Filled for anybody who may read the version.</summary>
    public List<string> ManagerNames { get; init; } = new();

    /// <summary>True when the reader is one of them.</summary>
    public bool IAmManager { get; init; }

    /// <summary>
    /// True when the reader may write this version — as a named manager, or through <c>timetable.manage</c>.
    /// </summary>
    public bool CanIWrite { get; init; }

    /// <summary>
    /// True when the reader may write it ONLY through the permission, and somebody else is named. Every
    /// write they make is recorded as an override and told to the named managers, so the UI says so
    /// BEFORE they touch it rather than after.
    /// </summary>
    public bool WouldBeOverride { get; init; }

    /// <summary>True when the reader may appoint managers: <c>timetable.manage</c>, never a manager themselves.</summary>
    public bool CanIAppoint { get; init; }
}

/// <summary>
/// Appoint the managers of one version. Sent by a holder of <c>timetable.manage</c> only — a named manager
/// may not add or remove managers, including themselves, or the appointment is self-serve and the control
/// is decoration. Same asymmetry as RoleAssignmentGuard.
///
/// The list REPLACES. An empty list removes every manager, which is legitimate: the version goes back to
/// being openable by the permission alone.
/// </summary>
public record SetTimetableManagersRequest
{
    public List<Guid> UserIds { get; set; } = new();
}

public record TimetableLessonDto
{
    public Guid Id { get; init; }
    public int CycleDay { get; init; }
    public string PeriodKey { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public Guid SubjectId { get; init; }
    public string SubjectName { get; init; } = string.Empty;
    public string SubjectCode { get; init; } = string.Empty;
    public string? SubjectColor { get; init; }
    public Guid TeacherUserId { get; init; }
    public string TeacherName { get; init; } = string.Empty;
    public string? Room { get; init; }
    public Guid? GroupId { get; init; }
}

public enum TimetableIssueSeverity { Hard = 0, Soft = 1 }

/// <summary>What the clash checker found (plan §6.2), in the XHSTT vocabulary: by constraint, by resource. Append only.</summary>
public enum TimetableIssueKind
{
    // Hard: never publishable.
    TeacherDoubleBooked = 0,
    ClassDoubleBooked = 1,
    RoomDoubleBooked = 2,
    TeacherUnavailable = 3,
    TeacherOnSessionDuty = 4,
    OutsideBellSchedule = 5,
    TeacherInactive = 6,
    TeacherNotAssigned = 7,
    UnknownClass = 8,
    UnknownRoom = 9,
    SubjectRetired = 10,
    // Soft: shown, and acknowledged with a note to publish.
    OverWeeklyMaximum = 20,
    UnderWeeklyMinimum = 21,
    OverDailyMaximum = 22,
    TooManyConsecutive = 23,
    SubjectTwiceInDay = 24,
    IdleGap = 25,
    PlannedNotPlaced = 26
}

public record TimetableIssueDto
{
    /// <summary>Short and stable for the same breach across runs: what the integrity sweep compares.</summary>
    public string Key { get; init; } = string.Empty;
    public TimetableIssueKind Kind { get; init; }
    public TimetableIssueSeverity Severity { get; init; }
    /// <summary>"teacher", "class" or "room".</summary>
    public string Resource { get; init; } = string.Empty;
    /// <summary>The user id, normalised class name or normalised room name.</summary>
    public string ResourceKey { get; init; } = string.Empty;
    public string ResourceName { get; init; } = string.Empty;
    public int? CycleDay { get; init; }
    public string? PeriodKey { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<Guid> LessonIds { get; init; } = new();
}

/// <summary>A class-subject-teacher's planned periods and how many are placed (plan §6.2's side list).</summary>
public record UnplacedLoadDto
{
    public Guid AssignmentId { get; init; }
    public Guid TeacherUserId { get; init; }
    public string TeacherName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public Guid SubjectId { get; init; }
    public string SubjectName { get; init; } = string.Empty;
    public string SubjectCode { get; init; } = string.Empty;
    /// <summary>Per week.</summary>
    public int Planned { get; init; }
    /// <summary>Per week: an A/B cycle's placements are halved.</summary>
    public decimal Placed { get; init; }
}

public record TimetableDiagnosisDto
{
    public int HardCount { get; init; }
    public int SoftCount { get; init; }
    public List<TimetableIssueDto> Issues { get; init; } = new();
    public List<UnplacedLoadDto> Unplaced { get; init; } = new();
}

public record TimetableDetailDto
{
    public TimetableDto Timetable { get; init; } = new();
    public TimetableSettingsDto Settings { get; init; } = new();
    public List<TimetableLessonDto> Lessons { get; init; } = new();
    /// <summary>Only for a timetable master.</summary>
    public TimetableDiagnosisDto? Diagnosis { get; init; }

    /// <summary>
    /// True when this caller may write the version. Since 2026-09-22 that is the ownership rule, not the
    /// permission alone: see <see cref="TimetableDto.CanIWrite"/> and <see cref="TimetableDto.WouldBeOverride"/>.
    /// </summary>
    public bool CanManage { get; init; }

    /// <summary>Cover and cancellations falling inside the version's dates from today on. Empty for a draft.</summary>
    public List<TimetableExceptionDto> Exceptions { get; init; } = new();
}

public record CreateTimetableRequest
{
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    /// <summary>The term; defaults to the one covering today.</summary>
    [MaxLength(20)] public string? PeriodKey { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    /// <summary>Copy every lesson of this version: a change to a published timetable is a new draft from it.</summary>
    public Guid? CopyFromTimetableId { get; set; }

    /// <summary>
    /// The appointed master(s) of the new version. Null INHERITS from the version copied from — a corrected
    /// draft is the same person's job — and null with nothing copied leaves it unowned, which is how every
    /// version behaved before ownership existed and is the safe default.
    ///
    /// Deliberately NOT defaulted to the creator: that would make every version owned, and then every other
    /// permission holder's ordinary write would become an override with a notification behind it. Appointing is
    /// a deliberate act.
    /// </summary>
    public List<Guid>? ManagerUserIds { get; set; }
}

public record PlaceLessonRequest
{
    public int CycleDay { get; set; }
    [Required, MaxLength(20)] public string PeriodKey { get; set; } = string.Empty;
    [Required, MaxLength(100)] public string ClassName { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public List<Guid> TeacherUserIds { get; set; } = new();
    [MaxLength(60)] public string? Room { get; set; }
    /// <summary>More streams taught together in the same lesson (a joint lesson): one group, no clash with itself.</summary>
    public List<string> AlsoClassNames { get; set; } = new();
}

public record MoveLessonRequest
{
    public int CycleDay { get; set; }
    [Required, MaxLength(20)] public string PeriodKey { get; set; } = string.Empty;
    [MaxLength(60)] public string? Room { get; set; }
}

public record LessonChangeResultDto
{
    public List<TimetableLessonDto> Lessons { get; init; } = new();
    public TimetableDiagnosisDto Diagnosis { get; init; } = new();
}

public record PublishTimetableRequest
{
    /// <summary>Soft clashes are published only when acknowledged, with a note (plan §6.2).</summary>
    public bool AcknowledgeSoftClashes { get; set; }
    [MaxLength(500)] public string? Note { get; set; }

    /// <summary>
    /// ARCHIVE A LIVE TIMETABLE THAT COVERS THE SAME DATES. Off by default since 2026-09-22, and that is the
    /// point: publishing used to archive every overlapping version SILENTLY, and because every lesson query
    /// filters on Published over today's date, that stopped every register in the school. Nobody reading
    /// "published" expects the other one to have stopped.
    ///
    /// Re-publishing a corrected version is the genuine case, so the path stays — it just has to be asked for.
    /// </summary>
    public bool Replace { get; set; }
}

// ---- One-day departures from the published timetable (2026-09-22) -----------------------------------------

/// <summary>
/// One lesson, one date, one departure. See <see cref="LessonExceptionKind"/> for why there are only two
/// kinds and why a one-off swap is two covers.
/// </summary>
public record TimetableExceptionDto
{
    public Guid Id { get; init; }
    public Guid TimetableId { get; init; }
    public Guid TimetableLessonId { get; init; }

    /// <summary>The branch-local date it applies to.</summary>
    public DateOnly Date { get; init; }

    public LessonExceptionKind Kind { get; init; }

    /// <summary>Who teaches it instead. Always set for a Cover, never for a Cancelled.</summary>
    public Guid? CoverUserId { get; init; }
    public string? CoverUserName { get; init; }

    /// <summary>Whose lesson it normally is, so a list reads without joining back to the timetable.</summary>
    public Guid TeacherUserId { get; init; }
    public string TeacherName { get; init; } = string.Empty;

    public int CycleDay { get; init; }
    public string PeriodKey { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string? SubjectName { get; init; }
    public string? Room { get; init; }

    [MaxLength(300)] public string? Reason { get; init; }

    /// <summary>The self-service request it came out of, when it was not entered directly by a master.</summary>
    public Guid? SourceRequestId { get; init; }

    public string? CreatedByName { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>True when the reader may withdraw it: the version's write rule, or the person who arranged it.</summary>
    public bool CanIWithdraw { get; init; }

    /// <summary>A sentence a person can read without opening anything.</summary>
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// Record a cover or a cancellation directly — the timetable master's own path, beside the self-service one.
/// It names a LESSON and a DATE, never a teacher and a time, so it cannot describe something that is not on
/// the timetable.
/// </summary>
public record CreateLessonExceptionRequest
{
    public Guid TimetableLessonId { get; set; }
    public DateOnly Date { get; set; }
    public LessonExceptionKind Kind { get; set; }

    /// <summary>Required for a Cover, refused for a Cancelled.</summary>
    public Guid? CoverUserId { get; set; }

    [MaxLength(300)] public string? Reason { get; set; }
}

// ---- Lessons in the day (plan §7) ------------------------------------------------------------------------

/// <summary>
/// Where a lesson stands, derived from its duty, its register record and any recovery lesson (plan §7.3). Never
/// stored: "unrecorded" and "not recovered" are facts about time passing, not marks anybody made.
/// </summary>
public enum LessonStatus
{
    /// <summary>Not started yet.</summary>
    Scheduled = 0,
    /// <summary>Started or over, with no flag. Shown as unrecorded, never silently as taught or missed.</summary>
    Unrecorded = 1,
    /// <summary>Taught, confirmed by a lesson supervisor (or marked by one).</summary>
    Taught = 2,
    /// <summary>The teacher's own "taught", waiting for a supervisor to confirm.</summary>
    TaughtSelfReported = 3,
    /// <summary>The teacher's own "not taught", waiting for a supervisor to say with or without permission.</summary>
    NotTaughtSelfReported = 4,
    /// <summary>Missed with permission (Excused): outside the attendance denominator.</summary>
    MissedWithPermission = 5,
    /// <summary>Missed without permission (Absent).</summary>
    MissedWithoutPermission = 6,
    /// <summary>Missed, and a recovery lesson for it is scheduled.</summary>
    RecoveryScheduled = 7,
    /// <summary>Missed, and its recovery lesson was taught.</summary>
    Recovered = 8,
    /// <summary>Missed with no taught recovery by the policy's deadline.</summary>
    NotRecovered = 9,
    /// <summary>Cancelled: a school event, or no longer on the published timetable.</summary>
    Cancelled = 10
}

public record LessonItemDto
{
    public Guid DutyId { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
    /// <summary>"P3", when the lesson came from the timetable.</summary>
    public string? PeriodLabel { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public Guid? SubjectId { get; init; }
    public string SubjectName { get; init; } = string.Empty;
    public string SubjectCode { get; init; } = string.Empty;
    public string? Room { get; init; }
    public Guid TeacherUserId { get; init; }
    public string TeacherName { get; init; } = string.Empty;
    public LessonStatus Status { get; init; }
    public DutyOutcome? Outcome { get; init; }
    public string? FlagNote { get; init; }
    public string? FlaggedByName { get; init; }
    public DateTime? FlaggedAt { get; init; }
    /// <summary>Set on a recovery lesson: the missed lesson it makes up for.</summary>
    public Guid? RecoversDutyId { get; init; }
    public string? RecoversText { get; init; }
    /// <summary>Set on a missed lesson with a recovery scheduled.</summary>
    public Guid? RecoveryDutyId { get; init; }
    public DateTime? RecoveryStartsAt { get; init; }
    /// <summary>A missed lesson must be recovered by then (the policy's deadline).</summary>
    public DateTime? RecoverBy { get; init; }
    public string? CancelReason { get; init; }
    /// <summary>The caller is the teacher and may mark Taught / Not taught.</summary>
    public bool CanSelfReport { get; init; }
    /// <summary>The caller is a lesson supervisor for this teacher.</summary>
    public bool CanFlag { get; init; }
    /// <summary>The caller may schedule its recovery (a missed lesson, none scheduled).</summary>
    public bool CanScheduleRecovery { get; init; }
}

/// <summary>A teacher's day (plan §7.2 "My Day"): today's lessons in order, and everything else that is theirs today.</summary>
public record MyDayDto
{
    public DateOnly Date { get; init; }
    public List<LessonItemDto> Lessons { get; init; } = new();
    public List<StaffDutyDto> Sessions { get; init; } = new();
    public List<StaffDutyDto> OnDuty { get; init; } = new();
    public List<DutyReportSummaryDto> ReportsDue { get; init; } = new();
    /// <summary>Missed lessons of mine not yet recovered, soonest deadline first.</summary>
    public List<LessonItemDto> RecoveryOwed { get; init; } = new();
    /// <summary>Earlier lessons of mine still unrecorded, within the policy's window.</summary>
    public List<LessonItemDto> Unrecorded { get; init; } = new();
    /// <summary>Welfare records with an action assigned to me and still open.</summary>
    public int WelfareActionsOwed { get; init; }
    public DateTime? NextLessonStartsAt { get; init; }
    /// <summary>False when no published timetable covers the date: an empty day then means "no timetable", not "no lessons".</summary>
    public bool HasTimetable { get; init; }
    /// <summary>
    /// School events on this date that the caller is part of: staff-audience events, and any event naming them or one
    /// of their departments as responsible (plan TERM_PROGRAMME_CALENDAR_AND_GATES §9). A whole-school event on a
    /// teaching day is shown here — lessons still run (decision D6).
    /// </summary>
    public List<SchoolEventDto> Events { get; init; } = new();
}

public record LessonListDto
{
    public List<LessonItemDto> Items { get; init; } = new();
    public int Total { get; init; }
    public int Scheduled { get; init; }
    public int Taught { get; init; }
    public int Unrecorded { get; init; }
    public int Missed { get; init; }
    public int Recovered { get; init; }
    public int NotRecovered { get; init; }
    public int SelfReportsToConfirm { get; init; }
    /// <summary>Non-empty when the caller's staff scope narrows the list (plan §13 "tell the client when its figures are scoped").</summary>
    public List<string> ScopedToDepartments { get; init; } = new();
    /// <summary>The caller sees only their own lessons.</summary>
    public bool OwnOnly { get; init; }
}

public record FlagLessonRequest
{
    /// <summary>Taught = Present (or Late); not taught = Absent; missed with permission = Excused (supervisors only).</summary>
    public DutyOutcome Outcome { get; set; }
    [MaxLength(1000)] public string? Note { get; set; }
}

public record ConfirmLessonsRequest
{
    public List<Guid> DutyIds { get; set; } = new();
}

public record ConfirmLessonsResultDto
{
    public int Confirmed { get; init; }
    public int Skipped { get; init; }
}

public record ScheduleRecoveryRequest
{
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    [MaxLength(60)] public string? Room { get; set; }
}

public record CancelLessonRequest
{
    [Required, MaxLength(500)] public string Reason { get; set; } = string.Empty;
}

// ---- Reports and analysis (plan §11) -----------------------------------------------------------------------

public enum LoadBand { NotTeaching = 0, Under = 1, Within = 2, Over = 3 }

public record TeachingLoadSubjectDto
{
    public string SubjectCode { get; init; } = string.Empty;
    public string SubjectName { get; init; } = string.Empty;
    public List<string> Classes { get; init; } = new();
    public int Planned { get; init; }
    public decimal Timetabled { get; init; }
}

/// <summary>A teacher's load (plan §11 "Teaching load"): planned against timetabled against the norm band.</summary>
public record TeachingLoadRowDto
{
    public Guid UserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Departments { get; init; }
    public int Planned { get; init; }
    public decimal Timetabled { get; init; }
    /// <summary>Teaches on both O-Level (S1–S4) and A-Level (S5–S6): the lower minimum applies.</summary>
    public bool MixedLevel { get; init; }
    public int Minimum { get; init; }
    public int Maximum { get; init; }
    public LoadBand Band { get; init; }
    public List<TeachingLoadSubjectDto> BySubject { get; init; } = new();
}

public record LoadAggregateDto
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Teachers { get; init; }
    public int Planned { get; init; }
    public decimal Timetabled { get; init; }
    public int Under { get; init; }
    public int Over { get; init; }
}

/// <summary>Lesson counts for one row of the lessons-taught analysis (MoES Annex 4).</summary>
public record LessonCountsDto
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    /// <summary>Past lessons that were not cancelled.</summary>
    public int Scheduled { get; init; }
    public int Taught { get; init; }
    public int MissedWithPermission { get; init; }
    public int MissedWithoutPermission { get; init; }
    public int Recovered { get; init; }
    public int NotRecovered { get; init; }
    public int Unrecorded { get; init; }
    /// <summary>
    /// (Taught + recovered) ÷ (recorded lessons that count), where missed-with-permission is outside the denominator (MoES:
    /// an organisational reason is not the teacher's gap) and unrecorded lessons are shown beside it, never counted either
    /// way. Null when nothing that counts was recorded.
    /// </summary>
    public decimal? TaughtPercent { get; init; }
}

/// <summary>One missed lesson on the Lesson Recovery Schedule (MoES Annex 5).</summary>
public record RecoveryScheduleRowDto
{
    public Guid DutyId { get; init; }
    public string TeacherName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string SubjectName { get; init; } = string.Empty;
    public DateTime MissedAt { get; init; }
    public string? Reason { get; init; }
    public DateTime? RecoveryAt { get; init; }
    public LessonStatus Status { get; init; }
}

/// <summary>A person's duty rota and duty report compliance this period (plan §11 "Duty rota").</summary>
public record RotaComplianceRowDto
{
    public Guid UserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int OnDutySlots { get; init; }
    public int SupervisedSlots { get; init; }
    public int Acknowledged { get; init; }
    public int AcknowledgedBeforeStart { get; init; }
    public int ReportsDue { get; init; }
    public int ReportsOnTime { get; init; }
    public int ReportsLate { get; init; }
    public int ReportsOverdue { get; init; }
    public int ReportsReviewed { get; init; }
    public int ReportsReturned { get; init; }
    /// <summary>As a supervisor: mean hours from a report's submission to its review.</summary>
    public decimal? ReviewTurnaroundHours { get; init; }
}

public record RoomUseDto
{
    public string Room { get; init; } = string.Empty;
    public int PeriodsUsed { get; init; }
    public int PeriodsAvailable { get; init; }
    public decimal Percent { get; init; }
}

public record ClashTrendPointDto
{
    public DateTime At { get; init; }
    public int Hard { get; init; }
    public int Soft { get; init; }
}

public record TimetableHealthDto
{
    public Guid? TimetableId { get; init; }
    public string? TimetableName { get; init; }
    public int Hard { get; init; }
    public int Soft { get; init; }
    public int UnplacedAssignments { get; init; }
    public decimal UnplacedPeriods { get; init; }
    public List<TimetableIssueDto> TopIssues { get; init; } = new();
    public List<RoomUseDto> Rooms { get; init; } = new();
    /// <summary>From the integrity sweep's daily check, newest last.</summary>
    public List<ClashTrendPointDto> Trend { get; init; } = new();
}

public record WeeklyLessonsDto
{
    public DateOnly WeekStart { get; init; }
    public LessonCountsDto Counts { get; init; } = new();
}

public record TeachingReportsDto
{
    public PerformancePeriodDto Period { get; init; } = new();
    public List<string> ScopedToDepartments { get; init; } = new();
    public TeachingLoadNormsDto Norms { get; init; } = new();
    public List<TeachingLoadRowDto> Load { get; init; } = new();
    public List<LoadAggregateDto> LoadByDepartment { get; init; } = new();
    public List<LoadAggregateDto> LoadBySubject { get; init; } = new();
    public List<LoadAggregateDto> LoadByLevel { get; init; } = new();
    public LessonCountsDto LessonsTotal { get; init; } = new();
    public List<LessonCountsDto> LessonsByTeacher { get; init; } = new();
    public List<LessonCountsDto> LessonsByDepartment { get; init; } = new();
    public List<LessonCountsDto> LessonsBySubject { get; init; } = new();
    public List<LessonCountsDto> LessonsByClass { get; init; } = new();
    public List<LessonCountsDto> LessonsByLevel { get; init; } = new();
    public List<WeeklyLessonsDto> LessonsByWeek { get; init; } = new();
    public List<RecoveryScheduleRowDto> RecoverySchedule { get; init; } = new();
    public List<RotaComplianceRowDto> Rota { get; init; } = new();
    public TimetableHealthDto? Timetable { get; init; }
}

/// <summary>The dashboard's Teaching tiles (plan §14 Phase 5): this week, in the caller's scope.</summary>
public record TeachingDashboardDto
{
    public decimal? TaughtPercentThisWeek { get; init; }
    public int LessonsThisWeek { get; init; }
    public int UnrecordedThisWeek { get; init; }
    public int NotRecovered { get; init; }
    public int DutyReportsOverdue { get; init; }
    /// <summary>Null for a caller who is not a timetable master.</summary>
    public int? HardClashes { get; init; }
    public List<string> ScopedToDepartments { get; init; } = new();
}

// ---- Timetable import (plan §6.2, Phase 6) ------------------------------------------------------------------

/// <summary>
/// One lesson from an aSc / FET / spreadsheet export. Every field is free text and resolved on the server: a day as
/// "Mon", "Monday", "Mon B" or the cycle-day number; a period as its key, its label or its start time ("08:00"); a
/// subject by code or name; a teacher by email, username or full name; a class and a room by name. Rows naming the same
/// day, period, subject and teacher for different classes become one joint lesson.
/// </summary>
public record TimetableImportRow
{
    [MaxLength(40)] public string Day { get; set; } = string.Empty;
    [MaxLength(40)] public string Period { get; set; } = string.Empty;
    [MaxLength(100)] public string Class { get; set; } = string.Empty;
    [MaxLength(100)] public string Subject { get; set; } = string.Empty;
    [MaxLength(200)] public string Teacher { get; set; } = string.Empty;
    [MaxLength(60)] public string? Room { get; set; }
}

public record StartTimetableImportRequest
{
    public List<TimetableImportRow> Rows { get; set; } = new();
    /// <summary>Remove the draft's lessons first, so the file is the whole timetable.</summary>
    public bool ReplaceExisting { get; set; }
    [MaxLength(200)] public string? SourceFileName { get; set; }
}

/// <summary>What the import job stores in RosterImportJob.RowsJson.</summary>
public record TimetableImportPayload
{
    public Guid TimetableId { get; set; }
    public bool ReplaceExisting { get; set; }
    public List<TimetableImportRow> Rows { get; set; } = new();
}

/// <summary>
/// A room as the timetable settings page edits it (duty rota plan §6.1). Stored in <c>BranchVocabulariesDto.Rooms</c>, whose
/// only writer is <c>PUT …/timetable/rooms</c>: the student lists editor round-trips the blob but keeps the stored rooms.
/// </summary>
public class TimetableRoomDto
{
    public string Name { get; set; } = string.Empty;
    public int? Capacity { get; set; }
    public string? RoomType { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>Lessons naming this room in a draft or the published timetable. Read-only; a room in use cannot be removed.</summary>
    public int LessonCount { get; set; }
}

public class UpdateTimetableRoomsRequest
{
    public List<TimetableRoomDto> Rooms { get; set; } = new();

    /// <summary>Old name → new name. A rename moves draft and published lessons and future lessons on the roster.</summary>
    public Dictionary<string, string> Renames { get; set; } = new();
}
