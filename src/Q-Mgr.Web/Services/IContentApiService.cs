using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Web.Services;

public interface IContentApiService
{
    // Media
    Task<List<MediaContentDto>> GetMediaContentsAsync(Guid organizationId);
    Task<MediaContentDto?> GetMediaContentAsync(Guid mediaId);
    Task<MediaContentDto?> CreateMediaContentAsync(Guid organizationId, CreateMediaContentRequest request);
    Task<MediaContentDto?> UploadMediaContentAsync(Guid organizationId, Stream fileStream, string fileName, string? contentType, string? name);
    Task<MediaContentDto?> UpdateMediaContentAsync(Guid mediaId, UpdateMediaContentRequest request);
    Task<bool> DeleteMediaContentAsync(Guid mediaId);

    // Playlists
    Task<List<PlaylistDto>> GetPlaylistsAsync(Guid branchId);
    Task<PlaylistDetailDto?> GetPlaylistAsync(Guid playlistId);
    Task<PlaylistDto?> CreatePlaylistAsync(Guid branchId, CreatePlaylistRequest request);
    Task<PlaylistDto?> UpdatePlaylistAsync(Guid playlistId, UpdatePlaylistRequest request);
    Task<PlaylistDto?> SetPlaylistSpotifyBackgroundAsync(Guid playlistId, SetPlaylistSpotifyBackgroundRequest request);
    Task<bool> DeletePlaylistAsync(Guid playlistId);
    Task<PlaylistItemDto?> AddPlaylistItemAsync(Guid playlistId, AddPlaylistItemRequest request);
    Task<bool> RemovePlaylistItemAsync(Guid playlistId, Guid itemId);

    // Displays
    Task<List<DisplayDto>> GetDisplaysAsync(Guid branchId);
    Task<DisplayDetailDto?> GetDisplayAsync(Guid displayId);
    Task<DisplayDto?> CreateDisplayAsync(Guid branchId, CreateDisplayRequest request);
    Task<DisplayDto?> UpdateDisplayAsync(Guid displayId, UpdateDisplayRequest request);
    Task<bool> DeleteDisplayAsync(Guid displayId);

    // Display Zones
    Task<DisplayZoneDto?> CreateDisplayZoneAsync(Guid displayId, CreateDisplayZoneRequest request);
    Task<DisplayZoneDto?> UpdateDisplayZoneAsync(Guid displayId, Guid zoneId, UpdateDisplayZoneRequest request);
    Task<bool> DeleteDisplayZoneAsync(Guid displayId, Guid zoneId);

    // Campaigns
    Task<List<CampaignDto>> GetCampaignsAsync(Guid branchId);
    Task<CampaignDto?> CreateCampaignAsync(Guid branchId, CreateCampaignRequest request);
    Task<CampaignDto?> UpdateCampaignAsync(Guid campaignId, UpdateCampaignRequest request);
    Task<bool> DeleteCampaignAsync(Guid campaignId);
    Task<bool> RecordCampaignImpressionAsync(Guid campaignId, Guid mediaContentId);
}

