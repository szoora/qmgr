using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QMgr.Integration.Contracts;

namespace QMgr.Integration.Adapters;

/// <summary>
/// Base implementation of the Q-Mgr integration client
/// </summary>
public class QueueIntegrationClient : IQueueIntegrationClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QueueIntegrationClient> _logger;
    private readonly QueueIntegrationOptions _options;

    public QueueIntegrationClient(
        HttpClient httpClient,
        ILogger<QueueIntegrationClient> logger,
        QueueIntegrationOptions options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options;

        _httpClient.BaseAddress = new Uri(options.ApiBaseUrl);
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
        if (!string.IsNullOrEmpty(options.ApiSecret))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Secret", options.ApiSecret);
        }
    }

    public async Task<CreateTokenResult> CreateTokenAsync(CreateTokenRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var apiRequest = new
            {
                serviceTypeCode = request.ServiceTypeCode,
                customer = request.Customer,
                source = "API",
                priority = request.Priority,
                externalReference = request.ExternalReference,
                externalSystem = _options.SystemIdentifier,
                metadata = request.Metadata,
                estimatedArrival = request.EstimatedArrival
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"api/v1/branches/{_options.BranchId}/tokens",
                apiRequest,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to create token: {StatusCode} - {Error}", response.StatusCode, error);
                return new CreateTokenResult
                {
                    Success = false,
                    ErrorMessage = $"API Error: {response.StatusCode}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<TokenApiResponse>(cancellationToken: cancellationToken);

            return new CreateTokenResult
            {
                Success = true,
                TokenId = result?.Id,
                DisplayNumber = result?.DisplayNumber,
                PositionInQueue = result?.PositionInQueue,
                EstimatedWaitMinutes = result?.EstimatedWaitMinutes,
                CreatedAt = result?.CreatedAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating token");
            return new CreateTokenResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<TokenStatusResult?> GetTokenStatusAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<TokenApiResponse>(
                $"api/v1/branches/{_options.BranchId}/tokens/{tokenId}",
                cancellationToken);

            return MapToStatusResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get token status for {TokenId}", tokenId);
            return null;
        }
    }

    public async Task<TokenStatusResult?> GetTokenByExternalReferenceAsync(string externalReference, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<TokenApiResponse>(
                $"api/v1/branches/{_options.BranchId}/tokens/by-reference?externalSystem={_options.SystemIdentifier}&externalReference={externalReference}",
                cancellationToken);

            return MapToStatusResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get token by reference {Reference}", externalReference);
            return null;
        }
    }

    public async Task<List<TokenStatusResult>> GetCustomerTokensAsync(string customerId, bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<List<TokenApiResponse>>(
                $"api/v1/branches/{_options.BranchId}/tokens/by-customer/{customerId}?activeOnly={activeOnly}",
                cancellationToken);

            return response?.Select(MapToStatusResult).Where(r => r != null).Cast<TokenStatusResult>().ToList() ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tokens for customer {CustomerId}", customerId);
            return new();
        }
    }

    public async Task<bool> CancelTokenAsync(Guid tokenId, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { reason, cancelledBy = _options.SystemIdentifier };
            var response = await _httpClient.PostAsJsonAsync(
                $"api/v1/branches/{_options.BranchId}/tokens/{tokenId}/cancel",
                request,
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel token {TokenId}", tokenId);
            return false;
        }
    }

    public async Task<bool> UpdateTokenMetadataAsync(Guid tokenId, Dictionary<string, object> metadata, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { metadata };
            var response = await _httpClient.PatchAsJsonAsync(
                $"api/v1/branches/{_options.BranchId}/tokens/{tokenId}",
                request,
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update metadata for token {TokenId}", tokenId);
            return false;
        }
    }

    public async Task<QueueStatusResult?> GetQueueStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<QueueStatusApiResponse>(
                $"api/v1/branches/{_options.BranchId}/queue/status",
                cancellationToken);

            if (response == null) return null;

            return new QueueStatusResult
            {
                TotalWaiting = response.Summary?.TotalWaiting ?? 0,
                TotalServing = response.Summary?.TotalServing ?? 0,
                AverageWaitMinutes = (int)(response.Summary?.AverageWaitMinutes ?? 0),
                ServiceTypes = response.ServiceTypes?.Select(st => new ServiceTypeStatus
                {
                    Code = st.Code,
                    Name = st.Name,
                    WaitingCount = st.WaitingCount,
                    EstimatedWaitMinutes = st.EstimatedWaitMinutes,
                    ActiveCounters = st.CountersActive
                }).ToList() ?? new()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue status");
            return null;
        }
    }

    public async Task<int> GetEstimatedWaitTimeAsync(string serviceTypeCode, CancellationToken cancellationToken = default)
    {
        var status = await GetQueueStatusAsync(cancellationToken);
        var serviceType = status?.ServiceTypes.FirstOrDefault(st => st.Code == serviceTypeCode);
        return serviceType?.EstimatedWaitMinutes ?? 0;
    }

    private static TokenStatusResult? MapToStatusResult(TokenApiResponse? response)
    {
        if (response == null) return null;

        return new TokenStatusResult
        {
            Id = response.Id,
            DisplayNumber = response.DisplayNumber,
            Status = response.Status,
            PositionInQueue = response.PositionInQueue,
            EstimatedWaitMinutes = response.EstimatedWaitMinutes,
            CounterNumber = response.Counter?.CounterNumber,
            CreatedAt = response.CreatedAt,
            CalledAt = response.CalledAt,
            CompletedAt = response.ServiceCompletedAt,
            WaitTimeMinutes = response.ActualWaitMinutes,
            ServiceTimeMinutes = response.ServiceDurationMinutes,
            Metadata = response.Metadata
        };
    }

    // API Response models
    private record TokenApiResponse
    {
        public Guid Id { get; init; }
        public string DisplayNumber { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int? PositionInQueue { get; init; }
        public int? EstimatedWaitMinutes { get; init; }
        public int? ActualWaitMinutes { get; init; }
        public int? ServiceDurationMinutes { get; init; }
        public CounterInfo? Counter { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? CalledAt { get; init; }
        public DateTime? ServiceCompletedAt { get; init; }
        public Dictionary<string, object>? Metadata { get; init; }
    }

    private record CounterInfo
    {
        public string CounterNumber { get; init; } = string.Empty;
    }

    private record QueueStatusApiResponse
    {
        public SummaryInfo? Summary { get; init; }
        public List<ServiceTypeInfo>? ServiceTypes { get; init; }
    }

    private record SummaryInfo
    {
        public int TotalWaiting { get; init; }
        public int TotalServing { get; init; }
        public double AverageWaitMinutes { get; init; }
    }

    private record ServiceTypeInfo
    {
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int WaitingCount { get; init; }
        public int EstimatedWaitMinutes { get; init; }
        public int CountersActive { get; init; }
    }
}

public record QueueIntegrationOptions
{
    public string ApiBaseUrl { get; init; } = string.Empty;
    /// <summary>The API client id issued by Q-Mgr (X-API-Key).</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The client secret issued alongside it. Q-Mgr requires it on every API-key request (sent as
    /// X-API-Secret). Leave empty only if ApiKey already holds the combined "clientId.secret" form.
    /// </summary>
    public string ApiSecret { get; init; } = string.Empty;
    public Guid BranchId { get; init; }
    public string SystemIdentifier { get; init; } = string.Empty;
}
