using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// What the download page and the mobile handoff need from the API.
///
/// <para><b>It THROWS on a failed write and returns null on a failed read</b> — the house pattern.
/// <c>IQueueApiService</c> is this codebase's one exception, and it is recorded as a mistake: ten of
/// its methods swallow everything and return false, so a caller cannot tell success from failure and
/// a <c>try</c>/<c>catch</c> around it is dead code. Do not add an eleventh.</para>
/// </summary>
public interface IMobileAppApiService
{
    /// <summary>
    /// The published builds for a platform. Null means the call failed, which the page distinguishes
    /// from an empty list — "we could not reach the download host" and "no build has been published"
    /// are different sentences and a reader needs the right one.
    /// </summary>
    Task<AppReleasesResponse?> GetReleasesAsync(string platform = "android");

    /// <summary>
    /// Redeem a mobile handoff code for a real session. Throws with the API's own message, because
    /// the page that calls it must redirect on failure rather than render an error.
    /// </summary>
    Task<MobileSessionResult> RedeemHandoffAsync(string code);
}

/// <summary>What <c>/mobile-session</c> needs in order to write the session and move on.</summary>
public record MobileSessionResult
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public UserInfo? User { get; init; }
}

public class MobileAppApiService : IMobileAppApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<MobileAppApiService> _log;

    public MobileAppApiService(HttpClient http, JsonSerializerOptions json, ILogger<MobileAppApiService> log)
    {
        _http = http;
        _json = json;
        _log = log;
    }

    public async Task<AppReleasesResponse?> GetReleasesAsync(string platform = "android")
    {
        try
        {
            var response = await _http.GetAsync($"api/v1/app/releases?platform={Uri.EscapeDataString(platform)}");
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Could not read app releases: {Status}", response.StatusCode);
                return null;
            }

            var envelope = await response.Content.ReadFromJsonAsync<MobileEnvelope<AppReleasesResponse>>(_json);
            return envelope?.Data;
        }
        catch (Exception ex)
        {
            // A read, so null rather than a throw: the download page has a sentence for this and a
            // stack trace on a public page tells a stranger about our infrastructure.
            _log.LogWarning(ex, "Could not read app releases");
            return null;
        }
    }

    public async Task<MobileSessionResult> RedeemHandoffAsync(string code)
    {
        var response = await _http.PostAsJsonAsync("api/v1/auth/web-session", new { code });

        if (!response.IsSuccessStatusCode)
        {
            var message = await ApiErrorService.GetErrorMessageAsync(response);
            throw new InvalidOperationException(message);
        }

        var result = await response.Content.ReadFromJsonAsync<MobileSessionResult>(_json);
        if (result is null || string.IsNullOrWhiteSpace(result.AccessToken))
            throw new InvalidOperationException("The sign-in link could not be completed.");

        return result;
    }
}
