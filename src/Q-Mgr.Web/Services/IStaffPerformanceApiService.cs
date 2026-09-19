using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Web.Services;

/// <summary>
/// The typed client for every Staff Performance route (the contract is the header comment of
/// Q-Mgr.Shared/Application/DTOs/StaffPerformanceDto.cs). THROWS on failure with the server's own
/// message — the convention every Web API service here follows except IQueueApiService, whose
/// swallowed errors cost a real bug. A page wraps a call in try/catch and shows the reason.
/// </summary>
public interface IStaffPerformanceApiService
{
    // Parameters and policy (organization-wide)
    Task<List<PerformanceParameterDto>> GetParametersAsync(bool includeInactive = false);
    Task<PerformanceParameterDto> CreateParameterAsync(SavePerformanceParameterRequest request);
    Task<PerformanceParameterDto> UpdateParameterAsync(Guid id, SavePerformanceParameterRequest request);
    Task<PerformanceParameterDto> ToggleParameterAsync(Guid id);
    Task<StaffPerformancePolicyDto> GetPolicyAsync();
    Task<StaffPerformancePolicyDto> UpdatePolicyAsync(StaffPerformancePolicyDto policy);
    Task<PeriodStatusDto> GetPeriodStatusAsync(string periodKey);
    Task<PeriodStatusDto> ClosePeriodAsync(string periodKey, ClosePeriodRequest request);
    Task<PeriodStatusDto> ReopenPeriodAsync(string periodKey, ReopenPeriodRequest request);

    // Structure
    Task<List<DepartmentDto>> GetDepartmentsAsync(Guid branchId, bool includeInactive = false);
    Task<DepartmentDto> CreateDepartmentAsync(Guid branchId, SaveDepartmentRequest request);
    Task<DepartmentDto> UpdateDepartmentAsync(Guid branchId, Guid id, SaveDepartmentRequest request);
    Task<DepartmentDto> ToggleDepartmentAsync(Guid branchId, Guid id);
    Task<StaffDirectoryDto> GetDirectoryAsync(Guid branchId, string? period = null);

    // ---- The staff record (2026-09-19). Every field below has been on the User row since
    //      2026-09-18 and only the bulk import could write one; these are the surface that was
    //      missing. The read is staff.records.view plus the staff scope; the write is
    //      staff.structure.manage, because the employment half is an auditable MoES return.
    Task<StaffProfileDto> GetStaffProfileAsync(Guid branchId, Guid userId);
    Task<StaffProfileDto> UpdateStaffProfileAsync(Guid branchId, Guid userId, UpdateStaffProfileRequest request);

    /// <summary>Adds a member of staff from the directory, without a trip to Users &amp; Roles.</summary>
    Task<CreateStaffMemberResult> CreateStaffMemberAsync(Guid branchId, CreateStaffMemberRequest request);

    /// <summary>My own file. Self is always visible and is not scope — the ProfileController rule.</summary>
    Task<StaffProfileDto> GetMyProfileAsync();

    /// <summary>Maintains my own contact detail. Contact only; the employment half is the school's.</summary>
    Task<StaffProfileDto> UpdateMyContactAsync(UpdateStaffContactRequest request);
    Task<StaffMemberDto> UpdateMemberStructureAsync(Guid branchId, Guid userId, UpdateStaffStructureRequest request);
    Task<StructureCoverageDto> GetCoverageAsync(Guid branchId, string? period = null);

    // Subjects (duty rota plan §5.2). Organization-wide catalogue, read through a branch for its teacher counts.
    Task<List<SubjectDto>> GetSubjectsAsync(Guid branchId, bool includeInactive = false);
    Task<SubjectDto> CreateSubjectAsync(Guid branchId, SaveSubjectRequest request);
    Task<SubjectDto> UpdateSubjectAsync(Guid branchId, Guid id, SaveSubjectRequest request);
    Task<SubjectDto> ToggleSubjectAsync(Guid branchId, Guid id);

    // Records
    Task<StaffRecordSearchResultDto> SearchRecordsAsync(Guid branchId, Guid? subjectUserId = null, Guid? parameterId = null, DateTime? from = null, DateTime? to = null, string? status = null, string? q = null, int page = 1, int pageSize = 25);
    /// <summary>Throws <see cref="LateEntryConfirmationRequiredException"/> when the date is past the policy's late-entry threshold and <paramref name="acknowledgeLateEntry"/> is false.</summary>
    Task<StaffPerformanceRecordDto> CreateRecordAsync(Guid branchId, CreateStaffRecordRequest request, bool acknowledgeLateEntry = false);
    Task<StaffPerformanceRecordDto> GetRecordAsync(Guid branchId, Guid id);
    Task<StaffPerformanceRecordDto> FinalizeRecordAsync(Guid branchId, Guid id, bool acknowledgeLateEntry = false);
    Task<StaffPerformanceRecordDto> AddNoteAsync(Guid branchId, Guid id, AddStaffNoteRequest request);
    Task<StaffPerformanceRecordDto> RespondAsync(Guid branchId, Guid id, AddStaffNoteRequest request);
    Task<StaffPerformanceRecordDto> AcknowledgeAsync(Guid branchId, Guid id);
    Task<StaffPerformanceRecordDto> AnnulAsync(Guid branchId, Guid id, AnnulStaffRecordRequest request);
    Task<StaffPerformanceRecordDto> UpdateVisibilityAsync(Guid branchId, Guid id, UpdateStaffRecordVisibilityRequest request);
    Task<StaffPerformanceRecordDto> CorrectPointsAsync(Guid branchId, Guid id, CorrectStaffRecordPointsRequest request);
    Task<StaffTimelineDto> GetTimelineAsync(Guid branchId, Guid userId, DateTime? from = null, DateTime? to = null);
    Task<StaffScoreDto> GetScoreAsync(Guid branchId, Guid userId, string? period = null);
    Task<StaffPerformanceRecordDto> GiveRecognitionAsync(Guid branchId, GiveRecognitionRequest request);
    Task<RecognitionBudgetDto> GetRecognitionBudgetAsync(Guid branchId);

