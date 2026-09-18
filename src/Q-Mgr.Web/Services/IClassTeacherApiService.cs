using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Class-teacher assignments and the coverage report.
///
/// THROWS on failure, carrying the server's own message — the convention every Web API service in
/// this project follows EXCEPT <c>IQueueApiService</c>, which catches everything and returns
/// false/null and has already cost this codebase a real bug (CounterTerminal reporting success on
/// a failed call). A caller here wraps this in try/catch and shows the real reason.
/// </summary>
public interface IClassTeacherApiService
{
    Task<List<ClassTeacherDto>> GetAssignmentsAsync(Guid branchId, bool includeEnded = false);

    /// <summary>What the CALLER teaches. Gated on welfare.view, so a class teacher can read their own.</summary>
    Task<List<ClassTeacherDto>> GetMineAsync(Guid branchId);

    /// <summary>Every configured class, who holds it, and every way this feature fails silently.</summary>
    Task<ClassTeacherCoverageReportDto> GetCoverageAsync(Guid branchId);

    Task<List<ClassTeacherDto>> GetHistoryAsync(Guid branchId, string? className = null);

    Task<ClassTeacherDto> AssignAsync(Guid branchId, AssignClassTeacherRequest request);

    /// <summary>A subject teacher: the Teaching tier for that class (roster basics, never welfare).</summary>
    Task<ClassTeacherDto> AssignSubjectTeacherAsync(Guid branchId, AssignSubjectTeacherRequest request);

    /// <summary>Changes the planned periods a week on a live subject-teacher assignment. Everything else is end-and-reassign.</summary>
    Task<ClassTeacherDto> UpdatePeriodsAsync(Guid branchId, ClassTeacherDto assignment, int? periodsPerWeek);

    /// <summary>What a teacher teaches, grouped by subject. Null <paramref name="userId"/> is the caller. Somebody out of scope is a 404, thrown.</summary>
    Task<List<TeachingSummaryDto>> GetTeachingAsync(Guid branchId, Guid? userId = null);

    /// <summary>Ends an assignment. Never deletes it — the history of who could see a child's file is the audit answer.</summary>
    Task<ClassTeacherDto> EndAsync(Guid branchId, Guid assignmentId, EndClassTeacherRequest request);

    Task UpdateContactAsync(Guid branchId, Guid userId, UpdateStaffContactRequest request);
}

public class ClassTeacherApiService : IClassTeacherApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ClassTeacherApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public ClassTeacherApiService(HttpClient httpClient, ILogger<ClassTeacherApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public async Task<List<ClassTeacherDto>> GetAssignmentsAsync(Guid branchId, bool includeEnded = false)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<ClassTeacherDto>>(
                $"api/v1/branches/{branchId}/class-teachers?includeEnded={includeEnded}", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load class teachers for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<List<ClassTeacherDto>> GetMineAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<ClassTeacherDto>>(
                $"api/v1/branches/{branchId}/class-teachers/mine", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load own class assignments for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<ClassTeacherCoverageReportDto> GetCoverageAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ClassTeacherCoverageReportDto>(
                $"api/v1/branches/{branchId}/class-teachers/coverage", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load class-teacher coverage for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<List<ClassTeacherDto>> GetHistoryAsync(Guid branchId, string? className = null)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/class-teachers/history";
            if (!string.IsNullOrWhiteSpace(className)) url += $"?className={Uri.EscapeDataString(className)}";
            return await _httpClient.GetFromJsonAsync<List<ClassTeacherDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load class-teacher history for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<ClassTeacherDto> AssignAsync(Guid branchId, AssignClassTeacherRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/class-teachers", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<ClassTeacherDto>(_jsonOptions))!;
    }

    public async Task<ClassTeacherDto> AssignSubjectTeacherAsync(Guid branchId, AssignSubjectTeacherRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/class-teachers/subject-teachers", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<ClassTeacherDto>(_jsonOptions))!;
    }

    public async Task<ClassTeacherDto> UpdatePeriodsAsync(Guid branchId, ClassTeacherDto assignment, int? periodsPerWeek)
    {
        var response = await _httpClient.PatchAsJsonAsync($"api/v1/branches/{branchId}/class-teachers/{assignment.Id}/periods",
            new UpdateSubjectPeriodsRequest { PeriodsPerWeek = periodsPerWeek }, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<ClassTeacherDto>(_jsonOptions))!;
    }

    public async Task<List<TeachingSummaryDto>> GetTeachingAsync(Guid branchId, Guid? userId = null)
    {
        var url = $"api/v1/branches/{branchId}/class-teachers/teaching";
        if (userId is { } id) url += $"?userId={id}";
        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<List<TeachingSummaryDto>>(_jsonOptions) ?? new();
    }

    public async Task<ClassTeacherDto> EndAsync(Guid branchId, Guid assignmentId, EndClassTeacherRequest request)
    {
        // DELETE with a body — the reason is part of the audit trail, not a query string, and a
        // query string is the wrong place for free text that ends up in a log.
        var message = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/branches/{branchId}/class-teachers/{assignmentId}")
        {
            Content = JsonContent.Create(request, options: _jsonOptions)
        };
        var response = await _httpClient.SendAsync(message);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<ClassTeacherDto>(_jsonOptions))!;
    }

    public async Task UpdateContactAsync(Guid branchId, Guid userId, UpdateStaffContactRequest request)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/class-teachers/staff/{userId}/contact", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }
}
