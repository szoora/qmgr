namespace QMgr.Integration.Contracts;

/// <summary>
/// Interface for external systems to integrate with Q-Mgr
/// </summary>
public interface IQueueIntegrationClient
{
    /// <summary>
    /// Creates a new queue token for a customer
    /// </summary>
    Task<CreateTokenResult> CreateTokenAsync(CreateTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of a specific token
    /// </summary>
    Task<TokenStatusResult?> GetTokenStatusAsync(Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets token by external reference
    /// </summary>
    Task<TokenStatusResult?> GetTokenByExternalReferenceAsync(string externalReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets tokens for a specific customer
    /// </summary>
    Task<List<TokenStatusResult>> GetCustomerTokensAsync(string customerId, bool activeOnly = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a token
    /// </summary>
    Task<bool> CancelTokenAsync(Guid tokenId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates token metadata
    /// </summary>
    Task<bool> UpdateTokenMetadataAsync(Guid tokenId, Dictionary<string, object> metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current queue status for a branch
    /// </summary>
    Task<QueueStatusResult?> GetQueueStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets estimated wait time for a service type
    /// </summary>
    Task<int> GetEstimatedWaitTimeAsync(string serviceTypeCode, CancellationToken cancellationToken = default);
}

// Request/Response contracts

public record CreateTokenRequest
{
    public string ServiceTypeCode { get; init; } = string.Empty;
    public CustomerInfo? Customer { get; init; }
    public int Priority { get; init; } = 0; // 0=Normal, 1=Priority, 2=VIP, 3=Emergency
    public string? ExternalReference { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public DateTime? EstimatedArrival { get; init; }
}

public record CustomerInfo
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
}

public record CreateTokenResult
{
    public bool Success { get; init; }
    public Guid? TokenId { get; init; }
    public string? DisplayNumber { get; init; }
    public int? PositionInQueue { get; init; }
    public int? EstimatedWaitMinutes { get; init; }
    public DateTime? CreatedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

public record TokenStatusResult
{
    public Guid Id { get; init; }
    public string DisplayNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty; // waiting, called, serving, completed, cancelled, no_show
    public int? PositionInQueue { get; init; }
    public int? EstimatedWaitMinutes { get; init; }
    public string? CounterNumber { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CalledAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int? WaitTimeMinutes { get; init; }
    public int? ServiceTimeMinutes { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

public record QueueStatusResult
{
    public int TotalWaiting { get; init; }
    public int TotalServing { get; init; }
    public int AverageWaitMinutes { get; init; }
    public List<ServiceTypeStatus> ServiceTypes { get; init; } = new();
}

public record ServiceTypeStatus
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int WaitingCount { get; init; }
    public int EstimatedWaitMinutes { get; init; }
    public int ActiveCounters { get; init; }
}
