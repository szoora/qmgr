using Microsoft.Extensions.Logging;
using QMgr.Integration.Contracts;

namespace QMgr.Integration.Adapters;

/// <summary>
/// Adapter for Banking/Financial System integration
/// Handles customer queue management in banking halls
/// </summary>
public class BankingSystemAdapter
{
    private readonly IQueueIntegrationClient _queueClient;
    private readonly ILogger<BankingSystemAdapter> _logger;
    private readonly BankingAdapterOptions _options;

    public BankingSystemAdapter(
        IQueueIntegrationClient queueClient,
        ILogger<BankingSystemAdapter> logger,
        BankingAdapterOptions options)
    {
        _queueClient = queueClient;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Maps banking service types to Q-Mgr service codes
    /// </summary>
    public static readonly Dictionary<string, string> ServiceToCode = new()
    {
        { "TELLER", "TEL" },
        { "CASH_DEPOSIT", "TEL" },
        { "CASH_WITHDRAWAL", "TEL" },
        { "ACCOUNT_OPENING", "ACC" },
        { "ACCOUNT_SERVICES", "ACC" },
        { "LOAN_APPLICATION", "LON" },
        { "LOAN_INQUIRY", "LON" },
        { "MORTGAGE", "MTG" },
        { "FOREX", "FRX" },
        { "CUSTOMER_SERVICE", "CSR" },
        { "PREMIUM_BANKING", "VIP" },
        { "BUSINESS_BANKING", "BIZ" },
        { "CARDS", "CRD" }
    };

    /// <summary>
    /// Customer enters banking hall and selects service
    /// </summary>
    public async Task<BankingQueueResult> EnqueueCustomerAsync(
        BankCustomer customer,
        string serviceType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var serviceCode = GetServiceCode(serviceType);
            var priority = DeterminePriority(customer);

            var result = await _queueClient.CreateTokenAsync(new CreateTokenRequest
            {
                ServiceTypeCode = serviceCode,
                Customer = new CustomerInfo
                {
                    Id = customer.CustomerId ?? customer.AccountNumber,
                    Name = customer.FullName,
                    Phone = customer.Phone,
                    Email = customer.Email
                },
                Priority = priority,
                ExternalReference = customer.TransactionReference,
                Metadata = new Dictionary<string, object>
                {
                    ["customer_id"] = customer.CustomerId ?? "",
                    ["account_number"] = customer.AccountNumber ?? "",
                    ["account_type"] = customer.AccountType ?? "",
                    ["customer_segment"] = customer.Segment ?? "Retail",
                    ["service_requested"] = serviceType,
                    ["is_premium"] = customer.IsPremium,
                    ["relationship_manager"] = customer.RelationshipManagerId ?? "",
                    ["check_in_time"] = DateTime.UtcNow
                }
            }, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Customer {CustomerId} enqueued for {Service}. Token: {Token}",
                    customer.CustomerId, serviceType, result.DisplayNumber);
            }

            return new BankingQueueResult
            {
                Success = result.Success,
                TokenId = result.TokenId,
                TokenNumber = result.DisplayNumber,
                QueuePosition = result.PositionInQueue,
                EstimatedWaitMinutes = result.EstimatedWaitMinutes,
                ServiceType = serviceType,
                ErrorMessage = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueuing customer {CustomerId}", customer.CustomerId);
            return new BankingQueueResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Handle customer arriving from online appointment booking
    /// </summary>
    public async Task<BankingQueueResult> CheckInAppointmentAsync(
        string appointmentId,
        BankCustomer customer,
        CancellationToken cancellationToken = default)
    {
        // Check if appointment is already in queue
        var existing = await _queueClient.GetTokenByExternalReferenceAsync(appointmentId, cancellationToken);
        if (existing != null)
        {
            return new BankingQueueResult
            {
                Success = true,
                TokenId = existing.Id,
                TokenNumber = existing.DisplayNumber,
                QueuePosition = existing.PositionInQueue,
                EstimatedWaitMinutes = existing.EstimatedWaitMinutes,
                AlreadyExists = true
            };
        }

        // Create new token for appointment
        return await EnqueueCustomerAsync(customer with { TransactionReference = appointmentId },
            customer.RequestedService ?? "CUSTOMER_SERVICE", cancellationToken);
    }

    /// <summary>
    /// Get queue status for all banking services
    /// </summary>
    public async Task<BankingQueueOverview> GetQueueOverviewAsync(CancellationToken cancellationToken = default)
    {
        var status = await _queueClient.GetQueueStatusAsync(cancellationToken);
        if (status == null)
        {
            return new BankingQueueOverview();
        }

        return new BankingQueueOverview
        {
            TotalWaiting = status.TotalWaiting,
            TotalBeingServed = status.TotalServing,
            AverageWaitTime = status.AverageWaitMinutes,
            ServiceQueues = status.ServiceTypes.Select(st => new ServiceQueueInfo
            {
                ServiceCode = st.Code,
                ServiceName = st.Name,
                WaitingCount = st.WaitingCount,
                EstimatedWaitMinutes = st.EstimatedWaitMinutes,
                ActiveCounters = st.ActiveCounters
            }).ToList()
        };
    }

    /// <summary>
    /// Customer decides to leave the queue
    /// </summary>
    public async Task<bool> CustomerLeftQueueAsync(
        Guid tokenId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return await _queueClient.CancelTokenAsync(tokenId, reason, cancellationToken);
    }

    /// <summary>
    /// Update customer's service request (e.g., add more services while waiting)
    /// </summary>
    public async Task<bool> UpdateServiceRequestAsync(
        Guid tokenId,
        List<string> additionalServices,
        CancellationToken cancellationToken = default)
    {
        return await _queueClient.UpdateTokenMetadataAsync(tokenId, new Dictionary<string, object>
        {
            ["additional_services"] = string.Join(",", additionalServices),
            ["updated_at"] = DateTime.UtcNow
        }, cancellationToken);
    }

    /// <summary>
    /// Transfer customer to specialized service counter
    /// </summary>
    public async Task<BankingQueueResult> TransferToServiceAsync(
        Guid currentTokenId,
        string newService,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var currentToken = await _queueClient.GetTokenStatusAsync(currentTokenId, cancellationToken);
        if (currentToken == null)
        {
            return new BankingQueueResult { Success = false, ErrorMessage = "Token not found" };
        }

        // Cancel current and create new
        await _queueClient.CancelTokenAsync(currentTokenId, $"Transferred to {newService}", cancellationToken);

        var serviceCode = GetServiceCode(newService);
        var result = await _queueClient.CreateTokenAsync(new CreateTokenRequest
        {
            ServiceTypeCode = serviceCode,
            Customer = new CustomerInfo
            {
                Id = currentToken.Metadata?["customer_id"]?.ToString(),
                Name = currentToken.Metadata?["customer_name"]?.ToString()
            },
            Priority = 1, // Priority for transfers
            Metadata = new Dictionary<string, object>
            {
                ["transferred_from_token"] = currentTokenId,
                ["transfer_reason"] = reason,
                ["original_service"] = currentToken.Metadata?["service_requested"] ?? "",
                ["service_requested"] = newService
            }
        }, cancellationToken);

        return new BankingQueueResult
        {
            Success = result.Success,
            TokenId = result.TokenId,
            TokenNumber = result.DisplayNumber,
            QueuePosition = result.PositionInQueue,
            EstimatedWaitMinutes = result.EstimatedWaitMinutes,
            ServiceType = newService
        };
    }

    private string GetServiceCode(string serviceType)
    {
        var upperService = serviceType.ToUpperInvariant().Replace(" ", "_");
        return ServiceToCode.TryGetValue(upperService, out var code) ? code : "CSR";
    }

    private int DeterminePriority(BankCustomer customer)
    {
        // Premium/Priority banking customers
        if (customer.IsPremium || customer.Segment?.ToUpperInvariant() == "PREMIUM")
            return 2;

        // Business banking customers
        if (customer.Segment?.ToUpperInvariant() == "BUSINESS" || customer.Segment?.ToUpperInvariant() == "CORPORATE")
            return 1;

        // Elderly customers (optional, based on bank policy)
        if (customer.IsElderly)
            return 1;

        return 0;
    }
}

// Banking-specific models

public record BankCustomer
{
    public string? CustomerId { get; init; }
    public string? AccountNumber { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? AccountType { get; init; } // Savings, Checking, Business
    public string? Segment { get; init; } // Retail, Premium, Business, Corporate
    public bool IsPremium { get; init; }
    public bool IsElderly { get; init; }
    public string? RelationshipManagerId { get; init; }
    public string? RequestedService { get; init; }
    public string? TransactionReference { get; init; }
}

public record BankingQueueResult
{
    public bool Success { get; init; }
    public Guid? TokenId { get; init; }
    public string? TokenNumber { get; init; }
    public int? QueuePosition { get; init; }
    public int? EstimatedWaitMinutes { get; init; }
    public string? ServiceType { get; init; }
    public bool AlreadyExists { get; init; }
    public string? ErrorMessage { get; init; }
}

public record BankingQueueOverview
{
    public int TotalWaiting { get; init; }
    public int TotalBeingServed { get; init; }
    public int AverageWaitTime { get; init; }
    public List<ServiceQueueInfo> ServiceQueues { get; init; } = new();
}

public record ServiceQueueInfo
{
    public string ServiceCode { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public int WaitingCount { get; init; }
    public int EstimatedWaitMinutes { get; init; }
    public int ActiveCounters { get; init; }
}

public record BankingAdapterOptions
{
    public string BranchCode { get; init; } = string.Empty;
    public bool EnablePriorityBanking { get; init; } = true;
    public int ElderlyAgeThreshold { get; init; } = 65;
}
