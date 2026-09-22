using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Logging;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface IAuthService
{
    Task<IdentifyUserResponse?> IdentifyUserAsync(string email);
    Task<SignInOutcome> LoginAsync(string email, string password, Guid? organizationId = null);
    Task LogoutAsync();
    Task<UserInfo?> GetCurrentUserAsync();
    Task<string?> GetAccessTokenAsync();
    Task<bool> IsAuthenticatedAsync();
    Task<string?> RefreshTokenAsync();

    /// <summary>
    /// Raised once the API has REFUSED this circuit's refresh token and the local session has been
    /// cleared: the person is signed out, not short of a permission. <c>MainLayout</c> listens and
    /// sends them to the sign-in page with a return address. Not raised for a network failure, a
    /// 429 or a 5xx, which leave the session in place for the next attempt.
    /// </summary>
    event Action? SessionExpired;

    /// <summary>
    /// Raised when the stored copy of the signed-in person has been REPLACED — today, after they
    /// add a profile photograph. <c>MainLayout</c> listens and re-reads, so the header avatar
    /// follows without a reload. Not raised on sign-out; that is <see cref="SessionExpired"/>.
    /// </summary>
    event Action? CurrentUserChanged;

    /// <summary>
    /// Re-fetches the signed-in user (incl. role and permissions) from GET api/v1/auth/me and
    /// replaces the stored copy. Returns null when not signed in or the call fails.
    /// </summary>
    Task<UserInfo?> RefreshCurrentUserAsync();
}

/// <summary>
/// What a sign-in attempt came to. <see cref="MustChangePassword"/>: signed in with a temporary password;
/// the session can only set a new one (duty rota plan §12.3). <see cref="Message"/> is the API's own words
/// when it refused — "waiting for approval", "temporary password expired" — or null for a plain wrong password.
/// </summary>
public record SignInOutcome(bool Success, bool MustChangePassword = false, string? Message = null, string? ErrorCode = null);

public record IdentifyUserResponse
{
    public string Email { get; init; } = string.Empty;
    public Guid OrganizationId { get; init; }
    public string OrganizationName { get; init; } = string.Empty;
    public string OrganizationSlug { get; init; } = string.Empty;
    public bool HasPassword { get; init; }
}

public class AuthService : IAuthService
{
    private readonly HttpClient _httpClient;
    private readonly ILocalStorageService _localStorage;
    private readonly ITokenStorageService _tokenStorage;
    private readonly ILogger<AuthService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ViewerRequestContext _viewer;

    private const string AccessTokenKey = "access_token";
    private const string RefreshTokenKey = "refresh_token";
    private const string UserInfoKey = "user_info";

    public event Action? SessionExpired;
    public event Action? CurrentUserChanged;

    public AuthService(
        IHttpClientFactory httpClientFactory,
        ILocalStorageService localStorage,
        ITokenStorageService tokenStorage,
        ILogger<AuthService> logger,
        JsonSerializerOptions jsonOptions,
        ViewerRequestContext viewer)
    {
        // Use QMgrAuthApi to avoid circular dependency with AuthenticationMessageHandler
        _httpClient = httpClientFactory.CreateClient("QMgrAuthApi");
        _localStorage = localStorage;
        _tokenStorage = tokenStorage;
        _logger = logger;
        _jsonOptions = jsonOptions;
        _viewer = viewer;
    }

    private void AddViewerHeaders(HttpRequestMessage message)
    {
        var v = _viewer.Viewer;
        if (v == null) return;
        if (!string.IsNullOrWhiteSpace(v.IpAddress)) message.Headers.TryAddWithoutValidation("X-Viewer-Ip", v.IpAddress);
        // The rate-limit key: sign-in attempts are limited per browser, not across everyone at once.
        if (!string.IsNullOrWhiteSpace(v.IpAddress)) message.Headers.TryAddWithoutValidation("X-Real-IP", v.IpAddress);
        if (!string.IsNullOrWhiteSpace(v.UserAgent)) message.Headers.TryAddWithoutValidation("X-Viewer-Agent", v.UserAgent);
    }

