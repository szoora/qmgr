using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

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

    // Structure
    Task<List<DepartmentDto>> GetDepartmentsAsync(Guid branchId, bool includeInactive = false);
    Task<DepartmentDto> CreateDepartmentAsync(Guid branchId, SaveDepartmentRequest request);
    Task<DepartmentDto> UpdateDepartmentAsync(Guid branchId, Guid id, SaveDepartmentRequest request);
    Task<DepartmentDto> ToggleDepartmentAsync(Guid branchId, Guid id);
    Task<StaffDirectoryDto> GetDirectoryAsync(Guid branchId, string? period = null);
    Task<StaffMemberDto> UpdateMemberStructureAsync(Guid branchId, Guid userId, UpdateStaffStructureRequest request);
    Task<StructureCoverageDto> GetCoverageAsync(Guid branchId, string? period = null);

    // Records
    Task<StaffRecordSearchResultDto> SearchRecordsAsync(Guid branchId, Guid? subjectUserId = null, Guid? parameterId = null, DateTime? from = null, DateTime? to = null, string? status = null, string? q = null, int page = 1, int pageSize = 25);
    Task<StaffPerformanceRecordDto> CreateRecordAsync(Guid branchId, CreateStaffRecordRequest request);
    Task<StaffPerformanceRecordDto> GetRecordAsync(Guid branchId, Guid id);
    Task<StaffPerformanceRecordDto> FinalizeRecordAsync(Guid branchId, Guid id);
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
    Task<List<StaffDutyDto>> GetDutiesAsync(Guid branchId, DateTime from, DateTime to);
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
    Task<RosterImportJobDto> StartStaffImportAsync(Guid branchId, StartStaffImportRequest request);
    Task<List<RosterImportJobDto>> GetStaffImportJobsAsync(Guid branchId, int limit = 50);
    Task<RosterImportJobDto> GetStaffImportJobAsync(Guid branchId, Guid jobId);
    Task<List<RosterImportJobEntryDto>> GetStaffImportJobEntriesAsync(Guid branchId, Guid jobId, int limit = 500);

    // Portal (the caller's own)
    Task<StaffPortalDto> GetPortalAsync();
    Task<StaffRecordSearchResultDto> GetMyRecordsAsync(int page = 1, int pageSize = 25);
    Task<ActivityLogPageDto> GetMyActivityAsync(int page = 1, int pageSize = 50);
    Task<List<StaffColleagueDto>> GetColleaguesAsync();
    Task<StaffFileExportDto> ExportMyFileAsync();
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

    private static string Q(params (string Key, string? Value)[] parts)
    {
        var items = parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return items.Count == 0 ? string.Empty : "?" + string.Join("&", items);
    }

    private static string? D(DateTime? d) => d?.ToUniversalTime().ToString("O");

    // ---- Parameters and policy ----
    public Task<List<PerformanceParameterDto>> GetParametersAsync(bool includeInactive = false) => GetAsync<List<PerformanceParameterDto>>($"api/v1/staff/parameters{Q(("includeInactive", includeInactive ? "true" : null))}");
    public Task<PerformanceParameterDto> CreateParameterAsync(SavePerformanceParameterRequest request) => SendAsync<PerformanceParameterDto>(HttpMethod.Post, "api/v1/staff/parameters", request);
    public Task<PerformanceParameterDto> UpdateParameterAsync(Guid id, SavePerformanceParameterRequest request) => SendAsync<PerformanceParameterDto>(HttpMethod.Put, $"api/v1/staff/parameters/{id}", request);
    public Task<PerformanceParameterDto> ToggleParameterAsync(Guid id) => SendAsync<PerformanceParameterDto>(HttpMethod.Patch, $"api/v1/staff/parameters/{id}/toggle");
    public Task<StaffPerformancePolicyDto> GetPolicyAsync() => GetAsync<StaffPerformancePolicyDto>("api/v1/staff/policy");
    public Task<StaffPerformancePolicyDto> UpdatePolicyAsync(StaffPerformancePolicyDto policy) => SendAsync<StaffPerformancePolicyDto>(HttpMethod.Put, "api/v1/staff/policy", policy);

    // ---- Structure ----
    public Task<List<DepartmentDto>> GetDepartmentsAsync(Guid branchId, bool includeInactive = false) => GetAsync<List<DepartmentDto>>($"{B(branchId)}/structure/departments{Q(("includeInactive", includeInactive ? "true" : null))}");
    public Task<DepartmentDto> CreateDepartmentAsync(Guid branchId, SaveDepartmentRequest request) => SendAsync<DepartmentDto>(HttpMethod.Post, $"{B(branchId)}/structure/departments", request);
    public Task<DepartmentDto> UpdateDepartmentAsync(Guid branchId, Guid id, SaveDepartmentRequest request) => SendAsync<DepartmentDto>(HttpMethod.Put, $"{B(branchId)}/structure/departments/{id}", request);
    public Task<DepartmentDto> ToggleDepartmentAsync(Guid branchId, Guid id) => SendAsync<DepartmentDto>(HttpMethod.Patch, $"{B(branchId)}/structure/departments/{id}/toggle");
    public Task<StaffDirectoryDto> GetDirectoryAsync(Guid branchId, string? period = null) => GetAsync<StaffDirectoryDto>($"{B(branchId)}/structure/members{Q(("period", period))}");
    public Task<StaffMemberDto> UpdateMemberStructureAsync(Guid branchId, Guid userId, UpdateStaffStructureRequest request) => SendAsync<StaffMemberDto>(HttpMethod.Put, $"{B(branchId)}/structure/members/{userId}", request);
    public Task<StructureCoverageDto> GetCoverageAsync(Guid branchId, string? period = null) => GetAsync<StructureCoverageDto>($"{B(branchId)}/structure/coverage{Q(("period", period))}");

    // ---- Records ----
    public Task<StaffRecordSearchResultDto> SearchRecordsAsync(Guid branchId, Guid? subjectUserId = null, Guid? parameterId = null, DateTime? from = null, DateTime? to = null, string? status = null, string? q = null, int page = 1, int pageSize = 25)
        => GetAsync<StaffRecordSearchResultDto>($"{B(branchId)}/records{Q(("subjectUserId", subjectUserId?.ToString()), ("parameterId", parameterId?.ToString()), ("from", D(from)), ("to", D(to)), ("status", status), ("q", q), ("page", page.ToString()), ("pageSize", pageSize.ToString()))}");
    public Task<StaffPerformanceRecordDto> CreateRecordAsync(Guid branchId, CreateStaffRecordRequest request) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Post, $"{B(branchId)}/records", request);
    public Task<StaffPerformanceRecordDto> GetRecordAsync(Guid branchId, Guid id) => GetAsync<StaffPerformanceRecordDto>($"{B(branchId)}/records/{id}");
    public Task<StaffPerformanceRecordDto> FinalizeRecordAsync(Guid branchId, Guid id) => SendAsync<StaffPerformanceRecordDto>(HttpMethod.Post, $"{B(branchId)}/records/{id}/finalize");
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
    public Task<List<StaffDutyDto>> GetDutiesAsync(Guid branchId, DateTime from, DateTime to) => GetAsync<List<StaffDutyDto>>($"{B(branchId)}/duties{Q(("from", D(from)), ("to", D(to)))}");
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
    public Task<ActivityLogPageDto> GetActivityAsync(Guid branchId, int page = 1, int pageSize = 50, Guid? userId = null, string? action = null)
        => GetAsync<ActivityLogPageDto>($"{B(branchId)}/activity{Q(("page", page.ToString()), ("pageSize", pageSize.ToString()), ("userId", userId?.ToString()), ("action", action))}");
    public Task<RosterImportJobDto> StartStaffImportAsync(Guid branchId, StartStaffImportRequest request) => SendAsync<RosterImportJobDto>(HttpMethod.Post, $"{B(branchId)}/import-jobs", request);
    public Task<List<RosterImportJobDto>> GetStaffImportJobsAsync(Guid branchId, int limit = 50) => GetAsync<List<RosterImportJobDto>>($"{B(branchId)}/import-jobs?limit={limit}");
    public Task<RosterImportJobDto> GetStaffImportJobAsync(Guid branchId, Guid jobId) => GetAsync<RosterImportJobDto>($"{B(branchId)}/import-jobs/{jobId}");
    public Task<List<RosterImportJobEntryDto>> GetStaffImportJobEntriesAsync(Guid branchId, Guid jobId, int limit = 500) => GetAsync<List<RosterImportJobEntryDto>>($"{B(branchId)}/import-jobs/{jobId}/entries?limit={limit}");

    // ---- Portal ----
    public Task<StaffPortalDto> GetPortalAsync() => GetAsync<StaffPortalDto>("api/v1/staff/portal");
    public Task<StaffRecordSearchResultDto> GetMyRecordsAsync(int page = 1, int pageSize = 25) => GetAsync<StaffRecordSearchResultDto>($"api/v1/staff/portal/records{Q(("page", page.ToString()), ("pageSize", pageSize.ToString()))}");
    public Task<ActivityLogPageDto> GetMyActivityAsync(int page = 1, int pageSize = 50) => GetAsync<ActivityLogPageDto>($"api/v1/staff/portal/activity{Q(("page", page.ToString()), ("pageSize", pageSize.ToString()))}");
    public Task<List<StaffColleagueDto>> GetColleaguesAsync() => GetAsync<List<StaffColleagueDto>>("api/v1/staff/portal/colleagues");
    public Task<StaffFileExportDto> ExportMyFileAsync() => GetAsync<StaffFileExportDto>("api/v1/staff/portal/export");
}
