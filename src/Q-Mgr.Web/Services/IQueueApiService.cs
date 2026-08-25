using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface IQueueApiService
{
    Task<QueueStatusDto?> GetQueueStatusAsync(Guid branchId);
    Task<List<TokenDto>> GetWaitingTokensAsync(Guid branchId, Guid? serviceTypeId = null);
    Task<TokenDto?> GetTokenAsync(Guid tokenId);
    Task<TokenDto?> CreateTokenAsync(Guid branchId, string serviceTypeCode, CustomerDto? customer);
    Task<TokenDto?> CallNextTokenAsync(Guid counterId);
    Task<TokenDto?> CallSpecificTokenAsync(Guid counterId, Guid tokenId);
    Task<TokenDto?> CompleteServiceAsync(Guid counterId, Guid tokenId, string? notes);
    Task<bool> MarkNoShowAsync(Guid counterId, Guid tokenId);
    Task<bool> CancelTokenAsync(Guid branchId, Guid tokenId, string? reason);
    Task<TokenDto?> TransferTokenAsync(Guid fromCounterId, Guid tokenId, Guid toCounterId, string? reason);
    Task<List<CounterDto>> GetCountersAsync(Guid branchId);
    Task<List<ServiceTypeDto>> GetServiceTypesAsync(Guid branchId);
}

public class QueueApiService : IQueueApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QueueApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public QueueApiService(HttpClient httpClient, ILogger<QueueApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public async Task<QueueStatusDto?> GetQueueStatusAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<QueueStatusDto>($"api/v1/branches/{branchId}/queue/status", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue status for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<List<TokenDto>> GetWaitingTokensAsync(Guid branchId, Guid? serviceTypeId = null)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/tokens/waiting";
            if (serviceTypeId.HasValue)
                url += $"?serviceTypeId={serviceTypeId}";

            return await _httpClient.GetFromJsonAsync<List<TokenDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get waiting tokens");
            return new();
        }
    }

    public async Task<TokenDto?> GetTokenAsync(Guid tokenId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<TokenDto>($"api/v1/tokens/{tokenId}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get token {TokenId}", tokenId);
            return null;
        }
    }

    public async Task<TokenDto?> CreateTokenAsync(Guid branchId, string serviceTypeCode, CustomerDto? customer)
    {
        try
        {
            _logger.LogInformation("Creating token for branch {BranchId}, serviceTypeCode: {ServiceTypeCode}", branchId, serviceTypeCode);

            var request = new
            {
                serviceTypeCode,
                customer,
                source = "Kiosk"
            };

            var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/tokens", request, _jsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Token creation failed with status {StatusCode}: {Error}", response.StatusCode, errorContent);
                return null;
            }

            var token = await response.Content.ReadFromJsonAsync<TokenDto>(_jsonOptions);
            _logger.LogInformation("Token created successfully: {TokenId}", token?.Id);
            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create token for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<TokenDto?> CallNextTokenAsync(Guid counterId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/counters/{counterId}/call-next", null);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TokenDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call next token for counter {CounterId}", counterId);
            return null;
        }
    }

    public async Task<TokenDto?> CallSpecificTokenAsync(Guid counterId, Guid tokenId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/counters/{counterId}/call/{tokenId}", null);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TokenDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call specific token");
            return null;
        }
    }

    public async Task<TokenDto?> CompleteServiceAsync(Guid counterId, Guid tokenId, string? notes)
    {
        try
        {
            var request = new { tokenId, notes };
            var response = await _httpClient.PostAsJsonAsync($"api/v1/counters/{counterId}/complete", request, _jsonOptions);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TokenDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete service");
            return null;
        }
    }

    public async Task<bool> MarkNoShowAsync(Guid counterId, Guid tokenId)
    {
        try
        {
            var request = new { tokenId };
            var response = await _httpClient.PostAsJsonAsync($"api/v1/counters/{counterId}/no-show", request, _jsonOptions);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark no-show");
            return false;
        }
    }

    public async Task<bool> CancelTokenAsync(Guid branchId, Guid tokenId, string? reason)
    {
        try
        {
            // BUG FIX: this previously posted to "api/v1/tokens/{tokenId}/cancel", but
            // TokensController is routed under "api/v1/branches/{branchId}/tokens" — the real
            // endpoint is "api/v1/branches/{branchId}/tokens/{tokenId}/cancel". This method was
            // never actually called from any component, so the wrong URL never surfaced as a
            // live 404 — fixed now while wiring up the first real caller (Transfer picker UI).
            var request = new { reason };
            var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/tokens/{tokenId}/cancel", request, _jsonOptions);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel token");
            return false;
        }
    }

    public async Task<TokenDto?> TransferTokenAsync(Guid fromCounterId, Guid tokenId, Guid toCounterId, string? reason)
    {
        try
        {
            var request = new { tokenId, toCounterId, reason };
            var response = await _httpClient.PostAsJsonAsync($"api/v1/counters/{fromCounterId}/transfer", request, _jsonOptions);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<TokenDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transfer token");
            return null;
        }
    }

    public async Task<List<CounterDto>> GetCountersAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<CounterDto>>($"api/v1/branches/{branchId}/counters", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get counters");
            return new();
        }
    }

    public async Task<List<ServiceTypeDto>> GetServiceTypesAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<ServiceTypeDto>>($"api/v1/branches/{branchId}/service-types", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get service types");
            return new();
        }
    }
}
