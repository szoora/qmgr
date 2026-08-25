using Microsoft.Extensions.Logging;
using QMgr.Integration.Contracts;

namespace QMgr.Integration.Adapters;

/// <summary>
/// Adapter for Pharmacy Management System integration
/// Handles prescription queue management and customer notifications
/// </summary>
public class PharmacySystemAdapter
{
    private readonly IQueueIntegrationClient _queueClient;
    private readonly ILogger<PharmacySystemAdapter> _logger;
    private readonly PharmacyAdapterOptions _options;

    public PharmacySystemAdapter(
        IQueueIntegrationClient queueClient,
        ILogger<PharmacySystemAdapter> logger,
        PharmacyAdapterOptions options)
    {
        _queueClient = queueClient;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Pharmacy service codes
    /// </summary>
    public static readonly Dictionary<string, string> ServiceToCode = new()
    {
        { "PRESCRIPTION_PICKUP", "RXP" },
        { "PRESCRIPTION_DROP", "RXD" },
        { "CONSULTATION", "CNS" },
        { "VACCINATION", "VAC" },
        { "OTC_PURCHASE", "OTC" },
        { "REFILL", "REF" },
        { "INSURANCE_QUERY", "INS" }
    };

    /// <summary>
    /// Customer drops off prescription
    /// </summary>
    public async Task<PharmacyQueueResult> DropOffPrescriptionAsync(
        PrescriptionInfo prescription,
        CustomerContact customer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _queueClient.CreateTokenAsync(new CreateTokenRequest
            {
                ServiceTypeCode = "RXD",
                Customer = new CustomerInfo
                {
                    Id = customer.CustomerId,
                    Name = customer.Name,
                    Phone = customer.Phone,
                    Email = customer.Email
                },
                Priority = prescription.IsUrgent ? 2 : 0,
                ExternalReference = prescription.PrescriptionId,
                Metadata = new Dictionary<string, object>
                {
                    ["prescription_id"] = prescription.PrescriptionId,
                    ["doctor_name"] = prescription.DoctorName ?? "",
                    ["items_count"] = prescription.ItemsCount,
                    ["is_urgent"] = prescription.IsUrgent,
                    ["is_controlled"] = prescription.IsControlledSubstance,
                    ["insurance_required"] = prescription.InsuranceRequired,
                    ["stage"] = "dropped_off",
                    ["estimated_ready_time"] = DateTime.UtcNow.AddMinutes(_options.DefaultPrepTimeMinutes)
                }
            }, cancellationToken);

            _logger.LogInformation("Prescription {PrescriptionId} dropped off. Token: {Token}",
                prescription.PrescriptionId, result.DisplayNumber);

            return new PharmacyQueueResult
            {
                Success = result.Success,
                TokenId = result.TokenId,
                TokenNumber = result.DisplayNumber,
                EstimatedReadyTime = DateTime.UtcNow.AddMinutes(_options.DefaultPrepTimeMinutes),
                ErrorMessage = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dropping off prescription {PrescriptionId}", prescription.PrescriptionId);
            return new PharmacyQueueResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Mark prescription as ready and notify customer
    /// </summary>
    public async Task<bool> MarkPrescriptionReadyAsync(
        string prescriptionId,
        decimal totalAmount,
        CancellationToken cancellationToken = default)
    {
        var token = await _queueClient.GetTokenByExternalReferenceAsync(prescriptionId, cancellationToken);
        if (token == null)
        {
            _logger.LogWarning("Token not found for prescription {PrescriptionId}", prescriptionId);
            return false;
        }

        var success = await _queueClient.UpdateTokenMetadataAsync(token.Id, new Dictionary<string, object>
        {
            ["stage"] = "ready",
            ["ready_time"] = DateTime.UtcNow,
            ["total_amount"] = totalAmount,
            ["payment_pending"] = true
        }, cancellationToken);

        if (success)
        {
            _logger.LogInformation("Prescription {PrescriptionId} marked as ready", prescriptionId);
            // Trigger SMS/notification here if enabled
        }

        return success;
    }

    /// <summary>
    /// Customer arrives to pick up prescription
    /// </summary>
    public async Task<PharmacyQueueResult> CustomerArrivalForPickupAsync(
        string prescriptionId,
        CancellationToken cancellationToken = default)
    {
        // Check if prescription token exists
        var existingToken = await _queueClient.GetTokenByExternalReferenceAsync(prescriptionId, cancellationToken);

        if (existingToken != null)
        {
            // Update stage to indicate customer has arrived
            await _queueClient.UpdateTokenMetadataAsync(existingToken.Id, new Dictionary<string, object>
            {
                ["stage"] = "customer_waiting",
                ["arrival_time"] = DateTime.UtcNow
            }, cancellationToken);

            return new PharmacyQueueResult
            {
                Success = true,
                TokenId = existingToken.Id,
                TokenNumber = existingToken.DisplayNumber,
                QueuePosition = existingToken.PositionInQueue,
                Status = existingToken.Metadata?["stage"]?.ToString() ?? "waiting"
            };
        }

        // No existing token - create pickup token
        var result = await _queueClient.CreateTokenAsync(new CreateTokenRequest
        {
            ServiceTypeCode = "RXP",
            ExternalReference = prescriptionId,
            Metadata = new Dictionary<string, object>
            {
                ["prescription_id"] = prescriptionId,
                ["stage"] = "pickup_waiting"
            }
        }, cancellationToken);

        return new PharmacyQueueResult
        {
            Success = result.Success,
            TokenId = result.TokenId,
            TokenNumber = result.DisplayNumber,
            QueuePosition = result.PositionInQueue,
            EstimatedWaitMinutes = result.EstimatedWaitMinutes,
            Status = "pickup_waiting"
        };
    }

    /// <summary>
    /// Request pharmacist consultation
    /// </summary>
    public async Task<PharmacyQueueResult> RequestConsultationAsync(
        CustomerContact customer,
        string consultationType,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _queueClient.CreateTokenAsync(new CreateTokenRequest
            {
                ServiceTypeCode = "CNS",
                Customer = new CustomerInfo
                {
                    Id = customer.CustomerId,
                    Name = customer.Name,
                    Phone = customer.Phone
                },
                Metadata = new Dictionary<string, object>
                {
                    ["consultation_type"] = consultationType,
                    ["notes"] = notes ?? "",
                    ["stage"] = "waiting_consultation"
                }
            }, cancellationToken);

            return new PharmacyQueueResult
            {
                Success = result.Success,
                TokenId = result.TokenId,
                TokenNumber = result.DisplayNumber,
                QueuePosition = result.PositionInQueue,
                EstimatedWaitMinutes = result.EstimatedWaitMinutes
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting consultation");
            return new PharmacyQueueResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Request vaccination appointment
    /// </summary>
    public async Task<PharmacyQueueResult> ScheduleVaccinationAsync(
        CustomerContact customer,
        VaccinationRequest vaccination,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _queueClient.CreateTokenAsync(new CreateTokenRequest
            {
                ServiceTypeCode = "VAC",
                Customer = new CustomerInfo
                {
                    Id = customer.CustomerId,
                    Name = customer.Name,
                    Phone = customer.Phone,
                    Email = customer.Email
                },
                ExternalReference = vaccination.AppointmentId,
                EstimatedArrival = vaccination.ScheduledTime,
                Metadata = new Dictionary<string, object>
                {
                    ["vaccine_type"] = vaccination.VaccineType,
                    ["dose_number"] = vaccination.DoseNumber,
                    ["scheduled_time"] = vaccination.ScheduledTime,
                    ["allergies"] = vaccination.KnownAllergies ?? "",
                    ["stage"] = "scheduled"
                }
            }, cancellationToken);

            return new PharmacyQueueResult
            {
                Success = result.Success,
                TokenId = result.TokenId,
                TokenNumber = result.DisplayNumber,
                EstimatedWaitMinutes = result.EstimatedWaitMinutes
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scheduling vaccination");
            return new PharmacyQueueResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Complete prescription pickup
    /// </summary>
    public async Task<bool> CompletePrescriptionPickupAsync(
        Guid tokenId,
        decimal amountPaid,
        string paymentMethod,
        CancellationToken cancellationToken = default)
    {
        return await _queueClient.UpdateTokenMetadataAsync(tokenId, new Dictionary<string, object>
        {
            ["stage"] = "completed",
            ["pickup_time"] = DateTime.UtcNow,
            ["amount_paid"] = amountPaid,
            ["payment_method"] = paymentMethod,
            ["payment_pending"] = false
        }, cancellationToken);
    }

    /// <summary>
    /// Get prescription status
    /// </summary>
    public async Task<PrescriptionStatus?> GetPrescriptionStatusAsync(
        string prescriptionId,
        CancellationToken cancellationToken = default)
    {
        var token = await _queueClient.GetTokenByExternalReferenceAsync(prescriptionId, cancellationToken);
        if (token == null) return null;

        return new PrescriptionStatus
        {
            PrescriptionId = prescriptionId,
            TokenNumber = token.DisplayNumber,
            Stage = token.Metadata?["stage"]?.ToString() ?? "unknown",
            QueuePosition = token.PositionInQueue,
            EstimatedReadyTime = token.Metadata?.ContainsKey("estimated_ready_time") == true
                ? DateTime.Parse(token.Metadata["estimated_ready_time"].ToString()!)
                : null,
            TotalAmount = token.Metadata?.ContainsKey("total_amount") == true
                ? decimal.Parse(token.Metadata["total_amount"].ToString()!)
                : null,
            IsReady = token.Metadata?["stage"]?.ToString() == "ready"
        };
    }
}

// Pharmacy-specific models

public record PrescriptionInfo
{
    public string PrescriptionId { get; init; } = string.Empty;
    public string? DoctorName { get; init; }
    public int ItemsCount { get; init; }
    public bool IsUrgent { get; init; }
    public bool IsControlledSubstance { get; init; }
    public bool InsuranceRequired { get; init; }
}

public record CustomerContact
{
    public string? CustomerId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
}

public record VaccinationRequest
{
    public string AppointmentId { get; init; } = string.Empty;
    public string VaccineType { get; init; } = string.Empty;
    public int DoseNumber { get; init; } = 1;
    public DateTime ScheduledTime { get; init; }
    public string? KnownAllergies { get; init; }
}

public record PharmacyQueueResult
{
    public bool Success { get; init; }
    public Guid? TokenId { get; init; }
    public string? TokenNumber { get; init; }
    public int? QueuePosition { get; init; }
    public int? EstimatedWaitMinutes { get; init; }
    public DateTime? EstimatedReadyTime { get; init; }
    public string? Status { get; init; }
    public string? ErrorMessage { get; init; }
}

public record PrescriptionStatus
{
    public string PrescriptionId { get; init; } = string.Empty;
    public string TokenNumber { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public int? QueuePosition { get; init; }
    public DateTime? EstimatedReadyTime { get; init; }
    public decimal? TotalAmount { get; init; }
    public bool IsReady { get; init; }
}

public record PharmacyAdapterOptions
{
    public int DefaultPrepTimeMinutes { get; init; } = 15;
    public bool EnableSmsNotifications { get; init; } = true;
    public bool RequireInsuranceVerification { get; init; } = true;
}
