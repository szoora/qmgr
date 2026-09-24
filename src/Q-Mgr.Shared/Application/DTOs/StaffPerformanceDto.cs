using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;
using QMgr.Domain.Identity;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// Staff Performance Monitor — the DTO contract between Q-Mgr.API and Q-Mgr.Web.
// Plan: docs/plans/STAFF_PERFORMANCE_MONITOR.md. One file per feature, flat, as every other DTO file.
//
// ROUTES (all [Authorize] + [RequireModule(ModuleCodes.StudentWelfare)]; branch routes call
// VerifyBranchOwnership; anything reaching a staff member other than the caller goes through
// IStaffScopeService and answers 404, never 403, when out of scope):
//
//   api/v1/staff/parameters                         GET (any) · POST · PUT {id} · PATCH {id}/toggle   [staff.parameters.manage]
//   api/v1/staff/policy                             GET (any) · PUT                                   [staff.parameters.manage]
//   api/v1/branches/{b}/staff/structure/departments GET · POST · PUT {id} · PATCH {id}/toggle          [view: staff.records.view; write: staff.structure.manage]
//   api/v1/branches/{b}/staff/structure/members     GET (directory) · PUT {userId}                     [view: staff.records.view; write: staff.structure.manage]
//   api/v1/branches/{b}/staff/structure/coverage    GET                                               [staff.structure.manage]
//   api/v1/branches/{b}/staff/records               GET (search, scoped) · POST                        [staff.records.view / staff.records.create]
//   api/v1/branches/{b}/staff/records/{id}          GET · POST notes · POST annul · PATCH visibility · PATCH points · POST finalize · POST attachments
//   api/v1/branches/{b}/staff/records/{id}/respond  POST (subject only) · POST acknowledge (subject only)
//   api/v1/branches/{b}/staff/members/{userId}/timeline   GET                                         [staff.records.view + scope, or self]
//   api/v1/branches/{b}/staff/members/{userId}/score      GET ?period=                                [staff.records.view + scope, or self]
//   api/v1/branches/{b}/staff/recognition           POST · GET budget                                 [staff.recognition.give]
//   api/v1/branches/{b}/staff/duties                GET ?from&to · POST · PUT {id} · DELETE {id} (cancel) · POST {id}/duplicate
//   api/v1/branches/{b}/staff/duties/{id}/register  GET · POST (submit; named recorder or staff.duties.manage) · PUT minutes
//   api/v1/branches/{b}/staff/notices               GET (mine) · GET manage · POST · PUT {id} · DELETE {id} · POST {id}/acknowledge · GET {id}/acknowledgements
//   api/v1/branches/{b}/staff/reports               GET ?period=                                      [staff.reports.view; carries ScopedToDepartments]
//   api/v1/branches/{b}/staff/appraisals            GET board?period= · POST open · GET {id} · PUT {id}/targets · POST {id}/self · POST {id}/review · POST {id}/moderate · POST {id}/sign · POST {id}/appeal · PUT {id}/report
//   api/v1/branches/{b}/staff/activity              GET ?page&pageSize&userId&action                  [staff.records.view, scoped; self through the portal]
//   api/v1/branches/{b}/staff/import-jobs           POST                                              [users.create + staff.structure.manage; refused for a scoped caller]
//   api/v1/staff/portal                             GET (hub) · GET records · GET activity · GET colleagues · GET export   [Authorize only — the caller's own]
//   api/v1/notifications                            GET ?eventKey&offset&limit · POST read-all?eventKey  (base product)
//
// SignalR: "StaffScoreUpdated" (StaffScoreUpdatedEvent) to user-{id} after any record about them is finalised.
// =====================================================================================================

// ---- Departments and structure ----------------------------------------------------------------------

