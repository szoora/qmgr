using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

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

    /// <summary>Relationship, contact ordering, custody and the per-child contact restriction.</summary>
    Task<StudentGuardianDto?> UpdateGuardianLinkAsync(Guid branchId, Guid studentId, Guid guardianLinkId, UpdateGuardianLinkRequest request);

    Task<List<StudentFlagDto>> GetFlagsAsync(Guid branchId, Guid studentId, bool includeEnded = false);
    Task<StudentFlagDto> RaiseFlagAsync(Guid branchId, Guid studentId, CreateStudentFlagRequest request);
    Task UpdateFlagAsync(Guid branchId, Guid studentId, Guid flagId, UpdateStudentFlagRequest request);
    Task EndFlagAsync(Guid branchId, Guid studentId, Guid flagId, EndStudentFlagRequest request);

    /// <summary>The one-page picture — assembled server-side in a single call.</summary>
    Task<StudentPictureDto?> GetStudentPictureAsync(Guid branchId, Guid studentId);

    /// <summary>Records (given=true) or withdraws (given=false) a student's data-processing consent — see Student.DataConsentGivenAt.</summary>
    Task<StudentDto?> UpdateConsentAsync(Guid branchId, Guid studentId, UpdateStudentConsentRequest request);

    /// <summary>Subject-access-request export: one JSON document of everything held about the student. Null on any failure (permission, not found, network) — the caller shows a generic toast.</summary>
    Task<StudentDataExportFile?> ExportStudentDataAsync(Guid branchId, Guid studentId);

    /// <summary>Starts a background bulk-import job and returns immediately — poll or listen for RosterImportProgress over SignalR for live status.</summary>
    Task<RosterImportJobDto> StartImportAsync(Guid branchId, StartRosterImportRequest request);

    /// <summary>Same job pipeline as StartImportAsync, but for historical welfare-ledger rows (Kind = Welfare) — same progress event, same entries log.</summary>
    Task<RosterImportJobDto> StartWelfareImportAsync(Guid branchId, StartWelfareImportRequest request);

    /// <summary>Import history; pass <paramref name="kind"/> so each page lists only its own uploads (roster vs. welfare history share one job table).</summary>
    Task<List<RosterImportJobDto>> GetImportJobsAsync(Guid branchId, RosterImportKind? kind = null);
    Task<RosterImportJobDto?> GetImportJobAsync(Guid branchId, Guid jobId);
    Task<List<RosterImportJobEntryDto>> GetImportJobEntriesAsync(Guid branchId, Guid jobId);

    /// <summary>The branch's configurable lists. Seeded from what students already hold on a branch that has never opened the editor.</summary>
    Task<BranchVocabulariesDto> GetVocabulariesAsync(Guid branchId);

    Task<UpdateBranchVocabulariesResultDto> UpdateVocabulariesAsync(Guid branchId, UpdateBranchVocabulariesRequest request);

    Task<Dictionary<string, List<VocabularyUsageDto>>> GetVocabularyUsageAsync(Guid branchId);

    Task<ClassColorSettingsDto> GetClassColorsAsync(Guid branchId);
    Task<ClassColorSettingsDto?> UpdateClassColorsAsync(Guid branchId, ClassColorSettingsDto request);

    Task<PrintLetterheadDto?> GetPrintLetterheadAsync(Guid branchId);
}

