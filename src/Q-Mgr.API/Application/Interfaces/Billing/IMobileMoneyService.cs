using QMgr.Domain.Enums;

namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Service for Mobile Money payments via CRM gateway (MTN, Airtel, Yo)
/// </summary>
public interface IMobileMoneyService
{
    /// <summary>
    /// True when the gateway is switched on and has a base URL (Platform Settings row first, then configuration).
    /// </summary>
    Task<bool> IsConfiguredAsync();

    /// <summary>
    /// Initiate a payment collection from a customer's mobile money account
    /// </summary>
    Task<MobileMoneyPaymentResult> CollectPaymentAsync(
        Guid organizationId,
        string phoneNumber,
        decimal amount,
        string currency,
        string narrative,
        string? externalReference = null);

    /// <summary>
    /// Check the status of a mobile money transaction
    /// </summary>
    Task<MobileMoneyStatusResult> CheckPaymentStatusAsync(string transactionId);

    /// <summary>
    /// Validate a phone number and detect the mobile money channel
    /// </summary>
    Task<PhoneValidationResult> ValidatePhoneAsync(string phoneNumber);

    /// <summary>
    /// Retry a failed payment
    /// </summary>
    Task<MobileMoneyPaymentResult> RetryPaymentAsync(string originalTransactionId);

    /// <summary>
    /// Get supported mobile money channels
    /// </summary>
    IEnumerable<MobileMoneyChannel> GetSupportedChannels();
}

/// <summary>
/// Result of a mobile money payment initiation
/// </summary>
public record MobileMoneyPaymentResult(
    bool Success,
    string? TransactionId,
    string? ExternalReference,
    MobileMoneyPaymentStatus Status,
    string? Channel,
    string? ErrorCode,
    string? ErrorMessage,
    string? CustomerMessage);

/// <summary>
/// Result of checking payment status
/// </summary>
public record MobileMoneyStatusResult(
    string TransactionId,
    MobileMoneyPaymentStatus Status,
    string? Channel,
    decimal? Amount,
    string? Currency,
    DateTime? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Result of phone number validation
/// </summary>
public record PhoneValidationResult(
    bool IsValid,
    string? NormalizedPhone,
    string? Channel,
    string? Carrier,
    string? CountryCode,
    string? ErrorMessage);

/// <summary>
/// Mobile money channel information
/// </summary>
public record MobileMoneyChannel(
    string Code,
    string Name,
    string Country,
    string Currency,
    string[] PhonePrefixes,
    bool IsActive);

/// <summary>
/// Status of a mobile money payment
/// </summary>
public enum MobileMoneyPaymentStatus
{
    Pending,
    Processing,
    AwaitingConfirmation,
    Succeeded,
    Failed,
    Cancelled,
    Expired,
    Refunded
}
