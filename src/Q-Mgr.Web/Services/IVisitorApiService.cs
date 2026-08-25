using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Web.Services;

public interface IVisitorApiService
{
    Task<List<VisitorDto>> GetVisitorsAsync(Guid branchId, VisitorStatus? status = null, bool watchlistOnly = false);
    Task<VisitorSummaryDto?> GetSummaryAsync(Guid branchId);
    Task<VisitorDto?> PreRegisterAsync(Guid branchId, PreRegisterVisitorRequest request);
    Task<VisitorDto?> CheckInAsync(Guid branchId, CheckInVisitorRequest request);
    Task<VisitorDto?> CheckInExistingAsync(Guid branchId, Guid visitorId, CheckInVisitorRequest? request = null);
    Task<VisitorDto?> CheckOutAsync(Guid branchId, Guid visitorId);
    Task<VisitorDto?> UpdateVisitorAsync(Guid branchId, Guid visitorId, UpdateVisitorRequest request);
    Task<VisitorDto?> SetWatchlistAsync(Guid branchId, Guid visitorId, bool isWatchlisted, string? reason);
    Task<bool> DeleteVisitorAsync(Guid branchId, Guid visitorId);
}

public class VisitorApiService : IVisitorApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VisitorApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public VisitorApiService(HttpClient httpClient, ILogger<VisitorApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public async Task<List<VisitorDto>> GetVisitorsAsync(Guid branchId, VisitorStatus? status = null, bool watchlistOnly = false)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/visitors?watchlistOnly={watchlistOnly}";
            if (status.HasValue) url += $"&status={status}";
            return await _httpClient.GetFromJsonAsync<List<VisitorDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitors for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<VisitorSummaryDto?> GetSummaryAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<VisitorSummaryDto>($"api/v1/branches/{branchId}/visitors/summary", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitor summary for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<VisitorDto?> PreRegisterAsync(Guid branchId, PreRegisterVisitorRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitors/pre-register", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pre-register visitor");
            return null;
        }
    }

    public async Task<VisitorDto?> CheckInAsync(Guid branchId, CheckInVisitorRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitors/checkin", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check in visitor");
            return null;
        }
    }

    public async Task<VisitorDto?> CheckInExistingAsync(Guid branchId, Guid visitorId, CheckInVisitorRequest? request = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitors/{visitorId}/checkin", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check in pre-registered visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task<VisitorDto?> CheckOutAsync(Guid branchId, Guid visitorId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/branches/{branchId}/visitors/{visitorId}/checkout", null);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check out visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task<VisitorDto?> UpdateVisitorAsync(Guid branchId, Guid visitorId, UpdateVisitorRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/visitors/{visitorId}", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task<VisitorDto?> SetWatchlistAsync(Guid branchId, Guid visitorId, bool isWatchlisted, string? reason)
    {
        try
        {
            var request = new SetWatchlistRequest { IsWatchlisted = isWatchlisted, Reason = reason };
            var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/visitors/{visitorId}/watchlist", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set watchlist for visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task<bool> DeleteVisitorAsync(Guid branchId, Guid visitorId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/v1/branches/{branchId}/visitors/{visitorId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete visitor {VisitorId}", visitorId);
            return false;
        }
    }
}
