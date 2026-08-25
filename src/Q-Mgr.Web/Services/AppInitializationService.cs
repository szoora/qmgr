using Blazored.LocalStorage;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Service that runs on application startup to initialize tokens from localStorage
/// This ensures tokens are available in TokenStorageService before any API calls are made
/// </summary>
public interface IAppInitializationService
{
    Task InitializeAsync();
}

public class AppInitializationService : IAppInitializationService
{
    private readonly ILocalStorageService _localStorage;
    private readonly ITokenStorageService _tokenStorage;
    private readonly ILogger<AppInitializationService> _logger;
    private bool _initialized = false;

    private const string AccessTokenKey = "access_token";
    private const string RefreshTokenKey = "refresh_token";
    private const string UserInfoKey = "user_info";

    public AppInitializationService(
        ILocalStorageService localStorage,
        ITokenStorageService tokenStorage,
        ILogger<AppInitializationService> logger)
    {
        _localStorage = localStorage;
        _tokenStorage = tokenStorage;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        try
        {
            _logger.LogInformation("Initializing application - loading tokens from localStorage");

            // Load tokens from localStorage into in-memory storage
            var accessToken = await _localStorage.GetItemAsync<string>(AccessTokenKey);
            var refreshToken = await _localStorage.GetItemAsync<string>(RefreshTokenKey);
            var userInfo = await _localStorage.GetItemAsync<UserInfo>(UserInfoKey);

            if (!string.IsNullOrEmpty(accessToken))
            {
                _tokenStorage.AccessToken = accessToken;
                _tokenStorage.RefreshToken = refreshToken;
                _tokenStorage.UserInfo = userInfo;

                _logger.LogInformation("Tokens loaded successfully for user: {Email}", userInfo?.Email);
            }
            else
            {
                _logger.LogInformation("No tokens found in localStorage - user not authenticated");
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during application initialization");
            // Don't throw - allow app to continue even if token loading fails
        }
    }
}
