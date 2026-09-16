using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// Staff Performance Monitor — the DTO contract between Q-Mgr.API and Q-Mgr.Web.
// Plan: docs/plans/STAFF_PERFORMANCE_MONITOR.md. One file per feature, flat, as every other DTO file.
//
// ROUTES (all [Authorize] + [RequireModule(ModuleCodes.StaffPerformance)]; branch routes call
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
    public string Email { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public string? RoleColor { get; init; }
    public string? JobTitle { get; init; }
    public string? EmployeeNumber { get; init; }
    public Guid? BranchId { get; init; }
    public List<Guid> DepartmentIds { get; init; } = new();
    public List<string> DepartmentNames { get; init; } = new();
    public Guid? LineManagerUserId { get; init; }
    public string? LineManagerName { get; init; }
    public StaffGroup StaffGroup { get; init; }
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
    public StaffGroup AppliesTo { get; init; }
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
}

public record SavePerformanceParameterRequest
{
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(1000)] public string? Description { get; set; }
    public ParameterKind Kind { get; set; } = ParameterKind.Contribution;
    public StaffGroup AppliesTo { get; set; } = StaffGroup.AllStaff;
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
}

public record SaveStaffDutyRequest
{
    public Guid ParameterId { get; set; }
    [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
    [MaxLength(2000)] public string? Description { get; set; }
    [MaxLength(200)] public string? Location { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    /// <summary>Null or empty = everyone active in the branch.</summary>
    public List<Guid>? ExpectedUserIds { get; set; }
    public List<Guid> RecorderUserIds { get; set; } = new();
}

public record DuplicateStaffDutyRequest
{
    public DateTime NewStartsAt { get; set; }
}

public record RegisterRowDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
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
    public StaffGroup? AudienceStaffGroup { get; init; }
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
    public StaffGroup? AudienceStaffGroup { get; set; }
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
    /// <summary>The MET ceiling: no single parameter above this share of the composite.</summary>
    public int MaxParameterWeightPercent { get; set; } = 50;
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
}

public record AppraisalBoardDto
{
    public PerformancePeriodDto Period { get; init; } = new();
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
    public List<PortalItemDto> OpenItems { get; init; } = new();
    public List<StaffNoticeDto> Notices { get; init; } = new();
    public List<StaffPerformanceRecordDto> RecognitionReceived { get; init; } = new();
    public List<StaffPerformanceRecordDto> RecognitionGiven { get; init; } = new();
    public List<StaffPerformanceRecordDto> RecentRecords { get; init; } = new();
    public StaffAppraisalDto? Appraisal { get; init; }
    public RecognitionBudgetDto RecognitionBudget { get; init; } = new();
    public int UnacknowledgedRecords { get; init; }
    public LeaderboardMode LeaderboardMode { get; init; }
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
    public string Email { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Phone { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? JobTitle { get; set; }
    public string RoleCode { get; set; } = "teacher";
    /// <summary>Department codes, comma-separated in the file, split here.</summary>
    public List<string> DepartmentCodes { get; set; } = new();
    public string? LineManagerEmail { get; set; }
}

public record StartStaffImportRequest
{
    public List<StaffImportRow> Rows { get; set; } = new();
    public bool SendInvites { get; set; } = true;
}
