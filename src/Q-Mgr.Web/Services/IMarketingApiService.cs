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
}
