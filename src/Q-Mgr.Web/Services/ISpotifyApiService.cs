using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface ISpotifyApiService
{
    // Platform admin (Super Admin only — API-side [RequirePermission] enforces this)
    Task<SpotifyConnectionStatusDto?> GetStatusAsync();
    Task<string?> GetAuthorizeUrlAsync();
    Task<bool> CompleteConnectionAsync(string code, string state);
    Task<bool> DisconnectAsync();

    // Any authenticated user — read-only, to pick a playlist for their own gallery
    Task<List<SpotifyPlaylistDto>> GetPlaylistsAsync();

    // Anonymous — used by public signage display pages to bootstrap the Web Playback SDK
    Task<string?> GetPlaybackTokenAsync();
}

public class SpotifyApiService : ISpotifyApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SpotifyApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public SpotifyApiService(HttpClient httpClient, ILogger<SpotifyApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public async Task<SpotifyConnectionStatusDto?> GetStatusAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SpotifyConnectionStatusDto>("api/v1/platform/spotify/status", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Spotify connection status");
            return null;
        }
    }

    public async Task<string?> GetAuthorizeUrlAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/v1/platform/spotify/connect");
            if (!response.IsSuccessStatusCode)
                return null;

            var dto = await response.Content.ReadFromJsonAsync<SpotifyAuthorizeUrlDto>(_jsonOptions);
            return dto?.AuthorizeUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Spotify authorize URL");
            return null;
        }
    }

    public async Task<bool> CompleteConnectionAsync(string code, string state)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/v1/platform/spotify/callback",
                new { code, state }, _jsonOptions);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete Spotify connection");
            return false;
        }
    }

    public async Task<bool> DisconnectAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("api/v1/platform/spotify/disconnect", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect Spotify");
            return false;
        }
    }

    public async Task<List<SpotifyPlaylistDto>> GetPlaylistsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<SpotifyPlaylistDto>>("api/v1/spotify/playlists", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Spotify playlists");
            return new();
        }
    }

    public async Task<string?> GetPlaybackTokenAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/v1/spotify/playback-token");
            if (!response.IsSuccessStatusCode)
                return null;

            var dto = await response.Content.ReadFromJsonAsync<SpotifyPlaybackTokenDto>(_jsonOptions);
            return dto?.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Spotify playback token");
            return null;
        }
    }
}
