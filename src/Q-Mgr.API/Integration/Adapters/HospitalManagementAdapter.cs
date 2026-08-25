using Microsoft.Extensions.Logging;
using QMgr.Integration.Contracts;

namespace QMgr.Integration.Adapters;

/// <summary>
/// Adapter for Hospital Management System integration
/// Handles patient check-in, appointment queue management, and department-based routing
/// </summary>
public class HospitalManagementAdapter
{
    private readonly IQueueIntegrationClient _queueClient;
    private readonly ILogger<HospitalManagementAdapter> _logger;
    private readonly HospitalAdapterOptions _options;

    public HospitalManagementAdapter(
        IQueueIntegrationClient queueClient,
        ILogger<HospitalManagementAdapter> logger,
        HospitalAdapterOptions options)
    {
        _queueClient = queueClient;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Maps hospital department to Q-Mgr service type code
    /// </summary>
    public static readonly Dictionary<string, string> DepartmentToServiceCode = new()
    {
        { "GENERAL", "GEN" },
        { "GYNECOLOGY", "GY" },
        { "PEDIATRICS", "PD" },
        { "CARDIOLOGY", "CD" },
        { "ORTHOPEDICS", "OR" },
        { "DERMATOLOGY", "DM" },
        { "OPHTHALMOLOGY", "OP" },
        { "ENT", "ENT" },
        { "LABORATORY", "LAB" },
        { "PHARMACY", "PH" },
        { "RADIOLOGY", "RAD" },
        { "EMERGENCY", "ER" }
    };

    /// <summary>
    /// Check in a patient when they arrive at the hospital
    /// </summary>
    public async Task<PatientCheckInResult> CheckInPatientAsync(
        PatientInfo patient,
        AppointmentInfo? appointment,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var serviceCode = GetServiceCode(appointment?.Department ?? "GENERAL");
            var priority = DeterminePriority(patient, appointment);

            var result = await _queueClient.CreateTokenAsync(new CreateTokenRequest
            {
                ServiceTypeCode = serviceCode,
                Customer = new CustomerInfo
                {
                    Id = patient.PatientId,
                    Name = patient.FullName,
                    Phone = patient.Phone,
                    Email = patient.Email
                },
                Priority = priority,
                ExternalReference = appointment?.AppointmentId ?? patient.PatientId,
                Metadata = new Dictionary<string, object>
                {
                    ["patient_id"] = patient.PatientId,
                    ["mrn"] = patient.MedicalRecordNumber ?? "",
                    ["appointment_id"] = appointment?.AppointmentId ?? "",
                    ["department"] = appointment?.Department ?? "GENERAL",
                    ["doctor_id"] = appointment?.DoctorId ?? "",
                    ["doctor_name"] = appointment?.DoctorName ?? "",
                    ["visit_type"] = appointment?.VisitType ?? "Walk-In",
                    ["stage"] = "check_in",
                    ["insurance_verified"] = patient.InsuranceVerified,
                    ["check_in_time"] = DateTime.UtcNow
                },
                EstimatedArrival = appointment?.ScheduledTime
            }, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Patient {PatientId} checked in successfully. Token: {Token}, Position: {Position}",
                    patient.PatientId, result.DisplayNumber, result.PositionInQueue);
            }

            return new PatientCheckInResult
            {
                Success = result.Success,
                TokenId = result.TokenId,
                TokenNumber = result.DisplayNumber,
                QueuePosition = result.PositionInQueue,
                EstimatedWaitMinutes = result.EstimatedWaitMinutes,
                ErrorMessage = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking in patient {PatientId}", patient.PatientId);
            return new PatientCheckInResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Update patient queue status (e.g., after vitals, before consultation)
    /// </summary>
    public async Task<bool> UpdatePatientStageAsync(
        Guid tokenId,
        string newStage,
        Dictionary<string, object>? additionalData = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = new Dictionary<string, object>
        {
            ["stage"] = newStage,
            [$"{newStage}_time"] = DateTime.UtcNow
        };

        if (additionalData != null)
        {
            foreach (var kvp in additionalData)
            {
                metadata[kvp.Key] = kvp.Value;
            }
        }

        return await _queueClient.UpdateTokenMetadataAsync(tokenId, metadata, cancellationToken);
    }

    /// <summary>
    /// Transfer patient to different department/service
    /// </summary>
    public async Task<PatientCheckInResult> TransferPatientAsync(
        Guid currentTokenId,
        string newDepartment,
        string reason,
        CancellationToken cancellationToken = default)
    {
        // Get current token info
        var currentToken = await _queueClient.GetTokenStatusAsync(currentTokenId, cancellationToken);
        if (currentToken == null)
        {
            return new PatientCheckInResult { Success = false, ErrorMessage = "Current token not found" };
        }

        // Cancel current token
        await _queueClient.CancelTokenAsync(currentTokenId, $"Transferred to {newDepartment}: {reason}", cancellationToken);

        // Create new token for new department
        var serviceCode = GetServiceCode(newDepartment);
        var result = await _queueClient.CreateTokenAsync(new CreateTokenRequest
        {
            ServiceTypeCode = serviceCode,
            Customer = new CustomerInfo
            {
                Id = currentToken.Metadata?["patient_id"]?.ToString(),
                Name = currentToken.Metadata?["patient_name"]?.ToString()
            },
            Priority = 1, // Priority for transfers
            ExternalReference = currentToken.Metadata?["appointment_id"]?.ToString() ?? currentTokenId.ToString(),
            Metadata = new Dictionary<string, object>
            {
                ["transferred_from"] = currentToken.Metadata?["department"] ?? "",
                ["transfer_reason"] = reason,
                ["original_token_id"] = currentTokenId,
                ["department"] = newDepartment,
                ["stage"] = "transferred"
            }
        }, cancellationToken);

        return new PatientCheckInResult
        {
            Success = result.Success,
            TokenId = result.TokenId,
            TokenNumber = result.DisplayNumber,
            QueuePosition = result.PositionInQueue,
            EstimatedWaitMinutes = result.EstimatedWaitMinutes
        };
    }

    /// <summary>
    /// Get all active tokens for a patient
    /// </summary>
    public async Task<List<PatientQueueStatus>> GetPatientQueueStatusAsync(
        string patientId,
        CancellationToken cancellationToken = default)
    {
        var tokens = await _queueClient.GetCustomerTokensAsync(patientId, true, cancellationToken);

        return tokens.Select(t => new PatientQueueStatus
        {
            TokenId = t.Id,
            TokenNumber = t.DisplayNumber,
            Department = t.Metadata?["department"]?.ToString() ?? "",
            Stage = t.Metadata?["stage"]?.ToString() ?? "",
            Status = t.Status,
            QueuePosition = t.PositionInQueue,
            EstimatedWaitMinutes = t.EstimatedWaitMinutes,
            CounterNumber = t.CounterNumber,
            DoctorName = t.Metadata?["doctor_name"]?.ToString()
        }).ToList();
    }

    /// <summary>
    /// Handle patient discharge/departure
    /// </summary>
    public async Task<bool> DischargePatientAsync(
        Guid tokenId,
        string dischargeNotes,
        CancellationToken cancellationToken = default)
    {
        return await _queueClient.UpdateTokenMetadataAsync(tokenId, new Dictionary<string, object>
        {
            ["stage"] = "discharged",
            ["discharge_time"] = DateTime.UtcNow,
            ["discharge_notes"] = dischargeNotes
        }, cancellationToken);
    }

    private string GetServiceCode(string department)
    {
        var upperDept = department.ToUpperInvariant().Replace(" ", "_");
        return DepartmentToServiceCode.TryGetValue(upperDept, out var code) ? code : "GEN";
    }

    private int DeterminePriority(PatientInfo patient, AppointmentInfo? appointment)
    {
        // Emergency department always gets highest priority
        if (appointment?.Department?.ToUpperInvariant() == "EMERGENCY")
            return 3;

        // VIP/Senior patients get priority
        if (patient.IsVip || patient.IsSeniorCitizen)
            return 2;

        // Scheduled appointments get slight priority over walk-ins
        if (appointment != null)
            return 1;

        return 0; // Walk-in, normal priority
    }
}

// Hospital-specific models

public record PatientInfo
{
    public string PatientId { get; init; } = string.Empty;
    public string? MedicalRecordNumber { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public DateTime? DateOfBirth { get; init; }
    public string? Gender { get; init; }
    public bool InsuranceVerified { get; init; }
    public bool IsVip { get; init; }
    public bool IsSeniorCitizen { get; init; }
}

public record AppointmentInfo
{
    public string AppointmentId { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string? DoctorId { get; init; }
    public string? DoctorName { get; init; }
    public DateTime ScheduledTime { get; init; }
    public string VisitType { get; init; } = "Scheduled"; // Scheduled, Follow-Up, Emergency
}

public record PatientCheckInResult
{
    public bool Success { get; init; }
    public Guid? TokenId { get; init; }
    public string? TokenNumber { get; init; }
    public int? QueuePosition { get; init; }
    public int? EstimatedWaitMinutes { get; init; }
    public string? ErrorMessage { get; init; }
}

public record PatientQueueStatus
{
    public Guid TokenId { get; init; }
    public string TokenNumber { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int? QueuePosition { get; init; }
    public int? EstimatedWaitMinutes { get; init; }
    public string? CounterNumber { get; init; }
    public string? DoctorName { get; init; }
}

public record HospitalAdapterOptions
{
    public string HospitalCode { get; init; } = string.Empty;
    public bool EnableSmsNotifications { get; init; } = true;
    public bool AutoCheckInAppointments { get; init; } = false;
}
