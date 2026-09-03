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

    /// <summary>
    /// The kiosk / join-page service menu, served anonymously. Use this from any customer-facing
    /// screen instead of <see cref="GetServiceTypesAsync"/> — that one is [Authorize]d and 401s on
    /// an unattended terminal with no staff session.
    /// </summary>
    Task<List<PublicServiceTypeDto>> GetPublicServiceTypesAsync(Guid branchId);

    /// <summary>
    /// Issues a ticket with no session, via <c>POST api/v1/branches/{branchId}/queue/tokens</c>.
    /// Unlike most methods here the failure is NOT collapsed to null: "the queue is full" and
    /// "you have asked for too many tickets" are answers a waiting customer must be shown
    /// verbatim, not a silent nothing-happened.
    /// </summary>
    Task<PublicTicketResult> IssuePublicTokenAsync(Guid branchId, PublicJoinQueueRequest request);

    /// <summary>Anonymous per-ticket status for the customer's own phone. Null when not found.</summary>
    Task<PublicTicketStatusDto?> GetPublicTicketStatusAsync(Guid branchId, string displayNumber);
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

/// <summary>
/// Outcome of an anonymous ticket request. <see cref="Ticket"/> is non-null on success;
/// otherwise <see cref="ErrorCode"/> is the API's own machine code (<c>QUEUE_FULL</c>,
/// <c>RATE_LIMITED</c>, <c>SERVICE_TYPE_REQUIRED</c>, <c>FIELD_TOO_LONG</c>) and
/// <see cref="Message"/> is the sentence to put in front of the customer. Web-only — never
/// crosses the API boundary, so it deliberately does not live in Q-Mgr.Shared.
/// </summary>
public record PublicTicketResult(PublicTicketDto? Ticket, int StatusCode, string? ErrorCode, string? Message, int RetryAfterSeconds = 0)
{
    public bool Success => Ticket != null;
    public bool IsQueueFull => ErrorCode == "QUEUE_FULL";
    public bool IsRateLimited => StatusCode == 429;
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

    public async Task<List<PublicServiceTypeDto>> GetPublicServiceTypesAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<PublicServiceTypeDto>>(
                $"api/v1/branches/{branchId}/queue/service-types", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get public service types for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<PublicTicketResult> IssuePublicTokenAsync(Guid branchId, PublicJoinQueueRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"api/v1/branches/{branchId}/queue/tokens", request, _jsonOptions);

            if (response.IsSuccessStatusCode)
            {
                var ticket = await response.Content.ReadFromJsonAsync<PublicTicketDto>(_jsonOptions);
                return ticket != null
                    ? new PublicTicketResult(ticket, (int)response.StatusCode, null, null)
                    : new PublicTicketResult(null, (int)response.StatusCode, "EMPTY_RESPONSE", "The ticket could not be read back. Please ask a member of staff.");
            }

            var (errorCode, message) = await ReadErrorAsync(response);
            var retryAfter = (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0;

            _logger.LogWarning("Anonymous ticket request for branch {BranchId} returned {StatusCode} ({ErrorCode})",
                branchId, (int)response.StatusCode, errorCode);

            return new PublicTicketResult(null, (int)response.StatusCode, errorCode, message, retryAfter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue an anonymous ticket for branch {BranchId}", branchId);
            return new PublicTicketResult(null, 0, "NETWORK", "Unable to reach the server. Please try again.");
        }
    }

    public async Task<PublicTicketStatusDto?> GetPublicTicketStatusAsync(Guid branchId, string displayNumber)
    {
        if (string.IsNullOrWhiteSpace(displayNumber)) return null;

        try
        {
            var response = await _httpClient.GetAsync(
                $"api/v1/branches/{branchId}/queue/tokens/{Uri.EscapeDataString(displayNumber.Trim())}");

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<PublicTicketStatusDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read public ticket status for branch {BranchId}", branchId);
            return null;
        }
    }

    /// <summary>
    /// Pulls (errorCode, humanMessage) out of whichever error shape came back — this controller's
    /// own <c>{ error, message }</c>, or a ProblemDetails <c>{ title, detail }</c>.
    /// </summary>
    private static async Task<(string? ErrorCode, string? Message)> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            string? code = doc.RootElement.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : null;

            string? message = null;
            foreach (var key in new[] { "message", "detail", "title" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    message = el.GetString();
                    break;
                }
            }

            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
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
