using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// The signed-in person's OWN account: sign-in details, contact detail and the devices holding a
/// session. Every write throws <see cref="InvalidOperationException"/> carrying the server's own
/// words, like its neighbours (never the IQueueApiService swallow-and-return-false shape).
///
/// <para>One home since 2026-09-25, when the account page and My file were split: the page used to
/// talk to the API through raw HttpClient calls with a private copy of the profile DTO, and My file
/// wrote contact detail through the staff portal, which a tenant without Welfare &amp; Performance
/// does not have.</para>
/// </summary>
public interface IAccountApiService
{
    Task<ProfileDto> GetProfileAsync();

    /// <summary>The sign-in and recovery address — the one identity field a person maintains themselves.</summary>
    Task<ProfileDto> UpdateEmailAsync(string email);

    Task ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword);

    Task<StaffContactDto> GetContactAsync();
    Task<StaffContactDto> UpdateContactAsync(UpdateStaffContactRequest request);

    Task<List<DeviceSessionDto>> GetDevicesAsync();

    /// <summary>Revokes every device's session AND this browser's; the caller then signs out locally.</summary>
    Task<string?> SignOutEverywhereAsync();
}

public class AccountApiService : IAccountApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public AccountApiService(HttpClient http, JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    public async Task<ProfileDto> GetProfileAsync()
        => await ReadAsync<ProfileDto>(await _http.GetAsync("api/v1/profile"));

    public async Task<ProfileDto> UpdateEmailAsync(string email)
        => await ReadAsync<ProfileDto>(await _http.PutAsJsonAsync("api/v1/profile", new { email }, _json));

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword)
        => await EnsureAsync(await _http.PutAsJsonAsync("api/v1/profile/password",
            new { currentPassword, newPassword, confirmPassword }, _json));

    public async Task<StaffContactDto> GetContactAsync()
        => await ReadAsync<StaffContactDto>(await _http.GetAsync("api/v1/profile/contact"));

    public async Task<StaffContactDto> UpdateContactAsync(UpdateStaffContactRequest request)
        => await ReadAsync<StaffContactDto>(await _http.PutAsJsonAsync("api/v1/profile/contact", request, _json));

    public async Task<List<DeviceSessionDto>> GetDevicesAsync()
    {
        var envelope = await ReadAsync<MobileEnvelope<List<DeviceSessionDto>>>(await _http.GetAsync("api/v1/auth/devices"));
        return envelope.Data ?? new List<DeviceSessionDto>();
    }

    public async Task<string?> SignOutEverywhereAsync()
    {
        var response = await _http.PostAsJsonAsync("api/v1/auth/logout", new MobileLogoutRequest { AllDevices = true }, _json);
        await EnsureAsync(response);
        try
        {
            var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(_json);
            return body != null && body.TryGetValue("message", out var m) ? m : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(_json)
            ?? throw new InvalidOperationException("The server returned an empty answer.");
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }
}
