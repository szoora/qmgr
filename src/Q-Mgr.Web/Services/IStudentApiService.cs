using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface IStudentApiService
{
    Task<List<StudentDto>> GetStudentsAsync(Guid branchId, bool includeInactive = false);

    /// <summary>Combined student+guardian typeahead for the visiting-day check-in flow.</summary>
    Task<List<StudentGuardianSearchResultDto>> SearchAsync(Guid branchId, string query);

    Task<StudentDto> CreateStudentAsync(Guid branchId, CreateStudentRequest request);
    Task<StudentDto?> UpdateStudentAsync(Guid branchId, Guid studentId, UpdateStudentRequest request);
    Task<bool> DeactivateStudentAsync(Guid branchId, Guid studentId);
    Task<StudentGuardianDto> AddGuardianAsync(Guid branchId, Guid studentId, AddGuardianRequest request);
    Task<bool> RemoveGuardianAsync(Guid branchId, Guid studentId, Guid guardianLinkId);

    /// <summary>Starts a background bulk-import job and returns immediately — poll or listen for RosterImportProgress over SignalR for live status.</summary>
    Task<RosterImportJobDto> StartImportAsync(Guid branchId, StartRosterImportRequest request);
    Task<List<RosterImportJobDto>> GetImportJobsAsync(Guid branchId);
    Task<RosterImportJobDto?> GetImportJobAsync(Guid branchId, Guid jobId);
    Task<List<RosterImportJobEntryDto>> GetImportJobEntriesAsync(Guid branchId, Guid jobId);

    Task<ClassColorSettingsDto> GetClassColorsAsync(Guid branchId);
    Task<ClassColorSettingsDto?> UpdateClassColorsAsync(Guid branchId, ClassColorSettingsDto request);
}

public class StudentApiService : IStudentApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<StudentApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public StudentApiService(HttpClient httpClient, ILogger<StudentApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    private async Task<string> ReadProblemTitleAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsLite>(_jsonOptions);
            return problem?.Title ?? $"Request failed ({(int)response.StatusCode})";
        }
        catch
        {
            return $"Request failed ({(int)response.StatusCode})";
        }
    }

    private record ProblemDetailsLite(string? Title, string? Detail);

    public async Task<List<StudentDto>> GetStudentsAsync(Guid branchId, bool includeInactive = false)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<StudentDto>>(
                $"api/v1/branches/{branchId}/students?includeInactive={includeInactive}", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get students for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<List<StudentGuardianSearchResultDto>> SearchAsync(Guid branchId, string query)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/students/search?q={Uri.EscapeDataString(query)}";
            return await _httpClient.GetFromJsonAsync<List<StudentGuardianSearchResultDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search roster for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<StudentDto> CreateStudentAsync(Guid branchId, CreateStudentRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/students", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ReadProblemTitleAsync(response));
        return (await response.Content.ReadFromJsonAsync<StudentDto>(_jsonOptions))!;
    }

    public async Task<StudentDto?> UpdateStudentAsync(Guid branchId, Guid studentId, UpdateStudentRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/students/{studentId}", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<StudentDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update student {StudentId}", studentId);
            return null;
        }
    }

    public async Task<bool> DeactivateStudentAsync(Guid branchId, Guid studentId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/branches/{branchId}/students/{studentId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deactivate student {StudentId}", studentId);
            return false;
        }
    }

    public async Task<StudentGuardianDto> AddGuardianAsync(Guid branchId, Guid studentId, AddGuardianRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/students/{studentId}/guardians", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ReadProblemTitleAsync(response));
        return (await response.Content.ReadFromJsonAsync<StudentGuardianDto>(_jsonOptions))!;
    }

    public async Task<bool> RemoveGuardianAsync(Guid branchId, Guid studentId, Guid guardianLinkId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/branches/{branchId}/students/{studentId}/guardians/{guardianLinkId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove guardian {GuardianLinkId}", guardianLinkId);
            return false;
        }
    }

    public async Task<RosterImportJobDto> StartImportAsync(Guid branchId, StartRosterImportRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/students/import-jobs", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ReadProblemTitleAsync(response));
        return (await response.Content.ReadFromJsonAsync<RosterImportJobDto>(_jsonOptions))!;
    }

    public async Task<List<RosterImportJobDto>> GetImportJobsAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<RosterImportJobDto>>(
                $"api/v1/branches/{branchId}/students/import-jobs", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get import jobs for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<RosterImportJobDto?> GetImportJobAsync(Guid branchId, Guid jobId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<RosterImportJobDto>(
                $"api/v1/branches/{branchId}/students/import-jobs/{jobId}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get import job {JobId}", jobId);
            return null;
        }
    }

    public async Task<List<RosterImportJobEntryDto>> GetImportJobEntriesAsync(Guid branchId, Guid jobId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<RosterImportJobEntryDto>>(
                $"api/v1/branches/{branchId}/students/import-jobs/{jobId}/entries", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get import job entries for job {JobId}", jobId);
            return new();
        }
    }

    public async Task<ClassColorSettingsDto> GetClassColorsAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ClassColorSettingsDto>(
                $"api/v1/branches/{branchId}/students/class-colors", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get class colors for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<ClassColorSettingsDto?> UpdateClassColorsAsync(Guid branchId, ClassColorSettingsDto request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/students/class-colors", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<ClassColorSettingsDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update class colors for branch {BranchId}", branchId);
            return null;
        }
    }
}