/// <summary>A downloaded SAR export — the JSON bytes plus the server-suggested filename (from Content-Disposition), so the browser download keeps the "student-{code}-data-export.json" name the API chose.</summary>
public record StudentDataExportFile(byte[] Content, string FileName);

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
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<StudentDto>(_jsonOptions))!;
    }

    /// <summary>
    /// Throws with the server's own message rather than returning null on failure, matching
    /// CreateStudentAsync. It previously swallowed everything into a null, which meant the
    /// server's validation messages — "Date of birth cannot be in the future", "A student with
    /// code X already exists" — reached the user as a generic failure with no way to tell what
    /// was actually wrong. Server-side validation nobody can read is not validation.
    /// </summary>
    public async Task<StudentDto?> UpdateStudentAsync(Guid branchId, Guid studentId, UpdateStudentRequest request)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/students/{studentId}", request, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<StudentDto>(_jsonOptions);
    }

    public async Task<StudentGuardianDto?> UpdateGuardianLinkAsync(Guid branchId, Guid studentId, Guid guardianLinkId, UpdateGuardianLinkRequest request)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/students/{studentId}/guardians/{guardianLinkId}", request, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<StudentGuardianDto>(_jsonOptions);
    }

    public async Task<List<StudentFlagDto>> GetFlagsAsync(Guid branchId, Guid studentId, bool includeEnded = false)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<StudentFlagDto>>(
                $"api/v1/branches/{branchId}/students/{studentId}/flags?includeEnded={includeEnded}", _jsonOptions);
            return result ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load flags for student {StudentId}", studentId);
            return new();
        }
    }

    public async Task<StudentFlagDto> RaiseFlagAsync(Guid branchId, Guid studentId, CreateStudentFlagRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/students/{studentId}/flags", request, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<StudentFlagDto>(_jsonOptions))!;
    }

    public async Task UpdateFlagAsync(Guid branchId, Guid studentId, Guid flagId, UpdateStudentFlagRequest request)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/students/{studentId}/flags/{flagId}", request, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    public async Task EndFlagAsync(Guid branchId, Guid studentId, Guid flagId, EndStudentFlagRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/students/{studentId}/flags/{flagId}/end", request, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    public async Task<BranchVocabulariesDto> GetVocabulariesAsync(Guid branchId)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<BranchVocabulariesDto>(
                $"api/v1/branches/{branchId}/students/vocabularies", _jsonOptions);
            return result ?? new BranchVocabulariesDto();
        }
        catch (Exception ex)
        {
            // An empty vocabulary degrades a picker to a free-text box rather than blocking the
            // form — losing the list is annoying, refusing to let anyone record a student is worse.
            _logger.LogError(ex, "Failed to load vocabularies for branch {BranchId}", branchId);
            return new BranchVocabulariesDto();
        }
    }

    public async Task<UpdateBranchVocabulariesResultDto> UpdateVocabulariesAsync(Guid branchId, UpdateBranchVocabulariesRequest request)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/students/vocabularies", request, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<UpdateBranchVocabulariesResultDto>(_jsonOptions))!;
    }

    public async Task<Dictionary<string, List<VocabularyUsageDto>>> GetVocabularyUsageAsync(Guid branchId)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<Dictionary<string, List<VocabularyUsageDto>>>(
                $"api/v1/branches/{branchId}/students/vocabularies/usage", _jsonOptions);
            return result ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load vocabulary usage for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<StudentPictureDto?> GetStudentPictureAsync(Guid branchId, Guid studentId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<StudentPictureDto>(
                $"api/v1/branches/{branchId}/students/{studentId}/picture", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the student picture for {StudentId}", studentId);
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
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
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

    public async Task<StudentDto?> UpdateConsentAsync(Guid branchId, Guid studentId, UpdateStudentConsentRequest request)
    {
        try
        {
            var response = await _httpClient.PatchAsJsonAsync($"api/v1/branches/{branchId}/students/{studentId}/consent", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<StudentDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update consent for student {StudentId}", studentId);
            return null;
        }
    }

    public async Task<StudentDataExportFile?> ExportStudentDataAsync(Guid branchId, Guid studentId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/v1/branches/{branchId}/students/{studentId}/data-export");
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"student-{studentId:N}-data-export.json";
            return new StudentDataExportFile(bytes, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export data for student {StudentId}", studentId);
            return null;
        }
    }

    public async Task<RosterImportJobDto> StartImportAsync(Guid branchId, StartRosterImportRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/students/import-jobs", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<RosterImportJobDto>(_jsonOptions))!;
    }

    public async Task<RosterImportJobDto> StartWelfareImportAsync(Guid branchId, StartWelfareImportRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/welfare-records/import-jobs", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<RosterImportJobDto>(_jsonOptions))!;
    }

    public async Task<List<RosterImportJobDto>> GetImportJobsAsync(Guid branchId, RosterImportKind? kind = null)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/students/import-jobs";
            if (kind.HasValue) url += $"?kind={kind.Value}";
            return await _httpClient.GetFromJsonAsync<List<RosterImportJobDto>>(url, _jsonOptions) ?? new();
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

    public async Task<PrintLetterheadDto?> GetPrintLetterheadAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<PrintLetterheadDto>(
                $"api/v1/branches/{branchId}/students/print-letterhead", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get print letterhead for branch {BranchId}", branchId);
            return null;
        }
    }
}