    // Duties
    Task<List<StaffDutyDto>> GetDutiesAsync(Guid branchId, DateTime from, DateTime to, DutyKind? kind = null);
    /// <summary>"Seen, I'm on duty" (plan §4.2). Only a person the rota slot names.</summary>
    Task<StaffDutyDto> AcknowledgeDutyAsync(Guid branchId, Guid id);

    // Duty rota (plan §4.1)
    Task<Guid> GetRotaParameterIdAsync(Guid branchId);
    Task<RotaGenerateResultDto> GenerateRotaAsync(Guid branchId, GenerateRotaRequest request);
    Task<RotaGenerateResultDto> ExtendRotaAsync(Guid branchId, Guid seriesId, ExtendRotaRequest request);
    Task<int> CancelRotaSeriesAsync(Guid branchId, Guid seriesId);
    Task SwapRotaAsync(Guid branchId, SwapRotaRequest request);
    Task<List<RotaWarningDto>> CheckRotaSlotAsync(Guid branchId, CheckRotaSlotRequest request);
    Task<RotaFairnessDto> GetRotaFairnessAsync(Guid branchId, string? period = null);

    // Duty reports (plan §4.3)
    Task<List<DutyReportSummaryDto>> GetMyDutyReportsAsync(Guid branchId, bool openOnly = false);
    Task<DutyReportQueueDto> GetDutyReportQueueAsync(Guid branchId, DateTime? from = null, DateTime? to = null, DutyReportStatus? status = null, Guid? dutyId = null);
    Task<DutyReportDto> GetDutyReportAsync(Guid branchId, Guid id);
    Task<DutyReportDto> SaveDutyReportAsync(Guid branchId, Guid id, SaveDutyReportRequest request);
    Task<DutyReportDto> SubmitDutyReportAsync(Guid branchId, Guid id, SaveDutyReportRequest request);
    Task<DutyReportDto> AddDutyReportNoteAsync(Guid branchId, Guid id, string body);
    Task<DutyReportDto> ReturnDutyReportAsync(Guid branchId, Guid id, string reason);
    Task<DutyReportDto> ReviewDutyReportAsync(Guid branchId, Guid id, string? remark);
    Task<DutyReportDto> ReopenDutyReportAsync(Guid branchId, Guid id, string reason);
    Task MarkNoDutyAsync(Guid branchId, Guid id, string reason);
    Task<List<DutyReportLinkedRecordDto>> GetLinkableWelfareRecordsAsync(Guid branchId);

    // Timetable (duty rota plan §6)
    Task<TimetableSettingsDto> GetTimetableSettingsAsync(Guid branchId);
    Task<TimetableSettingsDto> SaveTimetableSettingsAsync(Guid branchId, TimetableSettingsDto settings);
    Task<List<TimetableRoomDto>> GetTimetableRoomsAsync(Guid branchId);
    Task<List<TimetableRoomDto>> SaveTimetableRoomsAsync(Guid branchId, UpdateTimetableRoomsRequest request);
    Task<List<TimetableDto>> GetTimetablesAsync(Guid branchId);
    Task<TimetableDetailDto> GetTimetableAsync(Guid branchId, Guid id);
    /// <summary>The published version in force today, or null.</summary>
    Task<TimetableDetailDto?> GetCurrentTimetableAsync(Guid branchId);
    Task<TimetableDetailDto> CreateTimetableAsync(Guid branchId, CreateTimetableRequest request);
    Task DeleteTimetableDraftAsync(Guid branchId, Guid id);
    Task<LessonChangeResultDto> PlaceLessonAsync(Guid branchId, Guid timetableId, PlaceLessonRequest request);
    Task<LessonChangeResultDto> MoveLessonAsync(Guid branchId, Guid timetableId, Guid lessonId, MoveLessonRequest request);
    Task<LessonChangeResultDto> RemoveLessonAsync(Guid branchId, Guid timetableId, Guid lessonId);
    /// <summary>Throws <see cref="TimetableClashesException"/> for HARD_CLASHES or SOFT_CLASHES.</summary>
    Task<TimetableDetailDto> PublishTimetableAsync(Guid branchId, Guid id, PublishTimetableRequest request);
    Task<TimetableDetailDto> ArchiveTimetableAsync(Guid branchId, Guid id);
    Task<RosterImportJobDto> StartTimetableImportAsync(Guid branchId, Guid timetableId, StartTimetableImportRequest request);
    Task<RosterImportJobDto> GetTimetableImportJobAsync(Guid branchId, Guid jobId);
    Task<List<RosterImportJobEntryDto>> GetTimetableImportEntriesAsync(Guid branchId, Guid jobId, int limit = 500);

    // Lessons (duty rota plan §7)
    Task<MyDayDto> GetMyDayAsync(Guid branchId, DateOnly? date = null);
    Task<LessonListDto> GetLessonsAsync(Guid branchId, DateTime from, DateTime to, Guid? teacherUserId = null, LessonStatus? status = null);
    Task<LessonItemDto> FlagLessonAsync(Guid branchId, Guid dutyId, FlagLessonRequest request);
    Task<ConfirmLessonsResultDto> ConfirmLessonsAsync(Guid branchId, IEnumerable<Guid> dutyIds);
    Task<LessonItemDto> ScheduleRecoveryAsync(Guid branchId, Guid dutyId, ScheduleRecoveryRequest request);
    Task<LessonItemDto> CancelLessonAsync(Guid branchId, Guid dutyId, string reason);
    Task<TeachingReportsDto> GetTeachingReportsAsync(Guid branchId, string? period = null);
    Task<TeachingDashboardDto> GetTeachingDashboardAsync(Guid branchId);
    Task<StaffDutyDto> CreateDutyAsync(Guid branchId, SaveStaffDutyRequest request);
    Task<StaffDutyDto> UpdateDutyAsync(Guid branchId, Guid id, SaveStaffDutyRequest request);
    Task CancelDutyAsync(Guid branchId, Guid id);
    Task<StaffDutyDto> DuplicateDutyAsync(Guid branchId, Guid id, DuplicateStaffDutyRequest request);
    Task<StaffRegisterDto> GetRegisterAsync(Guid branchId, Guid id);
    Task<StaffRegisterDto> SubmitRegisterAsync(Guid branchId, Guid id, SubmitRegisterRequest request);
    Task<StaffDutyDto> AttachMinutesAsync(Guid branchId, Guid id, AttachMinutesRequest request);