    public async Task<IdentifyUserResponse?> IdentifyUserAsync(string email)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/v1/auth/identify", new
            {
                email
            });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("User identification failed for email {Email}", email);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<IdentifyUserResponse>(_jsonOptions);
            _logger.LogInformation("User identified: {Email}, Organization: {OrgName}",
                email, result?.OrganizationName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User identification error for {Email}", email);
            return null;
        }
    }

    public async Task<SignInOutcome> LoginAsync(string email, string password, Guid? organizationId = null)
    {
        try
        {
            // The login goes out on the auth client, which has no AuthenticationMessageHandler, so
            // it relays the browser's address itself: the sign-in is the first row of the staff
            // activity log and would otherwise name the Web server (see ViewerRequestContext).
            using var loginMessage = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login")
            {
                Content = JsonContent.Create(new { email, password, organizationId })
            };
            AddViewerHeaders(loginMessage);
            var response = await _httpClient.SendAsync(loginMessage);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Login failed for user {Email}", email);
                string? message = null, code = null;
                try
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("error", out var e)) code = e.GetString();
                    // Only the API's specific refusals are shown verbatim; a wrong password stays generic.
                    if (code != null && doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString();
                    else if (doc.RootElement.TryGetProperty("message", out var lm) && (lm.GetString() ?? "").StartsWith("Account locked", StringComparison.Ordinal)) message = lm.GetString();
                }
                catch (JsonException) { }
                return new SignInOutcome(false, Message: message, ErrorCode: code);
            }

            var result = await response.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions);
            if (result == null) return new SignInOutcome(false);

            // Save to localStorage for persistence. A password-change-only sign-in has no refresh token:
            // none is stored, so nothing can extend that session past its fifteen minutes.
            await _localStorage.SetItemAsync(AccessTokenKey, result.AccessToken);
            if (string.IsNullOrEmpty(result.RefreshToken)) await _localStorage.RemoveItemAsync(RefreshTokenKey);
            else await _localStorage.SetItemAsync(RefreshTokenKey, result.RefreshToken);
            await _localStorage.SetItemAsync(UserInfoKey, result.User);

            // Save to in-memory storage for HTTP handler access
            _tokenStorage.AccessToken = result.AccessToken;
            _tokenStorage.RefreshToken = string.IsNullOrEmpty(result.RefreshToken) ? null : result.RefreshToken;
            _tokenStorage.UserInfo = result.User;

            _logger.LogInformation("User {Email} logged in successfully{Temporary}", email, result.MustChangePassword ? " with a temporary password" : "");
            return new SignInOutcome(true, result.MustChangePassword);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login error for user {Email}", email);
            return new SignInOutcome(false, Message: "Unable to reach the server. Please try again.");
        }
    }

    public async Task LogoutAsync()
    {
        // Tell the API first, while the token is still in hand: it revokes the refresh token and
        // records the sign-out in the activity log. Best effort — a failed call must never stop
        // somebody signing out of this browser.
        try
        {
            var token = _tokenStorage.AccessToken ?? await _localStorage.GetItemAsync<string>(AccessTokenKey);
            if (!string.IsNullOrEmpty(token))
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/logout");
                message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                AddViewerHeaders(message);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _httpClient.SendAsync(message, cts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Server-side logout call failed; signing out locally regardless");
        }

        await ClearLocalSessionAsync();

        _logger.LogInformation("User logged out");
    }

    /// <summary>
    /// Forgets the session in this browser only. The in-memory store is cleared even when
    /// localStorage cannot be reached, or a dead token would keep being sent for the rest of the circuit.
    /// </summary>
    private async Task ClearLocalSessionAsync()
    {
        try
        {
            await _localStorage.RemoveItemAsync(AccessTokenKey);
            await _localStorage.RemoveItemAsync(RefreshTokenKey);
            await _localStorage.RemoveItemAsync(UserInfoKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clear the stored session from localStorage");
        }
        finally
        {
            _tokenStorage.Clear();
        }
    }

    public async Task<UserInfo?> GetCurrentUserAsync()
    {
        try
        {
            // Check in-memory storage first
            if (_tokenStorage.UserInfo != null)
            {
                return _tokenStorage.UserInfo;
            }

            // Load from localStorage and populate in-memory storage
            var user = await _localStorage.GetItemAsync<UserInfo>(UserInfoKey);
            if (user != null)
            {
                _tokenStorage.UserInfo = user;
                _tokenStorage.AccessToken = await _localStorage.GetItemAsync<string>(AccessTokenKey);
                _tokenStorage.RefreshToken = await _localStorage.GetItemAsync<string>(RefreshTokenKey);
            }

            return user;
        }
        catch
        {
            return null;
        }
    }

    public async Task<UserInfo?> RefreshCurrentUserAsync()
    {
        try
        {
            var token = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Refreshing current user failed with {StatusCode}", response.StatusCode);
                return null;
            }

            var user = await response.Content.ReadFromJsonAsync<UserInfo>(_jsonOptions);
            if (user == null) return null;

            await _localStorage.SetItemAsync(UserInfoKey, user);
            _tokenStorage.UserInfo = user;
            CurrentUserChanged?.Invoke();
            return user;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing current user failed");
            return null;
        }
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        try
        {
            // Check in-memory storage first
            if (!string.IsNullOrEmpty(_tokenStorage.AccessToken))
            {
                return _tokenStorage.AccessToken;
            }

            // Load from localStorage and populate in-memory storage
            var token = await _localStorage.GetItemAsync<string>(AccessTokenKey);
            if (!string.IsNullOrEmpty(token))
            {
                _tokenStorage.AccessToken = token;
                _tokenStorage.RefreshToken = await _localStorage.GetItemAsync<string>(RefreshTokenKey);
                _tokenStorage.UserInfo = await _localStorage.GetItemAsync<UserInfo>(UserInfoKey);
            }

            return token;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var token = await GetAccessTokenAsync();
        return !string.IsNullOrEmpty(token);
    }

    /// <summary>
    /// Exchanges the stored refresh token for a new access token. Returns the new
    /// access token on success, or null if the refresh token is missing/expired/revoked
    /// (caller should treat that as "session over" and route to login).
    /// </summary>
    public async Task<string?> RefreshTokenAsync()
    {
        try
        {
            var refreshToken = _tokenStorage.RefreshToken
                ?? await _localStorage.GetItemAsync<string>(RefreshTokenKey);

            if (string.IsNullOrEmpty(refreshToken))
                return null;

            using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/refresh")
            {
                Content = JsonContent.Create(new { refreshToken })
            };
            // Relayed like login and logout, or every refresh from this server shares one loopback
            // rate-limit bucket — and a 429 here used to sign the person out.
            AddViewerHeaders(refreshRequest);
            var response = await _httpClient.SendAsync(refreshRequest);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed with status {StatusCode}", response.StatusCode);

                // Only a refusal ends the session. A rate limit, a server error or an API restart says
                // nothing about the token, and wiping it turned a blip into a forced sign-in.
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden))
                    return null;

                // The API keeps one refresh token per user and rotates it on every use, so a second tab
                // of the same browser that refreshed a moment earlier makes this one's token stale. That
                // tab has already written the new pair to localStorage: adopt it rather than signing
                // both tabs out (which is what wiping localStorage here did).
                var stored = await _localStorage.GetItemAsync<string>(RefreshTokenKey);
                var storedAccess = await _localStorage.GetItemAsync<string>(AccessTokenKey);
                if (!string.IsNullOrEmpty(stored) && stored != refreshToken && !string.IsNullOrEmpty(storedAccess))
                {
                    _tokenStorage.AccessToken = storedAccess;
                    _tokenStorage.RefreshToken = stored;
                    _tokenStorage.UserInfo = await _localStorage.GetItemAsync<UserInfo>(UserInfoKey) ?? _tokenStorage.UserInfo;
                    _logger.LogInformation("Refresh token was rotated by another tab; adopted its session");
                    return storedAccess;
                }

                // Local only, never LogoutAsync: its server call revokes the user's one refresh token,
                // which after a sign-in on another device is THAT device's live session.
                await ClearLocalSessionAsync();
                _logger.LogInformation("Session ended: the refresh token was refused");
                SessionExpired?.Invoke();
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions);
            if (result == null) return null;

            await _localStorage.SetItemAsync(AccessTokenKey, result.AccessToken);
            await _localStorage.SetItemAsync(RefreshTokenKey, result.RefreshToken);
            await _localStorage.SetItemAsync(UserInfoKey, result.User);

            _tokenStorage.AccessToken = result.AccessToken;
            _tokenStorage.RefreshToken = result.RefreshToken;
            _tokenStorage.UserInfo = result.User;

            _logger.LogInformation("Access token refreshed successfully");
            return result.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh error");
            return null;
        }
    }

    private record LoginResponse
    {
        public string AccessToken { get; init; } = string.Empty;
        public string RefreshToken { get; init; } = string.Empty;
        public UserInfo? User { get; init; }
        public bool MustChangePassword { get; init; }
    }
}