public record DepartmentDto
{
    public Guid Id { get; init; }
    public Guid? BranchId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public Guid? HeadUserId { get; init; }
    public string? HeadName { get; init; }
    public Guid? DeputyHeadUserId { get; init; }
    public string? DeputyHeadName { get; init; }
    public int MemberCount { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
}

// Mutable — bound directly as a Blazor form model via @bind, same as CreateWelfareCategoryRequest.
public record SaveDepartmentRequest
{
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(20)] public string Code { get; set; } = string.Empty;
    public Guid? BranchId { get; set; }
    public Guid? HeadUserId { get; set; }
    public Guid? DeputyHeadUserId { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A member of staff as the module sees them: the User row plus the structure columns.</summary>
public record StaffMemberDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;

    /// <summary>
    /// What a list of people is sorted on — the name in the organisation's chosen SORT order, which
    /// may differ from how it is shown (PeopleNameSettingsDto.SortOrder). Built on the server by
    /// PersonNames.SortKey; a list sorts on this and falls back to the full name when it is absent.
    /// </summary>
    public string? SortName { get; init; }

    public string Email { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public string? RoleColor { get; init; }

    /// <summary>The person's photograph, already signed. Null when they have not added one.</summary>
    public string? PhotoUrl { get; init; }
    public string? JobTitle { get; init; }
    public string? EmployeeNumber { get; init; }
    public Guid? BranchId { get; init; }
    public List<Guid> DepartmentIds { get; init; } = new();
    public List<string> DepartmentNames { get; init; } = new();
    public Guid? LineManagerUserId { get; init; }
    public string? LineManagerName { get; init; }
    public string? StaffGroup { get; init; }
    public bool IsActive { get; init; }
    /// <summary>Filled on the directory: this period's band, or null when there is no evidence yet.</summary>
    public int? CurrentBand { get; init; }
    public string? CurrentBandName { get; init; }
    public decimal? CurrentComposite { get; init; }
    public DateTime? LastRecordAt { get; init; }
}

/// <summary>The staff directory, with the departments a scoped caller's view covers (empty for an unscoped caller).</summary>
public record StaffDirectoryDto
{
    public List<StaffMemberDto> Items { get; init; } = new();
    public List<string> ScopedToDepartments { get; init; } = new();
}

public record UpdateStaffStructureRequest
{
    public List<Guid> DepartmentIds { get; set; } = new();
    public Guid? LineManagerUserId { get; set; }
}

/// <summary>The staff-side coverage report: every way the structure fails silently, made visible.</summary>
public record StructureCoverageDto
{
    public string PeriodKey { get; init; } = string.Empty;
    public List<DepartmentDto> DepartmentsWithoutHead { get; init; } = new();
    public List<StaffMemberDto> StaffWithoutDepartment { get; init; } = new();
    public List<StaffMemberDto> StaffWithoutLineManager { get; init; } = new();
    public List<StaffMemberDto> StaffWithoutAppraiserThisPeriod { get; init; } = new();
    public List<StaffMemberDto> StaffWithoutObservationThisPeriod { get; init; } = new();
    public List<PerformanceParameterDto> ParametersWithNoRecordsThisPeriod { get; init; } = new();
}

// ---- Parameters -------------------------------------------------------------------------------------

public record PerformanceParameterDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ParameterKind Kind { get; init; }
    public string? AppliesToGroup { get; init; }
    public int? DefaultPoints { get; init; }
    public int MaxPointsPerEntry { get; init; }
    public int? MaxPointsPerPeriod { get; init; }
    public decimal Weight { get; init; }
    public int? RatingScale { get; init; }
    /// <summary>One descriptor per rubric level, lowest first. Empty when there is no scale.</summary>
    public List<string> Rubric { get; init; } = new();
    public WelfareVisibility DefaultVisibility { get; init; }
    public string Purpose { get; init; } = string.Empty;
    public string? Color { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
    public bool IsSystemSource { get; init; }
    /// <summary>The Attendance or Duty parameter a record here offsets as a recovered occasion (Lesson Recovery → Lesson Attendance).</summary>
    public Guid? OffsetsParameterId { get; init; }
}

public record SavePerformanceParameterRequest
{
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(1000)] public string? Description { get; set; }
    public ParameterKind Kind { get; set; } = ParameterKind.Contribution;
    public string? AppliesToGroup { get; set; }
    public int? DefaultPoints { get; set; }
    public int MaxPointsPerEntry { get; set; } = 10;
    public int? MaxPointsPerPeriod { get; set; }
    public decimal Weight { get; set; } = 1;
    public int? RatingScale { get; set; }
    public List<string> Rubric { get; set; } = new();
    public WelfareVisibility DefaultVisibility { get; set; } = WelfareVisibility.Standard;
    [MaxLength(300)] public string Purpose { get; set; } = string.Empty;
    [MaxLength(9)] public string? Color { get; set; }
    public int SortOrder { get; set; }
    public bool IsSystemSource { get; set; }
    public Guid? OffsetsParameterId { get; set; }
}

// ---- Records ----------------------------------------------------------------------------------------

public record StaffPerformanceRecordDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public Guid SubjectUserId { get; init; }
    public string SubjectName { get; init; } = string.Empty;
    public Guid ParameterId { get; init; }
    public string ParameterName { get; init; } = string.Empty;
    public ParameterKind ParameterKind { get; init; }
    public string? ParameterColor { get; init; }
    /// <summary>Why this was collected — the parameter's purpose, shown on the record's face (purpose limitation, DPPA s.3).</summary>
    public string ParameterPurpose { get; init; } = string.Empty;
    public Guid? DutyId { get; init; }
    public string? DutyTitle { get; init; }
    public DutyOutcome Outcome { get; init; }
    public int? Points { get; init; }
    public int? Rating { get; init; }
    public int? RatingScale { get; init; }
    public string Description { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public RecordSource Source { get; init; }
    public StaffRecordStatus Status { get; init; }
    public WelfareVisibility Visibility { get; init; }
    public Guid LoggedByUserId { get; init; }
    public string LoggedByName { get; init; } = string.Empty;
    public DateTime? AcknowledgedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>Logged more than the policy's late-entry threshold after it happened. Shown on the record's face.</summary>
    public bool IsLateEntry { get; init; }
    public List<StaffPerformanceNoteDto> Notes { get; init; } = new();
    public List<StaffPerformanceAttachmentDto> Attachments { get; init; } = new();
}

public record StaffPerformanceNoteDto
{
    public Guid Id { get; init; }
    public string Body { get; init; } = string.Empty;
    public Guid AuthorUserId { get; init; }
    public string AuthorName { get; init; } = string.Empty;
    public StaffNoteKind Kind { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record StaffPerformanceAttachmentDto
{
    public Guid Id { get; init; }
    public string FileUrl { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public Guid UploadedByUserId { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record CreateStaffRecordRequest
{
    public Guid SubjectUserId { get; set; }
    public Guid ParameterId { get; set; }
    public Guid? DutyId { get; set; }
    public DutyOutcome Outcome { get; set; } = DutyOutcome.NotApplicable;
    public int? Points { get; set; }
    public int? Rating { get; set; }
    [Required, MinLength(10), MaxLength(2000)] public string Description { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;
    public bool SaveAsDraft { get; set; }
    /// <summary>Observations: recorded as a note on the record and used to schedule nothing else.</summary>
    public DateTime? PreObservationMeetingAt { get; set; }
    public DateTime? FeedbackSessionAt { get; set; }
}

/// <summary>
/// The same Contribution or Conduct record for several members of staff at once — "everyone who ran the
/// open day", "the whole department missed the deadline" (POST …/staff/records/bulk, 2026-09-23).
/// <see cref="Record"/> carries everything but the person; its <c>SubjectUserId</c> is ignored.
/// </summary>
public record BulkStaffRecordRequest
{
    public List<Guid> SubjectUserIds { get; set; } = new();
    [Required] public CreateStaffRecordRequest Record { get; set; } = new();
}

public record BulkStaffRecordResultDto
{
    /// <summary>Records written — one per person.</summary>
    public int Created { get; init; }
    public List<Guid> RecordIds { get; init; } = new();
    /// <summary>Groups the batch on the staff activity log (one line, not forty).</summary>
    public Guid BatchId { get; init; }
    /// <summary>People told, after coalescing: each subject once, each supervisor once for the whole batch.</summary>
    public int PeopleAlerted { get; init; }
    /// <summary>The caller, when they were in the list: nobody logs a record about themselves in bulk.</summary>
    public List<Guid> SkippedSelf { get; init; } = new();
}

/// <summary>
/// The group log's limits and refusals, in ONE place so the dialog and the server say the same words —
/// the staff twin of <see cref="WelfareBulkLimits"/>.
/// </summary>
public static class StaffBulkLimits
{
    /// <summary>At most this many people in one group log. Synchronous and one transaction, so it is capped.</summary>
    public const int MaxPeople = 200;

    public const string CapMessage = "A record can be logged for at most 200 people at a time. Select fewer and log the rest separately.";

    public const string KindRefusal = "Only a contribution or a conduct record can be logged for a group. Attendance and duties come from registers, observations and wellbeing are about one person, and recognition has its own monthly allowance.";

    public const string VisibilityRefusal = "A confidential or restricted record is logged for one person at a time.";

    public const string DraftRefusal = "A record for a group cannot be saved as a draft.";

    public const string OnlySelfRefusal = "Nobody logs a record about themselves. Choose at least one other person.";

    /// <summary>The kinds a group log accepts. The dialog filters its parameter list by the same test.</summary>
    public static bool AllowsKind(ParameterKind kind) => kind is ParameterKind.Contribution or ParameterKind.Conduct;
}

public record AddStaffNoteRequest
{
    [Required, MinLength(2), MaxLength(4000)] public string Body { get; set; } = string.Empty;
}

public record AnnulStaffRecordRequest
{
    [Required, MinLength(5), MaxLength(1000)] public string Reason { get; set; } = string.Empty;
}

public record UpdateStaffRecordVisibilityRequest
{
    public WelfareVisibility Visibility { get; set; }
    [MaxLength(1000)] public string? Reason { get; set; }
}

public record CorrectStaffRecordPointsRequest
{
    public int? Points { get; set; }
    public int? Rating { get; set; }
    [Required, MinLength(5), MaxLength(1000)] public string Reason { get; set; } = string.Empty;
}

public record StaffRecordSearchResultDto
{
    public List<StaffPerformanceRecordDto> Items { get; init; } = new();
    public int TotalCount { get; init; }
    /// <summary>The departments these figures cover when the caller is scoped; empty for an unscoped caller.</summary>
    public List<string> ScopedToDepartments { get; init; } = new();
}

public record StaffTimelineDto
{
    public StaffMemberDto Subject { get; init; } = new();
    public List<StaffPerformanceRecordDto> Records { get; init; } = new();
    public StaffScoreDto? Score { get; init; }
    public List<ActivityEventDto> Activity { get; init; } = new();
    public bool IsSelf { get; init; }
}

// ---- Recognition ------------------------------------------------------------------------------------

public record GiveRecognitionRequest
{
    public Guid SubjectUserId { get; set; }
    public Guid ParameterId { get; set; }
    [Required, MinLength(10), MaxLength(1000)] public string Message { get; set; } = string.Empty;
}

public record RecognitionBudgetDto
{
    public int MonthlyBudget { get; init; }
    public int UsedThisMonth { get; init; }
    public int Remaining => Math.Max(0, MonthlyBudget - UsedThisMonth);
    public List<PerformanceParameterDto> Parameters { get; init; } = new();
}

// ---- Duties and registers ---------------------------------------------------------------------------

public record StaffDutyDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public Guid ParameterId { get; init; }
    public string ParameterName { get; init; } = string.Empty;
    public ParameterKind ParameterKind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Location { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
    /// <summary>Null means every active staff member in the branch.</summary>
    public List<Guid>? ExpectedUserIds { get; init; }
    public int ExpectedCount { get; init; }
    public List<Guid> RecorderUserIds { get; init; } = new();
    public List<string> RecorderNames { get; init; } = new();
    public DateTime? RegisterOpenedAt { get; init; }
    public DateTime? RegisterClosedAt { get; init; }
    public string? RegisterClosedByName { get; init; }
    public int MarkedCount { get; init; }
    public Guid? MinutesMediaContentId { get; init; }
    public string? MinutesFileUrl { get; init; }
    public Guid CreatedByUserId { get; init; }
    public bool IsActive { get; init; }
    /// <summary>For the caller: am I expected at this, may I take its register.</summary>
    public bool IsExpectedOfMe { get; init; }
    public bool CanIRecord { get; init; }
    public DutyOutcome? MyOutcome { get; init; }

    // ---- Duty rota (plan §4) ----
    public DutyKind Kind { get; init; }
    public Guid? SeriesId { get; init; }
    /// <summary>Set on the slots of a named (exam-supervision) series; null on a rota series.</summary>
    public string? SeriesName { get; init; }
    /// <summary>The people on duty, by name, in list order. Empty when the duty expects everyone.</summary>
    public List<string> ExpectedNames { get; init; } = new();
    public List<Guid> SupervisorUserIds { get; init; } = new();
    public List<string> SupervisorNames { get; init; } = new();
    public ReportCadence ReportCadence { get; init; }
    /// <summary>"18:00", branch-local; null uses the policy default.</summary>
    public string? ReportDueLocalTime { get; init; }
    public bool IsSupervisedByMe { get; init; }
    /// <summary>When the caller acknowledged this slot; null when they have not (or are not on it).</summary>
    public DateTime? MyAcknowledgedAt { get; init; }
    public int AcknowledgedCount { get; init; }
    /// <summary>
    /// Who has acknowledged, by user — only for a caller who manages duties or supervises the slot. A colleague on the
    /// same slot sees the count, not the names.
    /// </summary>
    public Dictionary<Guid, DateTime>? Acknowledgements { get; init; }

    // Lessons (duty rota plan §7). Null on a Session or Rota duty.
    public Guid? TimetableLessonId { get; init; }
    public string? ClassName { get; init; }
    public Guid? SubjectId { get; init; }
    public string? Room { get; init; }
    public Guid? RecoversDutyId { get; init; }
}

public record SaveStaffDutyRequest
{
    public Guid ParameterId { get; set; }
    [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
    [MaxLength(2000)] public string? Description { get; set; }
    [MaxLength(200)] public string? Location { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    /// <summary>Null or empty = everyone active in the branch. A rota slot must name its people.</summary>
    public List<Guid>? ExpectedUserIds { get; set; }
    public List<Guid> RecorderUserIds { get; set; } = new();

    /// <summary>Session (default) or Rota. Lessons come from the timetable and cannot be created here.</summary>
    public DutyKind Kind { get; set; } = DutyKind.Session;
    /// <summary>The administrator(s) on duty. Rota only; they are also the slot's recorders.</summary>
    public List<Guid> SupervisorUserIds { get; set; } = new();
    /// <summary>Null picks the policy default for the slot's length (plan §15 decision 1).</summary>
    public ReportCadence? ReportCadence { get; set; }
    /// <summary>"HH:mm"; null uses the policy default.</summary>
    [MaxLength(5)] public string? ReportDueLocalTime { get; set; }
}

// ---- Exam-supervision series (2026-09-23, plan TIMETABLE_OWNERSHIP §1 / Phase 3) ----------------------------------
// "Three timetables in a term" is one teaching timetable plus exam supervision, and exam supervision is a set of dated
// Session duties with invigilators, rooms and times — not a cycle of lessons. What it lacked was a name to group the
// slots under and an owner for the group. A series is the Session duties sharing a SeriesId; the name and the managers
// are carried on every row. Managers may add, change and cancel slots and take their registers without holding
// staff.duties.manage; appointing them is the permission holder's act alone, the timetable master rule.

public record DutySeriesDto
{
    public Guid SeriesId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid ParameterId { get; init; }
    public string ParameterName { get; init; } = string.Empty;
    public List<Guid> ManagerUserIds { get; init; } = new();
    public List<string> ManagerNames { get; init; } = new();
    public int SlotCount { get; init; }
    public DateTime? FirstStartsAt { get; init; }
    public DateTime? LastEndsAt { get; init; }
    /// <summary>Slots that have ended with their register still open — the thing a manager has to chase.</summary>
    public int RegistersOutstanding { get; init; }
    public bool IAmManager { get; init; }
    /// <summary>May the caller add, change and cancel slots (a manager, or a holder of staff.duties.manage).</summary>
    public bool CanWrite { get; init; }
    /// <summary>May the caller appoint managers and create series (the permission alone).</summary>
    public bool CanAppoint { get; init; }
}

public record DutySeriesDetailDto
{
    public DutySeriesDto Series { get; init; } = new();
    public List<StaffDutyDto> Slots { get; init; } = new();
}

/// <summary>One sitting of an exam: when, where, who invigilates.</summary>
public record DutySeriesSlotRequest
{
    /// <summary>"S.4 Mathematics Paper 1". Blank takes the series name.</summary>
    [MaxLength(200)] public string? Title { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    [MaxLength(200)] public string? Location { get; set; }
    /// <summary>The invigilators — the slot's expected people, marked on its register. At least one.</summary>
    public List<Guid> InvigilatorUserIds { get; set; } = new();
    [MaxLength(2000)] public string? Description { get; set; }
}

public record CreateDutySeriesRequest
{
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    /// <summary>Null takes the organization's "Exam Supervision" parameter.</summary>
    public Guid? ParameterId { get; set; }
    public List<Guid> ManagerUserIds { get; set; } = new();
    /// <summary>At least one: the name and the managers are carried on the slots, so a series cannot exist empty.</summary>
    public List<DutySeriesSlotRequest> Slots { get; set; } = new();
}

public record UpdateDutySeriesRequest
{
    [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
    /// <summary>Null leaves the managers as they are. A change is refused unless the caller holds staff.duties.manage.</summary>
    public List<Guid>? ManagerUserIds { get; set; }
}

/// <summary>What adding slots did, and what it noticed: an invigilator already somewhere else at that time is a
/// WARNING, never a refusal — a school may double up on purpose, and it is the person running the series who knows.</summary>
public record DutySeriesWriteResultDto
{
    public DutySeriesDetailDto Detail { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public record DuplicateStaffDutyRequest
{
    public DateTime NewStartsAt { get; set; }
}

public record RegisterRowDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;

    /// <summary>
    /// What a list of people is sorted on — the name in the organisation's chosen SORT order, which
    /// may differ from how it is shown (PeopleNameSettingsDto.SortOrder). Built on the server by
    /// PersonNames.SortKey; a list sorts on this and falls back to the full name when it is absent.
    /// </summary>
    public string? SortName { get; init; }

    /// <summary>Already signed. A register is read to recognise who is in the room.</summary>
    public string? PhotoUrl { get; init; }
    public string? JobTitle { get; init; }
    public string? DepartmentNames { get; init; }
    public DutyOutcome? Outcome { get; init; }
    public string? Note { get; init; }
    public Guid? RecordId { get; init; }
}

public record StaffRegisterDto
{
    public StaffDutyDto Duty { get; init; } = new();
    public List<RegisterRowDto> Rows { get; init; } = new();
}

public record RegisterEntryRequest
{
    public Guid UserId { get; set; }
    public DutyOutcome Outcome { get; set; }
    [MaxLength(500)] public string? Note { get; set; }
}

public record SubmitRegisterRequest
{
    public List<RegisterEntryRequest> Entries { get; set; } = new();
    /// <summary>Close the register after writing. A closed register can be reopened, which appends annulments and new rows rather than editing.</summary>
    public bool Close { get; set; } = true;
}

public record AttachMinutesRequest
{
    public Guid? MediaContentId { get; set; }
}

// ---- Notices ----------------------------------------------------------------------------------------

public record StaffNoticeDto
{
    public Guid Id { get; init; }
    public Guid? BranchId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string BodyHtml { get; init; } = string.Empty;
    public List<Guid>? AudienceDepartmentIds { get; init; }
    public List<string>? AudienceRoleCodes { get; init; }
    public string? AudienceStaffGroup { get; init; }
    public DateTime PublishAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public bool IsPinned { get; init; }
    public bool RequiresAcknowledgement { get; init; }
    public List<NoticeAttachmentDto> Attachments { get; init; } = new();
    public Guid PublishedByUserId { get; init; }
    public string PublishedByName { get; init; } = string.Empty;
    public DateTime? NotificationsSentAt { get; init; }
    public int RecipientCount { get; init; }
    public int AcknowledgedCount { get; init; }
    public DateTime? AcknowledgedByMeAt { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record NoticeAttachmentDto
{
    public Guid MediaContentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? FileUrl { get; init; }
}

public record SaveStaffNoticeRequest
{
    public Guid? BranchId { get; set; }
    [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
    [Required] public string BodyHtml { get; set; } = string.Empty;
    public List<Guid>? AudienceDepartmentIds { get; set; }
    public List<string>? AudienceRoleCodes { get; set; }
    public string? AudienceStaffGroup { get; set; }
    public DateTime PublishAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsPinned { get; set; }
    public bool RequiresAcknowledgement { get; set; }
    public List<Guid> AttachmentMediaContentIds { get; set; } = new();
}

public record NoticeAcknowledgementDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public DateTime? AcknowledgedAt { get; init; }
    public bool IsRead { get; init; }
}

// ---- Scoring ----------------------------------------------------------------------------------------

public record PerformancePeriodDto
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }

    /// <summary>
    /// The term's theme as the school writes it at the top of its programme ("Co-operation &amp; Dignity for Further
    /// Holistic Excellence"). Shown on the calendar and the printed term programme; carries no behaviour.
    /// </summary>
    public string? Theme { get; init; }
}

public record ScoreBandDto
{
    /// <summary>1..5, MoES's scale: 5 Excellent … 1 Poor.</summary>
    public int Rating { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal MinScore { get; init; }
}

public record ParameterScoreDto
{
    public Guid ParameterId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ParameterKind Kind { get; init; }
    public string? Color { get; init; }
    public decimal Weight { get; init; }
    /// <summary>0–100, or null when there was no evidence in the period (dropped from the denominator).</summary>
    public decimal? Score { get; init; }
    public int EvidenceCount { get; init; }
    public int Points { get; init; }
    public int Expected { get; init; }
    public int Present { get; init; }
    public int Late { get; init; }
    public int Absent { get; init; }
    public int Excused { get; init; }
    public int Recovered { get; init; }
    public decimal? MeanRating { get; init; }
    public int? RatingScale { get; init; }
}

public record ScoreTrendPointDto
{
    public DateOnly WeekStart { get; init; }
    public int Points { get; init; }
    public int Records { get; init; }
}

public record StaffScoreDto
{
    public Guid SubjectUserId { get; init; }
    public PerformancePeriodDto Period { get; init; } = new();
    public decimal? Composite { get; init; }
    public int? Band { get; init; }
    public string? BandName { get; init; }
    public int Points { get; init; }
    public int RecognitionReceived { get; init; }
    public int DutiesExpected { get; init; }
    public int DutiesAttended { get; init; }
    public List<ParameterScoreDto> Breakdown { get; init; } = new();
    public List<ScoreTrendPointDto> Trend { get; init; } = new();
    /// <summary>Private rank: filled only for the subject themselves (or when policy allows).</summary>
    public int? RankInBranch { get; init; }
    public int? RankedOutOf { get; init; }
}

/// <summary>Pushed over SignalR to user-{id} after any record about them is finalised.</summary>
public record StaffScoreUpdatedEvent(Guid UserId, string PeriodKey, decimal? Composite, int Points, int? Band);

// ---- Policy -----------------------------------------------------------------------------------------

/// <summary>
/// Organization.Settings["StaffPerformance"]. Read through IStaffPerformancePolicyService only —
/// never a second JSON reader.
/// </summary>
public record StaffPerformancePolicyDto
{
    /// <summary>Defined periods. When empty, the API derives Ugandan terms: T1 Feb–Apr, T2 May–Aug, T3 Sep–Dec.</summary>
    public List<PerformancePeriodDto> Periods { get; set; } = new();
    public List<ScoreBandDto> Bands { get; set; } = new()
    {
        new() { Rating = 5, Name = "Excellent", MinScore = 90 },
        new() { Rating = 4, Name = "Very Good", MinScore = 75 },
        new() { Rating = 3, Name = "Good", MinScore = 60 },
        new() { Rating = 2, Name = "Fair", MinScore = 45 },
        new() { Rating = 1, Name = "Poor", MinScore = 0 },
    };
    /// <summary>
    /// The staff groups this organization recognises — teaching, support, and whatever else it has
    /// (boarding, administration, ancillary). A parameter or a notice may be restricted to one.
    ///
    /// <para><b>A vocabulary, not an enum</b>, for the reasons on <see cref="StaffGroups"/>. The same
    /// <c>VocabularyItemDto</c> classes, houses, dormitories and rooms already use, so it brings
    /// <c>IsActive</c> (retire, never delete — removing a group in use would silently widen every
    /// parameter naming it to all staff) and <c>SortOrder</c> with it.</para>
    ///
    /// <para><b>Organization-scoped, unlike the branch vocabularies, and that is deliberate:</b>
    /// <c>PerformanceParameter</c> is organization-scoped, so a branch-scoped list would let a
    /// parameter name a group that exists on one branch and not the next — a person scoring
    /// differently depending on which branch read them. This blob also already writes under
    /// <c>WithPolicyLockAsync</c>, which <c>Branch.Settings</c> had to have bolted on.</para>
    ///
    /// <para>Empty means "not configured yet"; the reader seeds the two defaults.</para>
    /// </summary>
    public List<VocabularyItemDto> StaffGroups { get; set; } = new();

    /// <summary>
    /// How the school's staff are engaged — Permanent, Contract, or "Government-paid" and "PTA-paid" for a school
    /// whose MoES return separates them. <b>A vocabulary, not an enum</b>, for the reasons on
    /// <see cref="QMgr.Domain.Enums.EmploymentTypes"/>: nothing branches on a value. <c>User.EmploymentType</c> holds
    /// the NAME. Seeded on first read with the six the enum had. A type somebody holds is RETIRED, never removed —
    /// the server refuses the removal — because a stored name the list no longer carries reads as nothing at all.
    /// </summary>
    public List<VocabularyItemDto> EmploymentTypes { get; set; } = new();

    /// <summary>The names a person may be GIVEN now, in the school's order — what every picker, import preview and
    /// import job offers and resolves against. A retired type stays on the people who hold it but is not offered.</summary>
    public List<string> ActiveEmploymentTypeNames()
        => (EmploymentTypes ?? new()).Where(t => t.IsActive && !string.IsNullOrWhiteSpace(t.Name)).OrderBy(t => t.SortOrder).Select(t => t.Name).ToList();

    /// <summary>The MET ceiling: no single parameter above this share of the composite.</summary>
    public int MaxParameterWeightPercent { get; set; } = 50;

    // ---- The scoring dials (2026-09-22) ---------------------------------------------------------
    //
    // These three were hard-coded literals in StaffScoringService.ScoreParameter and are school
    // policy, not arithmetic. They are the honest answer to "must every parameter feature be
    // hard-coded?" — a school does NOT author a formula (a score that feeds an appraisal and an MoES
    // return has to be explainable, and a bad expression fails silently), but it absolutely decides
    // what a late arrival is worth. Defaults are exactly the previous literals, so no stored score
    // moves when this ships.

    /// <summary>
    /// What a late arrival is worth against a present one, in an Attendance or Duty parameter:
    /// <c>(present + Late×this + recovered) ÷ (present + late + absent)</c>.
    ///
    /// <para>0.5 was hard-coded. Some schools count three lates as an absence (≈0.67), some count
    /// late as absent outright (0). Clamped to 0–1 by the reader: a late worth more than a present
    /// would score somebody up for being late.</para>
    /// </summary>
    public decimal LateCreditFraction { get; set; } = 0.5m;

    /// <summary>
    /// How many entries a period a points parameter is assumed to attract when no
    /// <c>MaxPointsPerPeriod</c> is set, used to derive the cap it is scored against.
    /// <para>An invented "about ten a term" was hard-coded. Minimum 1.</para>
    /// </summary>
    public int DefaultEntriesPerPeriod { get; set; } = 10;

    /// <summary>
    /// Where a points parameter with evidence sits before that evidence pushes it either way —
    /// <c>NeutralScore + (100 − NeutralScore) × clamped ÷ cap</c> in the positive direction.
    /// <para>50 was hard-coded. Clamped to 0–100.</para>
    /// </summary>
    public decimal NeutralScore { get; set; } = 50m;
    public LeaderboardMode LeaderboardMode { get; set; } = LeaderboardMode.Private;
    public int LeaderboardTopN { get; set; } = 10;
    public int RecognitionMonthlyBudget { get; set; } = 5;
    public int DutyReminderLeadHours { get; set; } = 24;
    public int LateEntryThresholdDays { get; set; } = 14;
    public int ActivityAttributionRetentionDays { get; set; } = 365;
    public bool SystemAwardsEnabled { get; set; } = false;
    public DayOfWeek DigestWeekday { get; set; } = DayOfWeek.Monday;
    public int DigestHour { get; set; } = 6;
    public int SummaryHour { get; set; } = 6;
    public DateTime? LastSummarySentAt { get; set; }
    [MaxLength(200)] public string? DataProtectionOfficerContact { get; set; }
    /// <summary>
    /// Periods an approver has closed. Written ONLY by the close / reopen endpoints (the policy editor keeps
    /// whatever is stored, as it does LastSummarySentAt). A closed period takes no new scored records,
    /// registers, recognition, automatic credit, appraisal openings, annulments or point corrections;
    /// a visibility change is always allowed, because protecting a record must never be blocked.
    /// </summary>
    public List<ClosedPeriodDto> ClosedPeriods { get; set; } = new();

    // ---- Duty rota plan §3.1 (2026-09-17). The same one reader and one lock as everything above. ----

    /// <summary>
    /// The escalating reminder ladders (plan §4.2, §4.4, §7.2, §8.1). Empty in a stored blob means "the
    /// defaults"; <c>IStaffPerformancePolicyService.LadderFor</c> is the only reader.
    /// </summary>
    public List<ReminderLadderDto> ReminderLadders { get; set; } = new();
    public QuietHoursDto QuietHours { get; set; } = new();
    /// <summary>The tenant's duty report sections (plan §15 decision 1). Empty means the default template.</summary>
    public List<DutyReportSectionDto> DutyReportTemplate { get; set; } = new();
    public DutyReportDefaultsDto DutyReportDefaults { get; set; } = new();
    /// <summary>The tenant's minutes sections (2026-09-20). Empty means <see cref="MinutesTemplateDefaults"/>.</summary>
    public List<DutyReportSectionDto> MinutesTemplate { get; set; } = new();
    public MinutesDefaultsDto MinutesDefaults { get; set; } = new();
    public TeachingLoadNormsDto TeachingLoadNorms { get; set; } = new();
    /// <summary>Minutes before a lesson its in-app reminder goes (plan §7.2, decision 8: 10).</summary>
    public int LessonReminderMinutes { get; set; } = 10;
    /// <summary>Branch-local time of the morning "My Day" digest (plan §7.2: 06:30).</summary>
    public string MyDayLocalTime { get; set; } = "06:30";
    /// <summary>Days a missed lesson has to be recovered before it reads "not recovered" (plan §7.3).</summary>
    public int LessonRecoveryDeadlineDays { get; set; } = 14;
    /// <summary>Days a lesson may stay unrecorded before it goes to the supervisor's weekly analysis (plan §7.3).</summary>
    public int UnrecordedLessonWindowDays { get; set; } = 3;
    /// <summary>
    /// A subject teacher holding welfare.create may log a concern about a student they teach (plan §5.3,
    /// decision 5: on). Reporting a concern is not reading one: they keep what they wrote and nothing else.
    /// </summary>
    public bool SubjectTeachersMayLogConcerns { get; set; } = true;
    /// <summary>A student's learning-support need is shown to the teachers who teach them (plan §5.3). Off by default.</summary>
    public bool ShareLearningNeedsWithTeachingStaff { get; set; }

    /// <summary>
    /// What a member of staff may configure for themselves. It lives HERE rather than in
    /// Branch.Settings because this blob already has exactly one reader and one writer, both taking
    /// the organization lock — and Branch.Settings already has three writers and a standing warning
    /// about a fourth.
    ///
    /// Its own default has Enabled false, so a school that has never heard of this feature is not
    /// quietly running it.
    /// </summary>
    public SelfServicePolicyDto SelfService { get; set; } = new();
}

public record ClosedPeriodDto
{
    public string Key { get; set; } = string.Empty;
    public DateTime ClosedAt { get; set; }
    public Guid ClosedByUserId { get; set; }
    public string? ClosedByName { get; set; }
    /// <summary>Set when the period was closed with appraisals still unsigned: why that was right.</summary>
    [MaxLength(1000)] public string? OverrideReason { get; set; }
    public int UnsignedAppraisalsAtClose { get; set; }
}

public record ClosePeriodRequest
{
    /// <summary>Required when any appraisal for the period is not yet Signed.</summary>
    [MaxLength(1000)] public string? OverrideReason { get; set; }
}

public record ReopenPeriodRequest
{
    [Required, MinLength(5), MaxLength(1000)] public string Reason { get; set; } = string.Empty;
}

/// <summary>One period as the appraisal board and the policy page show it.</summary>
public record PeriodStatusDto
{
    public PerformancePeriodDto Period { get; init; } = new();
    public bool IsClosed { get; init; }
    public ClosedPeriodDto? Closure { get; init; }
    public int AppraisalCount { get; init; }
    public int UnsignedAppraisals { get; init; }
}

// ---- Reports ----------------------------------------------------------------------------------------

public record DepartmentScoreDto
{
    public Guid? DepartmentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int StaffCount { get; init; }
    public decimal? AverageComposite { get; init; }
    public decimal? AttendanceRate { get; init; }
    public int RecognitionCount { get; init; }
}

public record ParameterAggregateDto
{
    public Guid ParameterId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ParameterKind Kind { get; init; }
    public string? Color { get; init; }
    public int RecordCount { get; init; }
    public decimal? AverageScore { get; init; }
}

public record BandCountDto
{
    public int Rating { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
}

public record ObserverStatDto
{
    public Guid ObserverUserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Observations { get; init; }
    public decimal MeanRating { get; init; }
    public decimal SchoolMean { get; init; }
}

/// <summary>Two or more observers' ratings of the same lesson (plan §6.3), largest disagreement first.</summary>
public record ObservationPairDto
{
    public Guid SubjectUserId { get; init; }
    public string SubjectName { get; init; } = string.Empty;
    public string ParameterName { get; init; } = string.Empty;
    public int? RatingScale { get; init; }
    public DateOnly ObservedOn { get; init; }
    public string? DutyTitle { get; init; }
    public List<ObserverRatingDto> Ratings { get; init; } = new();
    /// <summary>Highest rating minus lowest. A spread of two or more on a four-level scale is worth a calibration conversation.</summary>
    public int Spread { get; init; }
}

public record ObserverRatingDto
{
    public Guid ObserverUserId { get; init; }
    public string ObserverName { get; init; } = string.Empty;
    public int Rating { get; init; }
}

public record LoggerCountDto
{
    public Guid UserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
    public Dictionary<string, int> ByKind { get; init; } = new();
}

public record LeaderboardRowDto
{
    public int Rank { get; init; }
    public Guid UserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? DepartmentName { get; init; }
    public decimal Composite { get; init; }
    public int Band { get; init; }
    public string BandName { get; init; } = string.Empty;
}

public record StaffReportsDto
{
    public PerformancePeriodDto Period { get; init; } = new();
    public List<string> ScopedToDepartments { get; init; } = new();
    public int StaffCount { get; init; }
    public int RecordCount { get; init; }
    public int RecognitionCount { get; init; }
    public int DutyCount { get; init; }
    public int RegistersNotTaken { get; init; }
    public Dictionary<string, int> AppraisalsByStage { get; init; } = new();
    public List<DepartmentScoreDto> ByDepartment { get; init; } = new();
    public List<ParameterAggregateDto> ByParameter { get; init; } = new();
    public List<BandCountDto> BandDistribution { get; init; } = new();
    public List<ScoreTrendPointDto> TrendByWeek { get; init; } = new();
    public List<ObserverStatDto> ObserverDispersion { get; init; } = new();
    /// <summary>Filled only for a reader who holds staff.confidential.view.</summary>
    public List<ObservationPairDto> ObservationPairs { get; init; } = new();
    public List<LoggerCountDto> WhoLogsWhat { get; init; } = new();
    /// <summary>Filled only when the tenant's LeaderboardMode allows it for this caller.</summary>
    public List<LeaderboardRowDto> Leaderboard { get; init; } = new();
    public LeaderboardMode LeaderboardMode { get; init; }
    public StructureCoverageDto Coverage { get; init; } = new();
}

// ---- Appraisals -------------------------------------------------------------------------------------

public record AppraisalTargetDto
{
    [Required, MaxLength(500)] public string Text { get; set; } = string.Empty;
    public Guid? ParameterId { get; set; }
    public bool Achieved { get; set; }
}

public record ParameterRatingDto
{
    public Guid ParameterId { get; set; }
    public int Rating { get; set; }
}

public record SupportPlanItemDto
{
    [Required, MaxLength(300)] public string Gap { get; set; } = string.Empty;
    [Required, MaxLength(300)] public string Support { get; set; } = string.Empty;
    public DateOnly? ByWhen { get; set; }
}

public record StaffAppraisalDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public Guid SubjectUserId { get; init; }
    public string SubjectName { get; init; } = string.Empty;
    /// <summary>The subject's photograph, already signed.</summary>
    public string? SubjectPhotoUrl { get; init; }
    public string? SubjectDepartmentNames { get; init; }
    public string PeriodKey { get; init; } = string.Empty;
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public Guid AppraiserUserId { get; init; }
    public string AppraiserName { get; init; } = string.Empty;
    public Guid? ModeratorUserId { get; init; }
    public string? ModeratorName { get; init; }
    public AppraisalStage Stage { get; init; }
    public List<AppraisalTargetDto> Targets { get; init; } = new();
    public decimal? ComputedScore { get; init; }
    public List<ParameterScoreDto> ComputedBreakdown { get; init; } = new();
    /// <summary>The live score while the appraisal is open; equals the frozen one once signed.</summary>
    public StaffScoreDto? LiveScore { get; init; }
    public List<ParameterRatingDto> SelfRatings { get; init; } = new();
    public int? SelfRating { get; init; }
    public string? SelfComments { get; init; }
    public int? AppraiserRating { get; init; }
    public string? AppraiserComments { get; init; }
    public int? FinalRating { get; init; }
    public string? FinalRatingName { get; init; }
    public string? Strengths { get; init; }
    public string? DevelopmentAreas { get; init; }
    public List<SupportPlanItemDto> SupportPlan { get; init; } = new();
    public List<AppraisalTargetDto> NextTargets { get; init; } = new();
    public DateTime? SelfSubmittedAt { get; init; }
    public DateTime? AppraiserSubmittedAt { get; init; }
    public DateTime? ModeratedAt { get; init; }
    public string? ModerationReason { get; init; }
    public DateTime? SignedAt { get; init; }
    public string? SignedByName { get; init; }
    public string? AppealNote { get; init; }
    public Guid? ReportMediaContentId { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool CanISelfAssess { get; init; }
    public bool CanIAppraise { get; init; }
    public bool CanIModerate { get; init; }
    public bool CanIAppeal { get; init; }
    /// <summary>
    /// For a moderator: how THIS appraiser has rated everyone this period against how every appraiser in
    /// the branch has, index 0 = rating 1 … index 4 = rating 5. The MET "home field" check — an appraiser
    /// who rates everyone a 4 is visible before their 4 is signed. Empty for anyone else.
    /// </summary>
    public List<int> AppraiserRatingCounts { get; init; } = new();
    public List<int> SchoolRatingCounts { get; init; } = new();
    public decimal? AppraiserMeanRating { get; init; }
    public decimal? SchoolMeanRating { get; init; }
    /// <summary>Annual appraisals only: that year's termly appraisals for the same person, which the annual rating averages.</summary>
    public List<TermRatingDto> TermlyRollup { get; init; } = new();
    public decimal? TermlyAverage { get; init; }
    public bool IsAnnual { get; init; }
    public bool PeriodClosed { get; init; }
}

public record TermRatingDto
{
    public Guid AppraisalId { get; init; }
    public string PeriodKey { get; init; } = string.Empty;
    public string PeriodName { get; init; } = string.Empty;
    public AppraisalStage Stage { get; init; }
    public int? FinalRating { get; init; }
}

public record AppraisalBoardDto
{
    public PerformancePeriodDto Period { get; init; } = new();
    /// <summary>Whether the period is closed, by whom and with what override.</summary>
    public PeriodStatusDto? PeriodStatus { get; init; }
    public Dictionary<string, int> ByStage { get; init; } = new();
    public List<StaffAppraisalDto> Items { get; init; } = new();
    public List<string> ScopedToDepartments { get; init; } = new();
}

public record OpenAppraisalsRequest
{
    public string? PeriodKey { get; set; }
    /// <summary>Null = every active staff member in the branch without an appraisal for the period.</summary>
    public List<Guid>? SubjectUserIds { get; set; }
    public Guid? AppraiserUserIdOverride { get; set; }
}

public record SetAppraisalTargetsRequest
{
    public List<AppraisalTargetDto> Targets { get; set; } = new();
}

public record SubmitSelfAssessmentRequest
{
    public List<ParameterRatingDto> Ratings { get; set; } = new();
    [Range(1, 5)] public int SelfRating { get; set; }
    [MaxLength(4000)] public string? Comments { get; set; }
}

public record SubmitAppraiserReviewRequest
{
    [Range(1, 5)] public int Rating { get; set; }
    [MaxLength(4000)] public string? Comments { get; set; }
    [MaxLength(2000)] public string? Strengths { get; set; }
    [MaxLength(2000)] public string? DevelopmentAreas { get; set; }
    public List<SupportPlanItemDto> SupportPlan { get; set; } = new();
    public List<AppraisalTargetDto> NextTargets { get; set; } = new();
    public List<AppraisalTargetDto> Targets { get; set; } = new();
}

public record ModerateAppraisalRequest
{
    [Range(1, 5)] public int FinalRating { get; set; }
    [MaxLength(1000)] public string? Reason { get; set; }
    public bool SignNow { get; set; } = true;
}

public record AppealAppraisalRequest
{
    [Required, MinLength(10), MaxLength(2000)] public string Note { get; set; } = string.Empty;
}

public record AttachAppraisalReportRequest
{
    public Guid MediaContentId { get; set; }
}

// ---- Portal -----------------------------------------------------------------------------------------

public record PortalItemDto
{
    /// <summary>"welfare-action" | "register-due" | "appraisal" | "notice-ack" | "record-unread"</summary>
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public DateTime? DueAt { get; init; }
    public bool IsOverdue { get; init; }
    public string Url { get; init; } = string.Empty;
}

public record StaffPortalDto
{
    public StaffMemberDto Me { get; init; } = new();
    public Guid BranchId { get; init; }
    public StaffScoreDto Score { get; init; } = new();
    public List<StaffDutyDto> ComingUp { get; init; } = new();
    /// <summary>The next school events for the caller (same audience rule as My School Day), for "Coming up" beside the meetings.</summary>
    public List<SchoolEventDto> UpcomingEvents { get; init; } = new();
    /// <summary>Rota slots I am on or supervise, under way or starting within a fortnight (plan §10 "On duty" card).</summary>
    public List<StaffDutyDto> OnDuty { get; init; } = new();
    /// <summary>My duty reports whose period has started and that are still mine to write: drafts and returned ones (plan §10).</summary>
    public List<DutyReportSummaryDto> ReportsToWrite { get; init; } = new();
    public List<PortalItemDto> OpenItems { get; init; } = new();
    public List<StaffNoticeDto> Notices { get; init; } = new();
    public List<StaffPerformanceRecordDto> RecognitionReceived { get; init; } = new();
    public List<StaffPerformanceRecordDto> RecognitionGiven { get; init; } = new();
    public List<StaffPerformanceRecordDto> RecentRecords { get; init; } = new();
    public StaffAppraisalDto? Appraisal { get; init; }
    public RecognitionBudgetDto RecognitionBudget { get; init; } = new();
    public int UnacknowledgedRecords { get; init; }
    public LeaderboardMode LeaderboardMode { get; init; }
    /// <summary>Department averages (departments of three or more), when the mode is Department or Public.</summary>
    public List<DepartmentScoreDto> DepartmentBoard { get; init; } = new();
    /// <summary>The top-N board, only when the tenant has switched the mode to Public.</summary>
    public List<LeaderboardRowDto> Leaderboard { get; init; } = new();
    /// <summary>The first-sign-in checklist (duty rota plan §12.3). The portal shows it until every item is done.</summary>
    public OnboardingChecklistDto Onboarding { get; init; } = new();
}

public record StaffColleagueDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? JobTitle { get; init; }
    public string? DepartmentNames { get; init; }
}

/// <summary>Everything the module holds about the caller, for the subject-access export.</summary>
public record StaffFileExportDto
{
    public StaffMemberDto Me { get; init; } = new();
    public DateTime GeneratedAt { get; init; }
    public List<StaffPerformanceRecordDto> Records { get; init; } = new();
    public List<StaffAppraisalDto> Appraisals { get; init; } = new();
    public List<ActivityEventDto> Activity { get; init; } = new();
    public List<StaffNoticeDto> AcknowledgedNotices { get; init; } = new();
    public string? DataProtectionOfficerContact { get; init; }
}

// ---- Staff import -----------------------------------------------------------------------------------

public record StaffImportRow
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// The name EXACTLY as the file wrote it, when the sheet carried one combined name column rather
    /// than two. Kept for two reasons: the import log can show what the file actually said, and a
    /// caller that posts only this — an integration rather than the browser — is still legal, because
    /// the server splits it with the batch's NameOrder instead of refusing the row. See
    /// docs/plans/BULK_IMPORT_SYSTEM.md §4.1 and QMgr.Domain.Identity.PersonName.
    /// </summary>
    public string? FullName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Phone { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? JobTitle { get; set; }
    public string RoleCode { get; set; } = "teacher";
    /// <summary>Department codes, comma-separated in the file, split here.</summary>
    public List<string> DepartmentCodes { get; set; } = new();
    public string? LineManagerEmail { get; set; }
    /// <summary>This row's delivery, overriding the import's (plan §12.5). Null follows the import.</summary>
    public StaffImportDeliveryMode? DeliveryMode { get; set; }
    /// <summary>Classes this person is class teacher of, e.g. "S2A" or "S2A;S2B". Validated against the class vocabulary.</summary>
    public string? ClassTeacherOf { get; set; }
    /// <summary>Subjects taught, e.g. "MATH:S2A,S2B; PHY:S3A". Validated against the subject catalogue and the classes.</summary>
    public string? Teaches { get; set; }

    // ---- The staff record (2026-09-18) ---------------------------------------------------------
    // Every one optional: a bank importing counter staff fills none of them, a school filling its
    // MoES return fills most. The importer parses each with the shared validators in
    // ImportParsing, so "03/04/2026" means 3 April here exactly as it does in the welfare import.

    /// <summary>Date of appointment, day-first (03/04/2026 is 3 April).</summary>
    public string? StartDate { get; set; }

    /// <summary>Date they left, for importing a historical staff list that includes leavers.</summary>
    public string? EndDate { get; set; }

    /// <summary>Permanent, Contract, Probation, PartTime, Volunteer, Seconded — matched case-insensitively.</summary>
    public string? EmploymentType { get; set; }

    /// <summary>Highest qualification as the school records it, e.g. "Dip.Ed", "BSc Ed".</summary>
    public string? Qualification { get; set; }

    /// <summary>MoES teacher registration / licence number.</summary>
    public string? TeachingRegistrationNumber { get; set; }

    /// <summary>Day-first, as above.</summary>
    public string? DateOfBirth { get; set; }

    /// <summary>"F"/"Female", "M"/"Male", or "Other". Anything else is a row warning, not a refusal.</summary>
    public string? Sex { get; set; }

    /// <summary>National Identification Number.</summary>
    public string? NationalId { get; set; }

    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
}

public record StartStaffImportRequest
{
    public List<StaffImportRow> Rows { get; set; } = new();

    /// <summary>
    /// Which way round a combined name column was written, for any row carrying FullName and no
    /// first/last pair. There is no way to detect this from the data — "Abaho Jude" is surname-first
    /// in Kampala and given-name-first in Cork — so the person importing chooses it, and the choice
    /// travels with the batch so the server splits exactly as the preview showed.
    /// </summary>
    public NameOrder NameOrder { get; set; } = NameOrder.GivenFirst;

    /// <summary>
    /// What to do with somebody already on file. FALSE is the old behaviour and stays the default for
    /// any caller that does not say: a row whose email already exists is reported and left untouched.
    /// True updates their details from the file — never their role, their permissions, their branch or
    /// their password, which are not an import's to change (RoleAssignmentGuard owns the first three).
    /// The browser asks before sending; see docs/plans/BULK_IMPORT_SYSTEM.md §6.
    /// </summary>
    public bool UpdateExisting { get; set; }
    /// <summary>Kept for older callers: true means <see cref="StaffImportDeliveryMode.Invitation"/> when no mode is given.</summary>
    public bool SendInvites { get; set; } = true;
    /// <summary>How people get in. Null follows <see cref="SendInvites"/> (Invitation when true, no delivery when false).</summary>
    public StaffImportDeliveryMode? DeliveryMode { get; set; }
    /// <summary>
    /// Off by default. A temporary password the administrator typed for this batch, used instead of a
    /// generated one per person. Accepted only when it passes the password policy and the blocklist (so
    /// never "staff", the school's name or a username); it still expires in 72 hours with a forced change.
    /// </summary>
    public string? BatchTemporaryPassword { get; set; }
}

/// <summary>Asks which of these email addresses the organization already has. Read-only.</summary>
public record StaffImportPrecheckRequest
{
    public List<string> Emails { get; set; } = new();

    /// <summary>
    /// The rows themselves, when the caller has them. With these the answer can say WHAT this file
    /// would change about each person it lands on, and which names look like the same person entered
    /// twice — neither of which can be worked out from a list of keys. The comparison is made on the
    /// server by the same rule the import itself applies, so the preview and the import cannot
    /// disagree, and the answer names the FIELDS only: a colleague's stored details are never read
    /// back to the browser. A caller that sends only the keys still gets the plain "already here" count.
    /// </summary>
    public List<StaffImportRow> Rows { get; set; } = new();

    /// <summary>
    /// The school's own staff numbers, for the people in the file who have no email address — which
    /// on a Ugandan school roll is most of them. Without this half, "is this person already here?"
    /// has no answer at all for them.
    /// </summary>
    public List<string> EmployeeNumbers { get; set; } = new();

    /// <summary>
    /// Which way round a combined name is written in this file — the same value the import will be
    /// sent. The server never guesses it, and a name read the wrong way round lands a row on the
    /// wrong person, so a preview using a different order would answer about somebody else.
    /// </summary>
    public NameOrder NameOrder { get; set; } = NameOrder.GivenFirst;
}

/// <summary>One person the import would land on rather than create.</summary>
public record StaffImportExistingDto
{
    public string NormalizedEmail { get; init; } = string.Empty;

    /// <summary>Set when this person was matched by staff number rather than by address.</summary>
    public string? EmployeeNumber { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    /// <summary>A join request nobody has approved. Importing over one would quietly approve it.</summary>
    public bool PendingApproval { get; init; }

    /// <summary>
    /// What this file would overwrite, in the reader's words ("phone", "job title", "start date").
    /// Empty means they are already here and nothing in the file is different — worth saying, because
    /// "23 already here" reads as 23 decisions when 20 of them would change nothing at all.
    /// </summary>
    public List<string> Changes { get; init; } = new();
}

/// <summary>
/// The answer to "who of these already exists". The preview turns each into a per-row warning, so a
/// reader learns before committing that 11 of their 42 rows are people the system already has —
/// rather than reading it in the summary afterwards.
/// </summary>
public record StaffImportPrecheckDto
{
    public List<StaffImportExistingDto> Existing { get; init; } = new();

    /// <summary>How many of <see cref="Existing"/> this file would actually change something about.</summary>
    public int ChangedCount => Existing.Count(e => e.Changes.Count > 0);

    /// <summary>Names that look like one person entered twice. A question for the reader, never a refusal.</summary>
    public List<ImportPossibleDuplicateDto> PossibleDuplicates { get; init; } = new();
}
