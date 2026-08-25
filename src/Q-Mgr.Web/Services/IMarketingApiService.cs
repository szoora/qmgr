using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface IMarketingApiService
{
    Task<List<ContactDto>> GetContactsAsync(string? tag = null);
    Task<ContactDto?> CreateContactAsync(CreateContactRequest request);
    Task<bool> DeleteContactAsync(Guid contactId);

    Task<List<BroadcastDto>> GetBroadcastsAsync();
    Task<BroadcastDto?> CreateBroadcastAsync(CreateBroadcastRequest request);
    Task<BroadcastDto?> ScheduleBroadcastAsync(Guid broadcastId, DateTime? scheduledAt);
    Task<BroadcastDto?> CancelBroadcastAsync(Guid broadcastId);

    /// <summary>
    /// Adds one more attachment to the broadcast — call once per file for multiple attachments.
    /// Throws with the API's actual ProblemDetails.Title on failure (size limit, disallowed file
    /// type, too many attachments already, wrong broadcast status) — callers show it directly
    /// rather than a generic message.
    /// </summary>
    Task<BroadcastDto> UploadBroadcastAttachmentAsync(Guid broadcastId, Stream fileStream, string fileName, string contentType);
    Task<BroadcastDto?> DeleteBroadcastAttachmentAsync(Guid broadcastId, Guid attachmentId);
}

public class MarketingApiService : IMarketingApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MarketingApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public MarketingApiService(HttpClient httpClient, ILogger<MarketingApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public async Task<List<ContactDto>> GetContactsAsync(string? tag = null)
    {
        try
        {
            var url = "api/v1/marketing/contacts";
            if (!string.IsNullOrWhiteSpace(tag)) url += $"?tag={Uri.EscapeDataString(tag)}";
            return await _httpClient.GetFromJsonAsync<List<ContactDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get contacts");
            return new();
        }
    }

    public async Task<ContactDto?> CreateContactAsync(CreateContactRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/v1/marketing/contacts", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<ContactDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create contact");
            return null;
        }
    }

    public async Task<bool> DeleteContactAsync(Guid contactId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/marketing/contacts/{contactId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete contact {ContactId}", contactId);
            return false;
        }
    }

    public async Task<List<BroadcastDto>> GetBroadcastsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<BroadcastDto>>("api/v1/marketing/broadcasts", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get broadcasts");
            return new();
        }
    }

    public async Task<BroadcastDto?> CreateBroadcastAsync(CreateBroadcastRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/v1/marketing/broadcasts", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<BroadcastDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create broadcast");
            return null;
        }
    }

    public async Task<BroadcastDto?> ScheduleBroadcastAsync(Guid broadcastId, DateTime? scheduledAt)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/marketing/broadcasts/{broadcastId}/schedule", new { scheduledAt }, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<BroadcastDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule broadcast {BroadcastId}", broadcastId);
            return null;
        }
    }

    public async Task<BroadcastDto?> CancelBroadcastAsync(Guid broadcastId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/marketing/broadcasts/{broadcastId}/cancel", null);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<BroadcastDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel broadcast {BroadcastId}", broadcastId);
            return null;
        }
    }

    public async Task<BroadcastDto> UploadBroadcastAttachmentAsync(Guid broadcastId, Stream fileStream, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(fileStream);
        if (!string.IsNullOrEmpty(contentType))
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(streamContent, "file", fileName);

        var response = await _httpClient.PostAsync($"api/v1/marketing/broadcasts/{broadcastId}/attachment", content);
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<BroadcastDto>(_jsonOptions))!;

        throw new InvalidOperationException(await ReadProblemTitleAsync(response));
    }

    public async Task<BroadcastDto?> DeleteBroadcastAttachmentAsync(Guid broadcastId, Guid attachmentId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/marketing/broadcasts/{broadcastId}/attachment/{attachmentId}");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<BroadcastDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove attachment {AttachmentId} from broadcast {BroadcastId}", attachmentId, broadcastId);
            return null;
        }
    }

    private static async Task<string> ReadProblemTitleAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.TryGetProperty("title", out var titleProp))
                return titleProp.GetString() ?? body;
        }
        catch (JsonException) { /* not JSON, fall back to the raw body */ }
        return body;
    }
}