public class ContentApiService : IContentApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ContentApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public ContentApiService(HttpClient httpClient, ILogger<ContentApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    #region Media

    public async Task<List<MediaContentDto>> GetMediaContentsAsync(Guid organizationId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<MediaContentDto>>($"api/v1/organizations/{organizationId}/media", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get media contents");
            return new();
        }
    }

    public async Task<MediaContentDto?> GetMediaContentAsync(Guid mediaId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<MediaContentDto>($"api/v1/media/{mediaId}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get media content {MediaId}", mediaId);
            return null;
        }
    }

    public async Task<MediaContentDto?> CreateMediaContentAsync(Guid organizationId, CreateMediaContentRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/organizations/{organizationId}/media", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<MediaContentDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create media content");
            return null;
        }
    }

    public async Task<MediaContentDto?> UploadMediaContentAsync(Guid organizationId, Stream fileStream, string fileName, string? contentType, string? name)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            using var streamContent = new StreamContent(fileStream);
            if (!string.IsNullOrEmpty(contentType))
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            content.Add(streamContent, "file", fileName);
            if (!string.IsNullOrEmpty(name))
                content.Add(new StringContent(name), "name");

            var response = await _httpClient.PostAsync($"api/v1/organizations/{organizationId}/media/upload", content);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<MediaContentDto>(_jsonOptions);

            // Surface the API's actual message (e.g. storage-quota-exceeded, with the nudge to
            // link externally instead) rather than swallowing it — callers' catch blocks show
            // ex.Message directly in a toast, so a generic "Upload failed." here would hide the
            // one piece of information (why, and what to do about it) the user actually needs.
            var errorBody = await response.Content.ReadAsStringAsync();
            var message = errorBody;
            try
            {
                var parsed = System.Text.Json.JsonDocument.Parse(errorBody);
                if (parsed.RootElement.TryGetProperty("message", out var msgProp))
                    message = msgProp.GetString() ?? errorBody;
            }
            catch (System.Text.Json.JsonException) { /* not JSON, fall back to the raw body */ }

            _logger.LogWarning("Media upload failed ({StatusCode}): {Error}", response.StatusCode, errorBody);
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload media content");
            return null;
        }
    }

    public async Task<MediaContentDto?> UpdateMediaContentAsync(Guid mediaId, UpdateMediaContentRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/media/{mediaId}", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<MediaContentDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update media content {MediaId}", mediaId);
            return null;
        }
    }

    public async Task<bool> DeleteMediaContentAsync(Guid mediaId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/media/{mediaId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete media content {MediaId}", mediaId);
            return false;
        }
    }

    #endregion

    #region Playlists

    public async Task<List<PlaylistDto>> GetPlaylistsAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<PlaylistDto>>($"api/v1/branches/{branchId}/playlists", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlists");
            return new();
        }
    }

    public async Task<PlaylistDetailDto?> GetPlaylistAsync(Guid playlistId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<PlaylistDetailDto>($"api/v1/playlists/{playlistId}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlist {PlaylistId}", playlistId);
            return null;
        }
    }

    public async Task<PlaylistDto?> CreatePlaylistAsync(Guid branchId, CreatePlaylistRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/playlists", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<PlaylistDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create playlist");
            return null;
        }
    }

    public async Task<PlaylistDto?> UpdatePlaylistAsync(Guid playlistId, UpdatePlaylistRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/playlists/{playlistId}", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<PlaylistDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update playlist {PlaylistId}", playlistId);
            return null;
        }
    }

    public async Task<PlaylistDto?> SetPlaylistSpotifyBackgroundAsync(Guid playlistId, SetPlaylistSpotifyBackgroundRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/playlists/{playlistId}/spotify-background", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<PlaylistDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set Spotify background for playlist {PlaylistId}", playlistId);
            return null;
        }
    }

    public async Task<bool> DeletePlaylistAsync(Guid playlistId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/playlists/{playlistId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete playlist {PlaylistId}", playlistId);
            return false;
        }
    }

    public async Task<PlaylistItemDto?> AddPlaylistItemAsync(Guid playlistId, AddPlaylistItemRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/playlists/{playlistId}/items", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<PlaylistItemDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add playlist item");
            return null;
        }
    }

    public async Task<bool> RemovePlaylistItemAsync(Guid playlistId, Guid itemId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/playlists/{playlistId}/items/{itemId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove playlist item");
            return false;
        }
    }

    #endregion

    #region Displays

    public async Task<List<DisplayDto>> GetDisplaysAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<DisplayDto>>($"api/v1/branches/{branchId}/displays", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get displays");
            return new();
        }
    }

    public async Task<DisplayDetailDto?> GetDisplayAsync(Guid displayId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<DisplayDetailDto>($"api/v1/displays/{displayId}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get display {DisplayId}", displayId);
            return null;
        }
    }

    public async Task<DisplayDto?> CreateDisplayAsync(Guid branchId, CreateDisplayRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/displays", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<DisplayDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create display");
            return null;
        }
    }

    public async Task<DisplayDto?> UpdateDisplayAsync(Guid displayId, UpdateDisplayRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/displays/{displayId}", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<DisplayDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update display {DisplayId}", displayId);
            return null;
        }
    }

    public async Task<bool> DeleteDisplayAsync(Guid displayId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/displays/{displayId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete display {DisplayId}", displayId);
            return false;
        }
    }

    #endregion

    #region Display Zones

    public async Task<DisplayZoneDto?> CreateDisplayZoneAsync(Guid displayId, CreateDisplayZoneRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/displays/{displayId}/zones", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<DisplayZoneDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create display zone");
            return null;
        }
    }

    public async Task<DisplayZoneDto?> UpdateDisplayZoneAsync(Guid displayId, Guid zoneId, UpdateDisplayZoneRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/displays/{displayId}/zones/{zoneId}", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<DisplayZoneDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update display zone");
            return null;
        }
    }

    public async Task<bool> DeleteDisplayZoneAsync(Guid displayId, Guid zoneId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/displays/{displayId}/zones/{zoneId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete display zone");
            return false;
        }
    }

    #endregion

    #region Campaigns

    public async Task<List<CampaignDto>> GetCampaignsAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<CampaignDto>>($"api/v1/branches/{branchId}/campaigns", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get campaigns");
            return new();
        }
    }

    public async Task<CampaignDto?> CreateCampaignAsync(Guid branchId, CreateCampaignRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/campaigns", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<CampaignDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create campaign");
            return null;
        }
    }

    public async Task<CampaignDto?> UpdateCampaignAsync(Guid campaignId, UpdateCampaignRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/campaigns/{campaignId}", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<CampaignDto>(_jsonOptions);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update campaign {CampaignId}", campaignId);
            return null;
        }
    }

    public async Task<bool> DeleteCampaignAsync(Guid campaignId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/campaigns/{campaignId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete campaign {CampaignId}", campaignId);
            return false;
        }
    }

    public async Task<bool> RecordCampaignImpressionAsync(Guid campaignId, Guid mediaContentId)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/campaigns/{campaignId}/impressions",
                new RecordCampaignImpressionRequest { MediaContentId = mediaContentId }, _jsonOptions);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record campaign impression for campaign {CampaignId}", campaignId);
            return false;
        }
    }

    #endregion
}