    // Notices
    Task<List<StaffNoticeDto>> GetMyNoticesAsync(Guid branchId);
    Task<List<StaffNoticeDto>> GetManagedNoticesAsync(Guid branchId, bool includeInactive = false);
    Task<StaffNoticeDto> CreateNoticeAsync(Guid branchId, SaveStaffNoticeRequest request);
    Task<StaffNoticeDto> UpdateNoticeAsync(Guid branchId, Guid id, SaveStaffNoticeRequest request);
    Task WithdrawNoticeAsync(Guid branchId, Guid id);
    Task<List<NoticeAcknowledgementDto>> GetNoticeAcknowledgementsAsync(Guid branchId, Guid id);
    Task AcknowledgeNoticeAsync(Guid id);

    // Reports, appraisals, activity
    Task<StaffReportsDto> GetReportsAsync(Guid branchId, string? period = null);
    Task<AppraisalBoardDto> GetAppraisalBoardAsync(Guid branchId, string? period = null);
    Task<List<StaffAppraisalDto>> OpenAppraisalsAsync(Guid branchId, OpenAppraisalsRequest request);
    Task<StaffAppraisalDto> GetAppraisalAsync(Guid branchId, Guid id);
    Task<StaffAppraisalDto> SetAppraisalTargetsAsync(Guid branchId, Guid id, SetAppraisalTargetsRequest request);
    Task<StaffAppraisalDto> SubmitSelfAssessmentAsync(Guid branchId, Guid id, SubmitSelfAssessmentRequest request);
    Task<StaffAppraisalDto> SubmitAppraiserReviewAsync(Guid branchId, Guid id, SubmitAppraiserReviewRequest request);
    Task<StaffAppraisalDto> ModerateAppraisalAsync(Guid branchId, Guid id, ModerateAppraisalRequest request);
    Task<StaffAppraisalDto> SignAppraisalAsync(Guid branchId, Guid id);
    Task<StaffAppraisalDto> AppealAppraisalAsync(Guid branchId, Guid id, AppealAppraisalRequest request);
    Task<StaffAppraisalDto> AttachAppraisalReportAsync(Guid branchId, Guid id, AttachAppraisalReportRequest request);
    Task<ActivityLogPageDto> GetActivityAsync(Guid branchId, int page = 1, int pageSize = 50, Guid? userId = null, string? action = null);
    /// <summary>Tells the API an export or Publish to Library just happened in the browser, so the activity log carries it. Never throws: a log line that failed must not undo a download the user already has.</summary>
    Task RecordExportAsync(Guid branchId, RecordStaffExportRequest request);
    Task<StaffImportStartedDto> StartStaffImportAsync(Guid branchId, StartStaffImportRequest request);
    Task<StaffImportPrecheckDto> PrecheckStaffImportAsync(Guid branchId, StaffImportPrecheckRequest request);
    Task<List<RosterImportJobDto>> GetStaffImportJobsAsync(Guid branchId, int limit = 50);
    Task<RosterImportJobDto> GetStaffImportJobAsync(Guid branchId, Guid jobId);
    Task<List<RosterImportJobEntryDto>> GetStaffImportJobEntriesAsync(Guid branchId, Guid jobId, int limit = 500);

    // Portal (the caller's own)
    Task<StaffPortalDto> GetPortalAsync();
    Task<StaffRecordSearchResultDto> GetMyRecordsAsync(int page = 1, int pageSize = 25);
    Task<ActivityLogPageDto> GetMyActivityAsync(int page = 1, int pageSize = 50);
    Task<List<StaffColleagueDto>> GetColleaguesAsync();
    Task<StaffFileExportDto> ExportMyFileAsync();
    /// <summary>Marks every record about me as seen; returns how many.</summary>
    Task<int> AcknowledgeAllMyRecordsAsync();
}

/// <summary>The API refused a record dated more than the policy's late-entry threshold ago until the caller confirms.</summary>
public class LateEntryConfirmationRequiredException : InvalidOperationException
{
    public int ThresholdDays { get; }
    public LateEntryConfirmationRequiredException(int thresholdDays)
        : base($"This record is dated more than {thresholdDays} days ago.") => ThresholdDays = thresholdDays;
}

/// <summary>Publishing refused for clashes: <see cref="Hard"/> cannot be overridden; soft ones can, with a note.</summary>
public class TimetableClashesException : InvalidOperationException
{
    public bool Hard { get; }
    public int Count { get; }
    public TimetableClashesException(bool hard, int count, string message) : base(message) { Hard = hard; Count = count; }
}

