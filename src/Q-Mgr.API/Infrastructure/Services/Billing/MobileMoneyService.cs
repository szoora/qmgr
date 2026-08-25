using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces.Billing;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// Mobile Money payment service adapter for CRM Epay gateway
/// Supports MTN Uganda, Airtel Uganda, and Yo Payments
/// </summary>
public class MobileMoneyService : IMobileMoneyService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MobileMoneyService> _logger;
    private readonly string _apiKey;
    private readonly bool _isEnabled;

    private static readonly MobileMoneyChannel[] SupportedChannels =
    {
        new("MTN_UG", "MTN Mobile Money", "UG", "UGX", new[] { "077", "078", "076", "039" }, true),
        new("AIRTEL_UG", "Airtel Money", "UG", "UGX", new[] { "070", "075" }, true),
        new("YO_UG", "Yo Payments", "UG", "UGX", Array.Empty<string>(), true)
    };

    public MobileMoneyService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<MobileMoneyService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;

        var baseUrl = _configuration["MobileMoney:CrmApiUrl"];
        if (!string.IsNullOrEmpty(baseUrl))
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }

        _apiKey = _configuration["MobileMoney:ApiKey"] ?? string.Empty;
        _isEnabled = _configuration.GetValue<bool>("MobileMoney:Enabled", false);

        // Set default headers
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
    }

    public async Task<MobileMoneyPaymentResult> CollectPaymentAsync(
        Guid organizationId,
        string phoneNumber,
        decimal amount,
        string currency,
        string narrative,
        string? externalReference = null)
    {
        if (!_isEnabled)
        {
            return new MobileMoneyPaymentResult(
                false, null, null,
                MobileMoneyPaymentStatus.Failed,
                null, "DISABLED", "Mobile money payments are not enabled", null);
        }

        try
        {
            // Validate and detect channel
            var validation = await ValidatePhoneAsync(phoneNumber);
            if (!validation.IsValid)
            {
                return new MobileMoneyPaymentResult(
                    false, null, null,
                    MobileMoneyPaymentStatus.Failed,
                    null, "INVALID_PHONE", validation.ErrorMessage, null);
            }

            var request = new CrmCollectRequest
            {
                PhoneNumber = validation.NormalizedPhone!,
                Amount = amount,
                Currency = currency,
                Narrative = narrative,
                ExternalReference = externalReference ?? Guid.NewGuid().ToString("N"),
                Channel = validation.Channel,
                Metadata = new Dictionary<string, string>
                {
                    { "organization_id", organizationId.ToString() },
                    { "source", "qmgr_billing" }
                }
            };

            var response = await _httpClient.PostAsJsonAsync("/api/v2/payments/collect", request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Mobile money collection failed: {StatusCode} - {Error}",
                    response.StatusCode, errorContent);

                return new MobileMoneyPaymentResult(
                    false, null, request.ExternalReference,
                    MobileMoneyPaymentStatus.Failed,
                    validation.Channel, "API_ERROR", $"Payment gateway error: {response.StatusCode}", null);
            }

            var result = await response.Content.ReadFromJsonAsync<CrmCollectResponse>();

            if (result == null)
            {
                return new MobileMoneyPaymentResult(
                    false, null, request.ExternalReference,
                    MobileMoneyPaymentStatus.Failed,
                    validation.Channel, "PARSE_ERROR", "Failed to parse gateway response", null);
            }

            _logger.LogInformation(
                "Mobile money collection initiated: TransactionId={TransactionId}, Status={Status}",
                result.TransactionId, result.Status);

            return new MobileMoneyPaymentResult(
                result.Success,
                result.TransactionId,
                request.ExternalReference,
                MapStatus(result.Status),
                validation.Channel,
                result.ErrorCode,
                result.ErrorMessage,
                result.CustomerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating mobile money collection for organization {OrganizationId}", organizationId);

            return new MobileMoneyPaymentResult(
                false, null, externalReference,
                MobileMoneyPaymentStatus.Failed,
                null, "EXCEPTION", ex.Message, null);
        }
    }

    public async Task<MobileMoneyStatusResult> CheckPaymentStatusAsync(string transactionId)
    {
        if (!_isEnabled)
        {
            return new MobileMoneyStatusResult(
                transactionId, MobileMoneyPaymentStatus.Failed,
                null, null, null, null, "DISABLED", "Mobile money payments are not enabled");
        }

        try
        {
            var response = await _httpClient.GetAsync($"/api/v2/payments/status/{transactionId}");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to check payment status for transaction {TransactionId}: {StatusCode}",
                    transactionId, response.StatusCode);

                return new MobileMoneyStatusResult(
                    transactionId, MobileMoneyPaymentStatus.Failed,
                    null, null, null, null, "API_ERROR", $"Status check failed: {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<CrmStatusResponse>();

            if (result == null)
            {
                return new MobileMoneyStatusResult(
                    transactionId, MobileMoneyPaymentStatus.Failed,
                    null, null, null, null, "PARSE_ERROR", "Failed to parse status response");
            }

            return new MobileMoneyStatusResult(
                transactionId,
                MapStatus(result.Status),
                result.Channel,
                result.Amount,
                result.Currency,
                result.CompletedAt,
                result.ErrorCode,
                result.ErrorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking payment status for transaction {TransactionId}", transactionId);

            return new MobileMoneyStatusResult(
                transactionId, MobileMoneyPaymentStatus.Failed,
                null, null, null, null, "EXCEPTION", ex.Message);
        }
    }

    public Task<PhoneValidationResult> ValidatePhoneAsync(string phoneNumber)
    {
        // Normalize phone number
        var normalized = NormalizePhoneNumber(phoneNumber);

        if (string.IsNullOrEmpty(normalized) || normalized.Length < 9)
        {
            return Task.FromResult(new PhoneValidationResult(
                false, null, null, null, null, "Invalid phone number format"));
        }

        // Detect channel based on prefix
        var channel = DetectChannel(normalized);

        if (channel == null)
        {
            return Task.FromResult(new PhoneValidationResult(
                false, normalized, null, null, null, "Unsupported mobile money provider"));
        }

        return Task.FromResult(new PhoneValidationResult(
            true,
            normalized,
            channel.Code,
            channel.Name,
            channel.Country,
            null));
    }

    public async Task<MobileMoneyPaymentResult> RetryPaymentAsync(string originalTransactionId)
    {
        if (!_isEnabled)
        {
            return new MobileMoneyPaymentResult(
                false, null, null,
                MobileMoneyPaymentStatus.Failed,
                null, "DISABLED", "Mobile money payments are not enabled", null);
        }

        try
        {
            var response = await _httpClient.PostAsync($"/api/v2/payments/retry/{originalTransactionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Mobile money retry failed for {TransactionId}: {StatusCode} - {Error}",
                    originalTransactionId, response.StatusCode, errorContent);

                return new MobileMoneyPaymentResult(
                    false, null, null,
                    MobileMoneyPaymentStatus.Failed,
                    null, "RETRY_FAILED", $"Retry failed: {response.StatusCode}", null);
            }

            var result = await response.Content.ReadFromJsonAsync<CrmCollectResponse>();

            if (result == null)
            {
                return new MobileMoneyPaymentResult(
                    false, null, null,
                    MobileMoneyPaymentStatus.Failed,
                    null, "PARSE_ERROR", "Failed to parse retry response", null);
            }

            _logger.LogInformation(
                "Mobile money payment retry initiated: NewTransactionId={TransactionId}",
                result.TransactionId);

            return new MobileMoneyPaymentResult(
                result.Success,
                result.TransactionId,
                null,
                MapStatus(result.Status),
                null,
                result.ErrorCode,
                result.ErrorMessage,
                result.CustomerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying payment for transaction {TransactionId}", originalTransactionId);

            return new MobileMoneyPaymentResult(
                false, null, null,
                MobileMoneyPaymentStatus.Failed,
                null, "EXCEPTION", ex.Message, null);
        }
    }

    public IEnumerable<MobileMoneyChannel> GetSupportedChannels()
    {
        return SupportedChannels.Where(c => c.IsActive);
    }

    private static string NormalizePhoneNumber(string phone)
    {
        // Remove all non-digit characters
        var digits = new string(phone.Where(char.IsDigit).ToArray());

        // Handle Uganda country code
        if (digits.StartsWith("256") && digits.Length == 12)
        {
            digits = "0" + digits[3..];
        }
        else if (digits.StartsWith("+256"))
        {
            digits = "0" + digits[4..];
        }
        else if (!digits.StartsWith("0") && digits.Length == 9)
        {
            digits = "0" + digits;
        }

        return digits;
    }

    private static MobileMoneyChannel? DetectChannel(string normalizedPhone)
    {
        foreach (var channel in SupportedChannels.Where(c => c.IsActive))
        {
            if (channel.PhonePrefixes.Any(prefix => normalizedPhone.StartsWith(prefix)))
            {
                return channel;
            }
        }

        return null;
    }

    private static MobileMoneyPaymentStatus MapStatus(string? status)
    {
        return status?.ToUpperInvariant() switch
        {
            "PENDING" => MobileMoneyPaymentStatus.Pending,
            "PROCESSING" => MobileMoneyPaymentStatus.Processing,
            "AWAITING_CONFIRMATION" => MobileMoneyPaymentStatus.AwaitingConfirmation,
            "SUCCEEDED" or "SUCCESS" or "COMPLETED" => MobileMoneyPaymentStatus.Succeeded,
            "FAILED" or "FAILURE" => MobileMoneyPaymentStatus.Failed,
            "CANCELLED" => MobileMoneyPaymentStatus.Cancelled,
            "EXPIRED" => MobileMoneyPaymentStatus.Expired,
            "REFUNDED" => MobileMoneyPaymentStatus.Refunded,
            _ => MobileMoneyPaymentStatus.Pending
        };
    }

    #region CRM API DTOs

    private class CrmCollectRequest
    {
        [JsonPropertyName("phoneNumber")]
        public string PhoneNumber { get; set; } = string.Empty;

        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "UGX";

        [JsonPropertyName("narrative")]
        public string Narrative { get; set; } = string.Empty;

        [JsonPropertyName("externalReference")]
        public string? ExternalReference { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, string>? Metadata { get; set; }
    }

    private class CrmCollectResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("transactionId")]
        public string? TransactionId { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("customerMessage")]
        public string? CustomerMessage { get; set; }
    }

    private class CrmStatusResponse
    {
        [JsonPropertyName("transactionId")]
        public string? TransactionId { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("amount")]
        public decimal? Amount { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("completedAt")]
        public DateTime? CompletedAt { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    #endregion
}
