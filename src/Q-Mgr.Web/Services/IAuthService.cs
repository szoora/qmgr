using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Logging;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface IAuthService
{
    Task<IdentifyUserResponse?> IdentifyUserAsync(string email);
    Task<bool> LoginAsync(string email, string password, Guid? organizationId = null);
    Task LogoutAsync();
    Task<UserInfo?> GetCurrentUserAsync();
    Task<string?> GetAccessTokenAsync();
    Task<bool> IsAuthenticatedAsync();
    Task<string?> RefreshTokenAsync();

    /// <summary>
    /// Re-fetches the signed-in user (incl. role and permissions) from GET api/v1/auth/me and
    /// replaces the stored copy. Returns null when not signed in or the call fails.
    /// </summary>
    Task<UserInfo?> RefreshCurrentUserAsync();
}

public record IdentifyUserResponse
{
    public string Email { get; init; } = string.Empty;
    public Guid OrganizationId { get; init; }
    public string OrganizationName { get; init; } = string.Empty;
    public string OrganizationSlug { get; init; } = string.Empty;
    public bool HasPassword { get; init; }
    public bool SsoEnabled { get; init; }
    public string? SsoUrl { get; init; }
}

public class AuthService : IAuthService
{
    private readonly HttpClient _httpClient;
    private readonly ILocalStorageService _localStorage;
    private readonly ITokenStorageService _tokenStorage;
    private readonly ILogger<AuthService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    private const string AccessTokenKey = "access_token";
    private const string RefreshTokenKey = "refresh_token";
    private const string UserInfoKey = "user_info";

    public AuthService(
        IHttpClientFactory httpClientFactory,
        ILocalStorageService localStorage,
        ITokenStorageService tokenStorage,
        ILogger<AuthService> logger,
        JsonSerializerOptions jsonOptions)
    {
        // Use QMgrAuthApi to avoid circular dependency with AuthenticationMessageHandler
        _httpClient = httpClientFactory.CreateClient("QMgrAuthApi");
        _localStorage = localStorage;
        _tokenStorage = tokenStorage;
        _logger = logger;
        _jsonOptions = jsonOptions;
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

    public async Task<bool> LoginAsync(string email, string password, Guid? organizationId = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/v1/auth/login", new
            {
                email,
                password,
                organizationId
            });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Login failed for user {Email}", email);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions);
            if (result == null) return false;

            // Save to localStorage for persistence
            await _localStorage.SetItemAsync(AccessTokenKey, result.AccessToken);
            await _localStorage.SetItemAsync(RefreshTokenKey, result.RefreshToken);
            await _localStorage.SetItemAsync(UserInfoKey, result.User);

            // Save to in-memory storage for HTTP handler access
            _tokenStorage.AccessToken = result.AccessToken;
            _tokenStorage.RefreshToken = result.RefreshToken;
            _tokenStorage.UserInfo = result.User;

            _logger.LogInformation("User {Email} logged in successfully", email);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login error for user {Email}", email);
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        await _localStorage.RemoveItemAsync(AccessTokenKey);
        await _localStorage.RemoveItemAsync(RefreshTokenKey);
        await _localStorage.RemoveItemAsync(UserInfoKey);

        _tokenStorage.Clear();

        _logger.LogInformation("User logged out");
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

            var response = await _httpClient.PostAsJsonAsync("api/v1/auth/refresh", new { refreshToken });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed with status {StatusCode}", response.StatusCode);
                await LogoutAsync();
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
    }
}