public class StaffPerformanceApiService : IStaffPerformanceApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public StaffPerformanceApiService(HttpClient http, JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    private static string B(Guid branchId) => $"api/v1/branches/{branchId}/staff";

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var message = new HttpRequestMessage(method, url);
        if (body != null) message.Content = JsonContent.Create(body, options: _json);
        var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var message = new HttpRequestMessage(method, url);
        if (body != null) message.Content = JsonContent.Create(body, options: _json);
        var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    /// <summary>A record write that the API may answer with 409 LATE_ENTRY: surfaced as its own exception so the dialog can ask and resubmit.</summary>
    private async Task<StaffPerformanceRecordDto> SendLateEntryAwareAsync(HttpMethod method, string url, object? body)
    {
        using var message = new HttpRequestMessage(method, url);
        if (body != null) message.Content = JsonContent.Create(body, options: _json);
        var response = await _http.SendAsync(message);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (text.Contains("LATE_ENTRY", StringComparison.Ordinal))
            {
                var days = 14;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("thresholdDays", out var t) && t.TryGetInt32(out var d)) days = d;
                }
                catch (JsonException) { }
                throw new LateEntryConfirmationRequiredException(days);
            }
            throw new InvalidOperationException(ApiErrorService.GetErrorMessageFromBody(text, "This change conflicts with the current state."));
        }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<StaffPerformanceRecordDto>(_json))!;
    }

    private static string Q(params (string Key, string? Value)[] parts)
    {
        var items = parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return items.Count == 0 ? string.Empty : "?" + string.Join("&", items);
    }

    private static string? D(DateTime? d) => d?.ToUniversalTime().ToString("O");

    // ---- Timetable ----
    private static string TT(Guid branchId) => $"api/v1/branches/{branchId}/timetable";
    public Task<TimetableSettingsDto> GetTimetableSettingsAsync(Guid branchId) => GetAsync<TimetableSettingsDto>($"{TT(branchId)}/settings");
    public Task<TimetableSettingsDto> SaveTimetableSettingsAsync(Guid branchId, TimetableSettingsDto settings) => SendAsync<TimetableSettingsDto>(HttpMethod.Put, $"{TT(branchId)}/settings", settings);
    public Task<List<TimetableRoomDto>> GetTimetableRoomsAsync(Guid branchId) => GetAsync<List<TimetableRoomDto>>($"{TT(branchId)}/rooms");
    public Task<List<TimetableRoomDto>> SaveTimetableRoomsAsync(Guid branchId, UpdateTimetableRoomsRequest request) => SendAsync<List<TimetableRoomDto>>(HttpMethod.Put, $"{TT(branchId)}/rooms", request);
    public Task<List<TimetableDto>> GetTimetablesAsync(Guid branchId) => GetAsync<List<TimetableDto>>($"{TT(branchId)}/timetables");
    public Task<TimetableDetailDto> GetTimetableAsync(Guid branchId, Guid id) => GetAsync<TimetableDetailDto>($"{TT(branchId)}/timetables/{id}");
    public async Task<TimetableDetailDto?> GetCurrentTimetableAsync(Guid branchId)
    {
        var response = await _http.GetAsync($"{TT(branchId)}/current");
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<TimetableDetailDto>(_json);
    }
    public Task<TimetableDetailDto> CreateTimetableAsync(Guid branchId, CreateTimetableRequest request) => SendAsync<TimetableDetailDto>(HttpMethod.Post, $"{TT(branchId)}/timetables", request);
    public Task DeleteTimetableDraftAsync(Guid branchId, Guid id) => SendAsync(HttpMethod.Delete, $"{TT(branchId)}/timetables/{id}");
    public Task<LessonChangeResultDto> PlaceLessonAsync(Guid branchId, Guid timetableId, PlaceLessonRequest request) => SendAsync<LessonChangeResultDto>(HttpMethod.Post, $"{TT(branchId)}/timetables/{timetableId}/lessons", request);
    public Task<LessonChangeResultDto> MoveLessonAsync(Guid branchId, Guid timetableId, Guid lessonId, MoveLessonRequest request) => SendAsync<LessonChangeResultDto>(HttpMethod.Put, $"{TT(branchId)}/timetables/{timetableId}/lessons/{lessonId}", request);
    public Task<LessonChangeResultDto> RemoveLessonAsync(Guid branchId, Guid timetableId, Guid lessonId) => SendAsync<LessonChangeResultDto>(HttpMethod.Delete, $"{TT(branchId)}/timetables/{timetableId}/lessons/{lessonId}");
    public async Task<TimetableDetailDto> PublishTimetableAsync(Guid branchId, Guid id, PublishTimetableRequest request)
    {
        var response = await _http.PostAsJsonAsync($"{TT(branchId)}/timetables/{id}/publish", request, _json);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var text = await response.Content.ReadAsStringAsync();
            var hard = text.Contains("HARD_CLASHES", StringComparison.Ordinal);
            if (hard || text.Contains("SOFT_CLASHES", StringComparison.Ordinal))
            {
                var count = 0;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty(hard ? "hardCount" : "softCount", out var c) && c.TryGetInt32(out var n)) count = n;
                }
                catch (JsonException) { }
                throw new TimetableClashesException(hard, count, ApiErrorService.GetErrorMessageFromBody(text, "Clashes remain."));
            }
            throw new InvalidOperationException(ApiErrorService.GetErrorMessageFromBody(text, "This change conflicts with the current state."));
        }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<TimetableDetailDto>(_json))!;
    }
    public Task<TimetableDetailDto> ArchiveTimetableAsync(Guid branchId, Guid id) => SendAsync<TimetableDetailDto>(HttpMethod.Post, $"{TT(branchId)}/timetables/{id}/archive");
    public Task<RosterImportJobDto> StartTimetableImportAsync(Guid branchId, Guid timetableId, StartTimetableImportRequest request) => SendAsync<RosterImportJobDto>(HttpMethod.Post, $"{TT(branchId)}/timetables/{timetableId}/import", request);
    public Task<RosterImportJobDto> GetTimetableImportJobAsync(Guid branchId, Guid jobId) => GetAsync<RosterImportJobDto>($"{TT(branchId)}/import-jobs/{jobId}");
    public Task<List<RosterImportJobEntryDto>> GetTimetableImportEntriesAsync(Guid branchId, Guid jobId, int limit = 500) => GetAsync<List<RosterImportJobEntryDto>>($"{TT(branchId)}/import-jobs/{jobId}/entries?limit={limit}");

    // ---- Lessons ----
    private static string LS(Guid branchId) => $"api/v1/branches/{branchId}/staff/lessons";
    public Task<MyDayDto> GetMyDayAsync(Guid branchId, DateOnly? date = null) => GetAsync<MyDayDto>($"{LS(branchId)}/my-day{Q(("date", date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)))}");
    public Task<LessonListDto> GetLessonsAsync(Guid branchId, DateTime from, DateTime to, Guid? teacherUserId = null, LessonStatus? status = null)
        => GetAsync<LessonListDto>($"{LS(branchId)}{Q(("from", D(from)), ("to", D(to)), ("teacherUserId", teacherUserId?.ToString()), ("status", status?.ToString()))}");
    public Task<LessonItemDto> FlagLessonAsync(Guid branchId, Guid dutyId, FlagLessonRequest request) => SendAsync<LessonItemDto>(HttpMethod.Post, $"{LS(branchId)}/{dutyId}/flag", request);
    public Task<ConfirmLessonsResultDto> ConfirmLessonsAsync(Guid branchId, IEnumerable<Guid> dutyIds) => SendAsync<ConfirmLessonsResultDto>(HttpMethod.Post, $"{LS(branchId)}/confirm", new ConfirmLessonsRequest { DutyIds = dutyIds.ToList() });
    public Task<LessonItemDto> ScheduleRecoveryAsync(Guid branchId, Guid dutyId, ScheduleRecoveryRequest request) => SendAsync<LessonItemDto>(HttpMethod.Post, $"{LS(branchId)}/{dutyId}/recovery", request);
    public Task<LessonItemDto> CancelLessonAsync(Guid branchId, Guid dutyId, string reason) => SendAsync<LessonItemDto>(HttpMethod.Post, $"{LS(branchId)}/{dutyId}/cancel", new CancelLessonRequest { Reason = reason });

    public Task<TeachingReportsDto> GetTeachingReportsAsync(Guid branchId, string? period = null) => GetAsync<TeachingReportsDto>($"{B(branchId)}/reports/teaching{Q(("period", period))}");
    public Task<TeachingDashboardDto> GetTeachingDashboardAsync(Guid branchId) => GetAsync<TeachingDashboardDto>($"{B(branchId)}/reports/teaching/dashboard");

    // ---- Parameters and policy ----
    public Task<List<PerformanceParameterDto>> GetParametersAsync(bool includeInactive = false) => GetAsync<List<PerformanceParameterDto>>($"api/v1/staff/parameters{Q(("includeInactive", includeInactive ? "true" : null))}");
    public Task<PerformanceParameterDto> CreateParameterAsync(SavePerformanceParameterRequest request) => SendAsync<PerformanceParameterDto>(HttpMethod.Post, "api/v1/staff/parameters", request);
    public Task<PerformanceParameterDto> UpdateParameterAsync(Guid id, SavePerformanceParameterRequest request) => SendAsync<PerformanceParameterDto>(HttpMethod.Put, $"api/v1/staff/parameters/{id}", request);
    public Task<PerformanceParameterDto> ToggleParameterAsync(Guid id) => SendAsync<PerformanceParameterDto>(HttpMethod.Patch, $"api/v1/staff/parameters/{id}/toggle");
    public Task<StaffPerformancePolicyDto> GetPolicyAsync() => GetAsync<StaffPerformancePolicyDto>("api/v1/staff/policy");
    public Task<StaffPerformancePolicyDto> UpdatePolicyAsync(StaffPerformancePolicyDto policy) => SendAsync<StaffPerformancePolicyDto>(HttpMethod.Put, "api/v1/staff/policy", policy);
    public Task<PeriodStatusDto> GetPeriodStatusAsync(string periodKey) => GetAsync<PeriodStatusDto>($"api/v1/staff/policy/periods/{Uri.EscapeDataString(periodKey)}");
    public Task<PeriodStatusDto> ClosePeriodAsync(string periodKey, ClosePeriodRequest request) => SendAsync<PeriodStatusDto>(HttpMethod.Post, $"api/v1/staff/policy/periods/{Uri.EscapeDataString(periodKey)}/close", request);
    public Task<PeriodStatusDto> ReopenPeriodAsync(string periodKey, ReopenPeriodRequest request) => SendAsync<PeriodStatusDto>(HttpMethod.Post, $"api/v1/staff/policy/periods/{Uri.EscapeDataString(periodKey)}/reopen", request);

    // ---- Structure ----
    public Task<List<DepartmentDto>> GetDepartmentsAsync(Guid branchId, bool includeInactive = false) => GetAsync<List<DepartmentDto>>($"{B(branchId)}/structure/departments{Q(("includeInactive", includeInactive ? "true" : null))}");
    public Task<DepartmentDto> CreateDepartmentAsync(Guid branchId, SaveDepartmentRequest request) => SendAsync<DepartmentDto>(HttpMethod.Post, $"{B(branchId)}/structure/departments", request);
    public Task<DepartmentDto> UpdateDepartmentAsync(Guid branchId, Guid id, SaveDepartmentRequest request) => SendAsync<DepartmentDto>(HttpMethod.Put, $"{B(branchId)}/structure/departments/{id}", request);
    public Task<DepartmentDto> ToggleDepartmentAsync(Guid branchId, Guid id) => SendAsync<DepartmentDto>(HttpMethod.Patch, $"{B(branchId)}/structure/departments/{id}/toggle");
    public Task<StaffDirectoryDto> GetDirectoryAsync(Guid branchId, string? period = null) => GetAsync<StaffDirectoryDto>($"{B(branchId)}/structure/members{Q(("period", period))}");
    public Task<StaffMemberDto> UpdateMemberStructureAsync(Guid branchId, Guid userId, UpdateStaffStructureRequest request) => SendAsync<StaffMemberDto>(HttpMethod.Put, $"{B(branchId)}/structure/members/{userId}", request);
    public Task<StaffProfileDto> GetStaffProfileAsync(Guid branchId, Guid userId) => GetAsync<StaffProfileDto>($"{B(branchId)}/structure/members/{userId}/profile");
    public Task<StaffProfileDto> UpdateStaffProfileAsync(Guid branchId, Guid userId, UpdateStaffProfileRequest request) => SendAsync<StaffProfileDto>(HttpMethod.Put, $"{B(branchId)}/structure/members/{userId}/profile", request);
    public Task<CreateStaffMemberResult> CreateStaffMemberAsync(Guid branchId, CreateStaffMemberRequest request) => SendAsync<CreateStaffMemberResult>(HttpMethod.Post, $"{B(branchId)}/structure/members", request);
    public Task<StaffProfileDto> GetMyProfileAsync() => GetAsync<StaffProfileDto>("api/v1/staff/portal/profile");
    public Task<StaffProfileDto> UpdateMyContactAsync(UpdateStaffContactRequest request) => SendAsync<StaffProfileDto>(HttpMethod.Put, "api/v1/staff/portal/profile/contact", request);
    public Task<List<SubjectDto>> GetSubjectsAsync(Guid branchId, bool includeInactive = false) => GetAsync<List<SubjectDto>>($"{B(branchId)}/subjects{Q(("includeInactive", includeInactive ? "true" : null))}");
    public Task<SubjectDto> CreateSubjectAsync(Guid branchId, SaveSubjectRequest request) => SendAsync<SubjectDto>(HttpMethod.Post, $"{B(branchId)}/subjects", request);
    public Task<SubjectDto> UpdateSubjectAsync(Guid branchId, Guid id, SaveSubjectRequest request) => SendAsync<SubjectDto>(HttpMethod.Put, $"{B(branchId)}/subjects/{id}", request);
    public Task<SubjectDto> ToggleSubjectAsync(Guid branchId, Guid id) => SendAsync<SubjectDto>(HttpMethod.Patch, $"{B(branchId)}/subjects/{id}/toggle");
    public Task<StructureCoverageDto> GetCoverageAsync(Guid branchId, string? period = null) => GetAsync<StructureCoverageDto>($"{B(branchId)}/structure/coverage{Q(("period", period))}");

    // ---- Records ----
    public Task<StaffRecordSearchResultDto> SearchRecordsAsync(Guid branchId, Guid? subjectUserId = null, Guid? parameterId = null, DateTime? from = null, DateTime? to = null, string? status = null, string? q = null, int page = 1, int pageSize = 25)
        => GetAsync<StaffRecordSearchResultDto>($"{B(branchId)}/records{Q(("subjectUserId", subjectUserId?.ToString()), ("parameterId", parameterId?.ToString()), ("from", D(from)), ("to", D(to)), ("status", status), ("q", q), ("page", page.ToString()), ("pageSize", pageSize.ToString()))}");
    public Task<StaffPerformanceRecordDto> CreateRecordAsync(Guid branchId, CreateStaffRecordRequest request, bool acknowledgeLateEntry = false)
        => SendLateEntryAwareAsync(HttpMethod.Post, $"{B(branchId)}/records{Q(("acknowledgeLateEntry", acknowledgeLateEntry ? "true" : null))}", request);
    public Task<StaffPerformanceRecordDto> GetRecordAsync(Guid branchId, Guid id) => GetAsync<StaffPerformanceRecordDto>($"{B(branchId)}/records/{id}");
    public Task<StaffPerformanceRecordDto> FinalizeRecordAsync(Guid branchId, Guid id, bool acknowledgeLateEntry = false)
        => SendLateEntryAwareAsync(HttpMethod.Post, $"{B(branchId)}/records/{id}/finalize{Q(("acknowledgeLateEntry", acknowledgeLateEntry ? "true" : null))}", null);
    public Task<StaffPerformanceRecordDto> AddNoteAsync(Guid branchId, Guid id, AddStaffNoteRequest request) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Post, $"{B(branchId)}/records/{id}/notes", request);
    public Task<StaffPerformanceRecordDto> RespondAsync(Guid branchId, Guid id, AddStaffNoteRequest request) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Post, $"{B(branchId)}/records/{id}/respond", request);
    public Task<StaffPerformanceRecordDto> AcknowledgeAsync(Guid branchId, Guid id) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Post, $"{B(branchId)}/records/{id}/acknowledge");
    public Task<StaffPerformanceRecordDto> AnnulAsync(Guid branchId, Guid id, AnnulStaffRecordRequest request) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Post, $"{B(branchId)}/records/{id}/annul", request);
    public Task<StaffPerformanceRecordDto> UpdateVisibilityAsync(Guid branchId, Guid id, UpdateStaffRecordVisibilityRequest request) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Patch, $"{B(branchId)}/records/{id}/visibility", request);
    public Task<StaffPerformanceRecordDto> CorrectPointsAsync(Guid branchId, Guid id, CorrectStaffRecordPointsRequest request) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Patch, $"{B(branchId)}/records/{id}/points", request);
    public Task<StaffTimelineDto> GetTimelineAsync(Guid branchId, Guid userId, DateTime? from = null, DateTime? to = null) => GetAsync<StaffTimelineDto>($"{B(branchId)}/members/{userId}/timeline{Q(("from", D(from)), ("to", D(to)))}");
    public Task<StaffScoreDto> GetScoreAsync(Guid branchId, Guid userId, string? period = null) => GetAsync<StaffScoreDto>($"{B(branchId)}/members/{userId}/score{Q(("period", period))}");
    public Task<StaffPerformanceRecordDto> GiveRecognitionAsync(Guid branchId, GiveRecognitionRequest request) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Post, $"{B(branchId)}/recognition", request);
    public Task<RecognitionBudgetDto> GetRecognitionBudgetAsync(Guid branchId) => GetAsync<RecognitionBudgetDto>($"{B(branchId)}/recognition/budget");

    // ---- Duties ----
    public Task<List<StaffDutyDto>> GetDutiesAsync(Guid branchId, DateTime from, DateTime to, DutyKind? kind = null) => GetAsync<List<StaffDutyDto>>($"{B(branchId)}/duties{Q(("from", D(from)), ("to", D(to)), ("kind", kind?.ToString()))}");
    public Task<StaffDutyDto> AcknowledgeDutyAsync(Guid branchId, Guid id) => SendAsync<StaffDutyDto>(HttpMethod.Post, $"{B(branchId)}/duties/{id}/acknowledge");
    public async Task<Guid> GetRotaParameterIdAsync(Guid branchId) => (await GetAsync<RotaDefaultsResponse>($"{B(branchId)}/rota/defaults")).ParameterId;
    public Task<RotaGenerateResultDto> GenerateRotaAsync(Guid branchId, GenerateRotaRequest request) => SendAsync<RotaGenerateResultDto>(HttpMethod.Post, $"{B(branchId)}/rota/generate", request);
    public Task<RotaGenerateResultDto> ExtendRotaAsync(Guid branchId, Guid seriesId, ExtendRotaRequest request) => SendAsync<RotaGenerateResultDto>(HttpMethod.Post, $"{B(branchId)}/rota/series/{seriesId}/extend", request);
    public async Task<int> CancelRotaSeriesAsync(Guid branchId, Guid seriesId) => (await SendAsync<CancelSeriesResponse>(HttpMethod.Delete, $"{B(branchId)}/rota/series/{seriesId}")).Cancelled;
    public Task SwapRotaAsync(Guid branchId, SwapRotaRequest request) => SendAsync(HttpMethod.Post, $"{B(branchId)}/rota/swap", request);
    public Task<List<RotaWarningDto>> CheckRotaSlotAsync(Guid branchId, CheckRotaSlotRequest request) => SendAsync<List<RotaWarningDto>>(HttpMethod.Post, $"{B(branchId)}/rota/check", request);
    public Task<RotaFairnessDto> GetRotaFairnessAsync(Guid branchId, string? period = null) => GetAsync<RotaFairnessDto>($"{B(branchId)}/rota/fairness{Q(("period", period))}");
    public Task<List<DutyReportSummaryDto>> GetMyDutyReportsAsync(Guid branchId, bool openOnly = false) => GetAsync<List<DutyReportSummaryDto>>($"{B(branchId)}/duty-reports/mine{Q(("openOnly", openOnly ? "true" : null))}");
    public Task<DutyReportQueueDto> GetDutyReportQueueAsync(Guid branchId, DateTime? from = null, DateTime? to = null, DutyReportStatus? status = null, Guid? dutyId = null) => GetAsync<DutyReportQueueDto>($"{B(branchId)}/duty-reports{Q(("from", D(from)), ("to", D(to)), ("status", status?.ToString()), ("dutyId", dutyId?.ToString()))}");
    public Task<DutyReportDto> GetDutyReportAsync(Guid branchId, Guid id) => GetAsync<DutyReportDto>($"{B(branchId)}/duty-reports/{id}");
    public Task<DutyReportDto> SaveDutyReportAsync(Guid branchId, Guid id, SaveDutyReportRequest request) => SendAsync<DutyReportDto>(HttpMethod.Put, $"{B(branchId)}/duty-reports/{id}", request);
    public Task<DutyReportDto> SubmitDutyReportAsync(Guid branchId, Guid id, SaveDutyReportRequest request) => SendAsync<DutyReportDto>(HttpMethod.Post, $"{B(branchId)}/duty-reports/{id}/submit", request);
    public Task<DutyReportDto> AddDutyReportNoteAsync(Guid branchId, Guid id, string body) => SendAsync<DutyReportDto>(HttpMethod.Post, $"{B(branchId)}/duty-reports/{id}/notes", new DutyReportNoteRequest { Body = body });
    public Task<DutyReportDto> ReturnDutyReportAsync(Guid branchId, Guid id, string reason) => SendAsync<DutyReportDto>(HttpMethod.Post, $"{B(branchId)}/duty-reports/{id}/return", new DutyReportNoteRequest { Body = reason });
    public Task<DutyReportDto> ReviewDutyReportAsync(Guid branchId, Guid id, string? remark) => SendAsync<DutyReportDto>(HttpMethod.Post, $"{B(branchId)}/duty-reports/{id}/review", new DutyReportNoteRequest { Body = remark ?? string.Empty });
    public Task<DutyReportDto> ReopenDutyReportAsync(Guid branchId, Guid id, string reason) => SendAsync<DutyReportDto>(HttpMethod.Post, $"{B(branchId)}/duty-reports/{id}/reopen", new DutyReportNoteRequest { Body = reason });
    public Task MarkNoDutyAsync(Guid branchId, Guid id, string reason) => SendAsync(HttpMethod.Post, $"{B(branchId)}/duty-reports/{id}/no-duty", new DutyReportNoteRequest { Body = reason });
    public Task<List<DutyReportLinkedRecordDto>> GetLinkableWelfareRecordsAsync(Guid branchId) => GetAsync<List<DutyReportLinkedRecordDto>>($"{B(branchId)}/duty-reports/linkable-welfare-records");
    private sealed record RotaDefaultsResponse(Guid ParameterId);
    private sealed record CancelSeriesResponse(int Cancelled, int Kept);
    public Task<StaffDutyDto> CreateDutyAsync(Guid branchId, SaveStaffDutyRequest request) => SendAsync<StaffDutyDto>(HttpMethod.Post, $"{B(branchId)}/duties", request);
    public Task<StaffDutyDto> UpdateDutyAsync(Guid branchId, Guid id, SaveStaffDutyRequest request) => SendAsync<StaffDutyDto>(HttpMethod.Put, $"{B(branchId)}/duties/{id}", request);
    public Task CancelDutyAsync(Guid branchId, Guid id) => SendAsync(HttpMethod.Delete, $"{B(branchId)}/duties/{id}");
    public Task<StaffDutyDto> DuplicateDutyAsync(Guid branchId, Guid id, DuplicateStaffDutyRequest request) => SendAsync<StaffDutyDto>(HttpMethod.Post, $"{B(branchId)}/duties/{id}/duplicate", request);
    public Task<StaffRegisterDto> GetRegisterAsync(Guid branchId, Guid id) => GetAsync<StaffRegisterDto>($"{B(branchId)}/duties/{id}/register");
    public Task<StaffRegisterDto> SubmitRegisterAsync(Guid branchId, Guid id, SubmitRegisterRequest request) => SendAsync<StaffRegisterDto>(HttpMethod.Post, $"{B(branchId)}/duties/{id}/register", request);
    public Task<StaffDutyDto> AttachMinutesAsync(Guid branchId, Guid id, AttachMinutesRequest request) => SendAsync<StaffDutyDto>(HttpMethod.Put, $"{B(branchId)}/duties/{id}/minutes", request);

    // ---- Notices ----
    public Task<List<StaffNoticeDto>> GetMyNoticesAsync(Guid branchId) => GetAsync<List<StaffNoticeDto>>($"{B(branchId)}/notices");
    public Task<List<StaffNoticeDto>> GetManagedNoticesAsync(Guid branchId, bool includeInactive = false) => GetAsync<List<StaffNoticeDto>>($"{B(branchId)}/notices/manage{Q(("includeInactive", includeInactive ? "true" : null))}");
    public Task<StaffNoticeDto> CreateNoticeAsync(Guid branchId, SaveStaffNoticeRequest request) => SendAsync<StaffNoticeDto>(HttpMethod.Post, $"{B(branchId)}/notices", request);
    public Task<StaffNoticeDto> UpdateNoticeAsync(Guid branchId, Guid id, SaveStaffNoticeRequest request) => SendAsync<StaffNoticeDto>(HttpMethod.Put, $"{B(branchId)}/notices/{id}", request);
    public Task WithdrawNoticeAsync(Guid branchId, Guid id) => SendAsync(HttpMethod.Delete, $"{B(branchId)}/notices/{id}");
    public Task<List<NoticeAcknowledgementDto>> GetNoticeAcknowledgementsAsync(Guid branchId, Guid id) => GetAsync<List<NoticeAcknowledgementDto>>($"{B(branchId)}/notices/{id}/acknowledgements");
    public Task AcknowledgeNoticeAsync(Guid id) => SendAsync(HttpMethod.Post, $"api/v1/staff/portal/notices/{id}/acknowledge");

    // ---- Reports, appraisals, activity ----
    public Task<StaffReportsDto> GetReportsAsync(Guid branchId, string? period = null) => GetAsync<StaffReportsDto>($"{B(branchId)}/reports{Q(("period", period))}");
    public Task<AppraisalBoardDto> GetAppraisalBoardAsync(Guid branchId, string? period = null) => GetAsync<AppraisalBoardDto>($"{B(branchId)}/appraisals/board{Q(("period", period))}");
    public Task<List<StaffAppraisalDto>> OpenAppraisalsAsync(Guid branchId, OpenAppraisalsRequest request) => SendAsync<List<StaffAppraisalDto>>(HttpMethod.Post, $"{B(branchId)}/appraisals/open", request);
    public Task<StaffAppraisalDto> GetAppraisalAsync(Guid branchId, Guid id) => GetAsync<StaffAppraisalDto>($"{B(branchId)}/appraisals/{id}");
    public Task<StaffAppraisalDto> SetAppraisalTargetsAsync(Guid branchId, Guid id, SetAppraisalTargetsRequest request) => SendAsync<StaffAppraisalDto>(HttpMethod.Put, $"{B(branchId)}/appraisals/{id}/targets", request);
    public Task<StaffAppraisalDto> SubmitSelfAssessmentAsync(Guid branchId, Guid id, SubmitSelfAssessmentRequest request) => SendAsync<StaffAppraisalDto>(HttpMethod.Post, $"{B(branchId)}/appraisals/{id}/self", request);
    public Task<StaffAppraisalDto> SubmitAppraiserReviewAsync(Guid branchId, Guid id, SubmitAppraiserReviewRequest request) => SendAsync<StaffAppraisalDto>(HttpMethod.Post, $"{B(branchId)}/appraisals/{id}/review", request);
    public Task<StaffAppraisalDto> ModerateAppraisalAsync(Guid branchId, Guid id, ModerateAppraisalRequest request) => SendAsync<StaffAppraisalDto>(HttpMethod.Post, $"{B(branchId)}/appraisals/{id}/moderate", request);
    public Task<StaffAppraisalDto> SignAppraisalAsync(Guid branchId, Guid id) => SendAsync<StaffAppraisalDto>(HttpMethod.Post, $"{B(branchId)}/appraisals/{id}/sign");
    public Task<StaffAppraisalDto> AppealAppraisalAsync(Guid branchId, Guid id, AppealAppraisalRequest request) => SendAsync<StaffAppraisalDto>(HttpMethod.Post, $"{B(branchId)}/appraisals/{id}/appeal", request);
    public Task<StaffAppraisalDto> AttachAppraisalReportAsync(Guid branchId, Guid id, AttachAppraisalReportRequest request) => SendAsync<StaffAppraisalDto>(HttpMethod.Put, $"{B(branchId)}/appraisals/{id}/report", request);
    public async Task RecordExportAsync(Guid branchId, RecordStaffExportRequest request)
    {
        try { await SendAsync(HttpMethod.Post, $"{B(branchId)}/activity/exports", request); }
        catch { /* the file is already with the user; a missing log line is not their problem */ }
    }

    public Task<ActivityLogPageDto> GetActivityAsync(Guid branchId, int page = 1, int pageSize = 50, Guid? userId = null, string? action = null)
        => GetAsync<ActivityLogPageDto>($"{B(branchId)}/activity{Q(("page", page.ToString()), ("pageSize", pageSize.ToString()), ("userId", userId?.ToString()), ("action", action))}");
    public Task<StaffImportStartedDto> StartStaffImportAsync(Guid branchId, StartStaffImportRequest request) => SendAsync<StaffImportStartedDto>(HttpMethod.Post, $"{B(branchId)}/import-jobs", request);

    /// <summary>Read-only: which of these emails the organization already has, asked before an import commits.</summary>
    public Task<StaffImportPrecheckDto> PrecheckStaffImportAsync(Guid branchId, StaffImportPrecheckRequest request) => SendAsync<StaffImportPrecheckDto>(HttpMethod.Post, $"{B(branchId)}/import-jobs/precheck", request);
    public Task<List<RosterImportJobDto>> GetStaffImportJobsAsync(Guid branchId, int limit = 50) => GetAsync<List<RosterImportJobDto>>($"{B(branchId)}/import-jobs?limit={limit}");
    public Task<RosterImportJobDto> GetStaffImportJobAsync(Guid branchId, Guid jobId) => GetAsync<RosterImportJobDto>($"{B(branchId)}/import-jobs/{jobId}");
    public Task<List<RosterImportJobEntryDto>> GetStaffImportJobEntriesAsync(Guid branchId, Guid jobId, int limit = 500) => GetAsync<List<RosterImportJobEntryDto>>($"{B(branchId)}/import-jobs/{jobId}/entries?limit={limit}");

    // ---- Portal ----
    public Task<StaffPortalDto> GetPortalAsync() => GetAsync<StaffPortalDto>("api/v1/staff/portal");
    public Task<StaffRecordSearchResultDto> GetMyRecordsAsync(int page = 1, int pageSize = 25) => GetAsync<StaffRecordSearchResultDto>($"api/v1/staff/portal/records{Q(("page", page.ToString()), ("pageSize", pageSize.ToString()))}");
    public Task<ActivityLogPageDto> GetMyActivityAsync(int page = 1, int pageSize = 50) => GetAsync<ActivityLogPageDto>($"api/v1/staff/portal/activity{Q(("page", page.ToString()), ("pageSize", pageSize.ToString()))}");
    public Task<List<StaffColleagueDto>> GetColleaguesAsync() => GetAsync<List<StaffColleagueDto>>("api/v1/staff/portal/colleagues");
    public Task<StaffFileExportDto> ExportMyFileAsync() => GetAsync<StaffFileExportDto>("api/v1/staff/portal/export");
    public async Task<int> AcknowledgeAllMyRecordsAsync()
        => (await SendAsync<AcknowledgeAllResult>(HttpMethod.Post, "api/v1/staff/portal/records/acknowledge-all")).Marked;
    private sealed record AcknowledgeAllResult(int Marked);
}
