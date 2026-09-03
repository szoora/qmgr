using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface IQueueApiService
{
    Task<QueueStatusDto?> GetQueueStatusAsync(Guid branchId);
    Task<List<TokenDto>> GetWaitingTokensAsync(Guid branchId, Guid? serviceTypeId = null);
    /// <summary>Anonymous, PII-free waiting list for public display screens.</summary>
    Task<List<TokenDto>> GetPublicWaitingTokensAsync(Guid branchId, int limit = 50);
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

    /// <summary>
    /// CSV exports from ReportsController. <paramref name="report"/> is one of the
    /// <see cref="ReportExportKind"/> constants. Unlike most methods on this service, a failure
    /// is NOT collapsed to null: a 403 here is a real, user-actionable answer (missing
    /// reports.export permission, or the module/feature isn't purchased) that the page must show
    /// as a toast, not a silent nothing-happened.
    /// </summary>
    Task<ReportExportResult> ExportReportCsvAsync(string report, Guid branchId, DateOnly from, DateOnly to);
}

/// <summary>URL segments for <see cref="IQueueApiService.ExportReportCsvAsync"/>.</summary>
public static class ReportExportKind
{
    public const string Overview = "overview";
    public const string Counters = "counters";
    public const string Services = "services";
    public const string Feedback = "feedback";
}

/// <summary>
/// Outcome of a CSV export call. <see cref="Csv"/> is non-null on success; otherwise
/// <see cref="StatusCode"/> and <see cref="ErrorMessage"/> (the API's own <c>message</c> /
/// <c>detail</c> when it sent one) describe why. Web-only — never crosses the API boundary, so it
/// deliberately does not live in Q-Mgr.Shared.
/// </summary>
public record ReportExportResult(string? Csv, int StatusCode, string? ErrorMessage)
{
    public bool Success => Csv != null;
    public bool IsForbidden => StatusCode == 403;
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

    public async Task<List<TokenDto>> GetPublicWaitingTokensAsync(Guid branchId, int limit = 50)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<TokenDto>>($"api/v1/branches/{branchId}/queue/waiting?limit={limit}", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get public waiting tokens");
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

    /// <summary>
    /// Null legitimately means "no one is waiting" (204 No Content) — the caller shows an
    /// informational message for that. A real failure (network error, 500, etc.) is deliberately
    /// NOT swallowed to null here, unlike most other methods on this service: doing so previously
    /// made a genuine backend error on this specific action ("relation \"tokens\" does not exist",
    /// found live this session) present to front-desk staff as the identical, misleading "There
    /// are no customers waiting in the queue" message as a real empty queue — the one action in
    /// this whole app where that distinction is load-bearing, since staff would otherwise have no
    /// way to tell "queue's actually empty" from "the call failed, try again."
    /// </summary>
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
            throw;
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

    public async Task<ReportExportResult> ExportReportCsvAsync(string report, Guid branchId, DateOnly from, DateOnly to)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/reports/{report}/export?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
                return new ReportExportResult(await response.Content.ReadAsStringAsync(), (int)response.StatusCode, null);

            // Both RequireFeature/RequireModule ({ error, message, ... }) and ProblemDetails
            // ({ title, detail, ... }) bodies are possible — pull whichever human-readable
            // field is present rather than assuming one shape.
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "message", "detail", "title" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                        {
                            message = el.GetString();
                            break;
                        }
                    }
                }
            }
            catch (JsonException) { /* empty or non-JSON error body — fall back to a generic message */ }

            _logger.LogWarning("Report export '{Report}' for branch {BranchId} returned {StatusCode}: {Message}", report, branchId, (int)response.StatusCode, message);
            return new ReportExportResult(null, (int)response.StatusCode, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export report '{Report}' for branch {BranchId}", report, branchId);
            return new ReportExportResult(null, 0, "Unable to reach the server.");
        }
    }
}
