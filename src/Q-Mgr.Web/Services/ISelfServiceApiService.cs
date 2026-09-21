using System.Net.Http.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// What My Workspace calls for staff self-service configuration.
///
/// EVERY WRITE THROWS with the server's own message, like every other API service in this project
/// except IQueueApiService — so a caller wrapping it in try/catch shows the real reason rather than
/// a shrug. The two CLAIM calls are the deliberate exception: they return a ClaimResultDto whose
/// refusal is the normal answer, not an error, because "already taken, ask for a swap" is
/// information the page renders in place rather than a failure to report.
/// </summary>
public interface ISelfServiceApiService
{
    Task<SelfServiceContextDto?> GetContextAsync(Guid branchId);
    Task UpdateDeclarationsAsync(Guid branchId, UpdateMyTeachingDeclarationsRequest request);

    Task<TimetableOpeningsDto?> GetOpeningsAsync(Guid branchId, string className, Guid subjectId);
    Task<ClaimResultDto> ClaimAsync(Guid branchId, ClaimSlotRequest request);
    Task<ClaimResultDto> ReleaseAsync(Guid branchId, Guid lessonId);

    Task<List<StaffConfigRequestDto>> GetRequestsAsync(Guid branchId, bool openOnly = true);
    Task<StaffConfigRequestDto?> CreateRequestAsync(Guid branchId, CreateConfigRequestRequest request);
    Task<StaffConfigRequestDto?> DecideAsync(Guid branchId, Guid id, DecideConfigRequestRequest decision);
    Task WithdrawAsync(Guid branchId, Guid id);
    Task AgreeAsync(Guid branchId, Guid id);
}

public class SelfServiceApiService : ISelfServiceApiService
{
    private readonly HttpClient _http;

    public SelfServiceApiService(HttpClient http) => _http = http;

    private static string Base(Guid branchId) => $"api/v1/branches/{branchId}/staff/self-service";

    public async Task<SelfServiceContextDto?> GetContextAsync(Guid branchId)
    {
        var response = await _http.GetAsync($"{Base(branchId)}/context");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<SelfServiceContextDto>();
    }

    public async Task UpdateDeclarationsAsync(Guid branchId, UpdateMyTeachingDeclarationsRequest request)
    {
        var response = await _http.PutAsJsonAsync($"{Base(branchId)}/declarations", request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    public async Task<TimetableOpeningsDto?> GetOpeningsAsync(Guid branchId, string className, Guid subjectId)
    {
        var url = $"{Base(branchId)}/openings?className={Uri.EscapeDataString(className)}&subjectId={subjectId}";
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<TimetableOpeningsDto>();
    }

    /// <summary>
    /// A refusal is a NORMAL answer here, carried in the body with the fresh grid attached, so the
    /// page can say "already taken" in place and offer the request that would resolve it — rather
    /// than throwing and making the reader start again.
    /// </summary>
    public async Task<ClaimResultDto> ClaimAsync(Guid branchId, ClaimSlotRequest request)
    {
        var response = await _http.PostAsJsonAsync($"{Base(branchId)}/claims", request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<ClaimResultDto>() ?? new ClaimResultDto { Ok = false, Refusal = "No answer from the server." };
    }

    public async Task<ClaimResultDto> ReleaseAsync(Guid branchId, Guid lessonId)
    {
        var response = await _http.DeleteAsync($"{Base(branchId)}/claims/{lessonId}");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<ClaimResultDto>() ?? new ClaimResultDto { Ok = false, Refusal = "No answer from the server." };
    }

    public async Task<List<StaffConfigRequestDto>> GetRequestsAsync(Guid branchId, bool openOnly = true)
    {
        var response = await _http.GetAsync($"{Base(branchId)}/requests?openOnly={openOnly.ToString().ToLowerInvariant()}");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<List<StaffConfigRequestDto>>() ?? new();
    }

    public async Task<StaffConfigRequestDto?> CreateRequestAsync(Guid branchId, CreateConfigRequestRequest request)
    {
        var response = await _http.PostAsJsonAsync($"{Base(branchId)}/requests", request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<StaffConfigRequestDto>();
    }

    public async Task<StaffConfigRequestDto?> DecideAsync(Guid branchId, Guid id, DecideConfigRequestRequest decision)
    {
        var response = await _http.PostAsJsonAsync($"{Base(branchId)}/requests/{id}/decide", decision);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<StaffConfigRequestDto>();
    }

    public async Task WithdrawAsync(Guid branchId, Guid id)
    {
        var response = await _http.PostAsync($"{Base(branchId)}/requests/{id}/withdraw", null);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    public async Task AgreeAsync(Guid branchId, Guid id)
    {
        var response = await _http.PostAsync($"{Base(branchId)}/requests/{id}/agree", null);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }
}
