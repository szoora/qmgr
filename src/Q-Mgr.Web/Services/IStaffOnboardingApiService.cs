using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Staff onboarding (duty rota plan §12): the administrator's settings, join link, join requests,
/// onboarding status and re-issue; the public join page; and the caller's own first-sign-in steps.
/// Throws <see cref="InvalidOperationException"/> carrying the API's own message on failure, like its
/// neighbours — every caller wraps it and shows the reason.
/// </summary>
public interface IStaffOnboardingApiService
{
    // Administrator
    Task<StaffOnboardingSettingsDto> GetSettingsAsync();
    Task<StaffOnboardingSettingsDto> SaveSettingsAsync(SaveStaffOnboardingSettingsRequest request);
    Task<JoinLinkIssuedDto> RotateJoinLinkAsync(int? validDays = null);
    Task<StaffOnboardingSettingsDto> CreateAcceptableUseNoticeAsync();
    Task<List<JoinRequestDto>> GetJoinRequestsAsync(bool includeClosed = false);
    Task<List<string>> ApproveAsync(Guid userId, ApproveJoinRequest request);
    Task RejectAsync(Guid userId, RejectJoinRequest request);
    Task<OnboardingStatusDto> GetStatusAsync(Guid? branchId = null);
    Task<ReissueAccessResultDto> ReissueAsync(ReissueAccessRequest request);

    // Public join page
    Task<JoinLinkInfoDto?> GetJoinLinkAsync(string code);
    Task<string> SendJoinEmailCodeAsync(string code, string email);
    Task<string> ApplyAsync(string code, JoinApplicationRequest request);

    // The caller's own account
    Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword);
    Task SendPhoneCodeAsync();
    Task VerifyPhoneCodeAsync(string code);
    Task<string?> UploadPhotoAsync(Stream content, string fileName, string contentType);
}

public class StaffOnboardingApiService : IStaffOnboardingApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    private const string Base = "api/v1/staff-onboarding";

    public StaffOnboardingApiService(HttpClient http, JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }

    private async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    private async Task<string> MessageAsync(HttpResponseMessage response)
    {
        await EnsureAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
    }

    public async Task<StaffOnboardingSettingsDto> GetSettingsAsync() => await ReadAsync<StaffOnboardingSettingsDto>(await _http.GetAsync($"{Base}/settings"));
    public async Task<StaffOnboardingSettingsDto> SaveSettingsAsync(SaveStaffOnboardingSettingsRequest request) => await ReadAsync<StaffOnboardingSettingsDto>(await _http.PutAsJsonAsync($"{Base}/settings", request, _json));
    public async Task<JoinLinkIssuedDto> RotateJoinLinkAsync(int? validDays = null) => await ReadAsync<JoinLinkIssuedDto>(await _http.PostAsync($"{Base}/join-link/rotate{(validDays is { } d ? $"?validDays={d}" : "")}", null));
    public async Task<StaffOnboardingSettingsDto> CreateAcceptableUseNoticeAsync() => await ReadAsync<StaffOnboardingSettingsDto>(await _http.PostAsync($"{Base}/acceptable-use-notice", null));
    public async Task<List<JoinRequestDto>> GetJoinRequestsAsync(bool includeClosed = false) => await ReadAsync<List<JoinRequestDto>>(await _http.GetAsync($"{Base}/requests?includeClosed={includeClosed.ToString().ToLowerInvariant()}"));

    public async Task<List<string>> ApproveAsync(Guid userId, ApproveJoinRequest request)
    {
        var response = await _http.PostAsJsonAsync($"{Base}/requests/{userId}/approve", request, _json);
        await EnsureAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
            : new List<string>();
    }

    public async Task RejectAsync(Guid userId, RejectJoinRequest request) => await EnsureAsync(await _http.PostAsJsonAsync($"{Base}/requests/{userId}/reject", request, _json));
    public async Task<OnboardingStatusDto> GetStatusAsync(Guid? branchId = null) => await ReadAsync<OnboardingStatusDto>(await _http.GetAsync($"{Base}/status{(branchId is { } b ? $"?branchId={b}" : "")}"));
    public async Task<ReissueAccessResultDto> ReissueAsync(ReissueAccessRequest request) => await ReadAsync<ReissueAccessResultDto>(await _http.PostAsJsonAsync($"{Base}/reissue", request, _json));

    public async Task<JoinLinkInfoDto?> GetJoinLinkAsync(string code)
    {
        var response = await _http.GetAsync($"api/v1/public/join/{Uri.EscapeDataString(code)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        return await ReadAsync<JoinLinkInfoDto>(response);
    }

    public async Task<string> SendJoinEmailCodeAsync(string code, string email)
        => await MessageAsync(await _http.PostAsJsonAsync($"api/v1/public/join/{Uri.EscapeDataString(code)}/email-code", new JoinEmailCodeRequest { Email = email }, _json));

    public async Task<string> ApplyAsync(string code, JoinApplicationRequest request)
        => await MessageAsync(await _http.PostAsJsonAsync($"api/v1/public/join/{Uri.EscapeDataString(code)}/apply", request, _json));

    /// <summary>True when this change replaced a temporary password (the first sign-in).</summary>
    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword)
    {
        var response = await _http.PutAsJsonAsync("api/v1/profile/password", new { currentPassword, newPassword, confirmPassword }, _json);
        await EnsureAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("firstSignIn", out var f) && f.ValueKind == JsonValueKind.True;
    }

    public async Task SendPhoneCodeAsync() => await EnsureAsync(await _http.PostAsync("api/v1/profile/phone/send-code", null));
    public async Task VerifyPhoneCodeAsync(string code) => await EnsureAsync(await _http.PostAsJsonAsync("api/v1/profile/phone/verify-code", new { code }, _json));

    public async Task<string?> UploadPhotoAsync(Stream content, string fileName, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(file, "file", fileName);
        var response = await _http.PostAsync("api/v1/profile/photo", form);
        await EnsureAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("photoUrl", out var p) ? p.GetString() : null;
    }
}
