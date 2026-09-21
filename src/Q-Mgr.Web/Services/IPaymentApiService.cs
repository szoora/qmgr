using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Payments through the sacc.ug gateway, from the Web (2026-09-19): the tenant's side (which ways to pay
/// exist, the renewal number, paying an invoice, following a payment) and the platform's side (the
/// gateway's settings, the connection check, the webhook, the test prompt, reconciliation).
///
/// Every write THROWS — <see cref="ApiFieldException"/> when the API named the fields at fault, so a form
/// can mark each one — the same contract as every other typed service except <c>IQueueApiService</c>.
/// Status reads used by a poller return null on a failed read instead: one dropped poll must not end
/// the wait for a payment the gateway is still holding.
/// </summary>
public interface IPaymentApiService
{
    Task<PaymentProvidersDto> GetProvidersAsync();
    Task<RenewalNumberDto> GetRenewalNumberAsync();
    Task<RenewalNumberDto> UpdateRenewalNumberAsync(string phoneNumber);
    Task<RenewalNumberDto> ClearRenewalNumberAsync();
    Task<PaymentStartDto> PayInvoiceAsync(Guid invoiceId, string phoneNumber);
    Task<PaymentStatusDto?> GetPaymentAsync(Guid referenceId);

    Task<GatewaySettingsDto> GetGatewayAsync();
    Task<GatewaySettingsDto> UpdateGatewayAsync(UpdateGatewaySettingsRequest request);
    Task<GatewayCheckDto> CheckGatewayAsync();
    Task<WebhookRegistrationDto> RegisterWebhookAsync(string? callbackUrl);
    Task<TestPromptStatusDto> SendTestPromptAsync(string phoneNumber);
    Task<TestPromptStatusDto?> GetTestPromptAsync(Guid referenceId);
    Task<ReconciliationDto> GetReconciliationAsync(DateOnly from, DateOnly to);
    Task ResolveReviewAsync(Guid referenceId, ResolveReviewRequest request);
}

/// <summary>A refusal that named the fields at fault (<c>errors: { field: message }</c>). The message is
/// the API's own summary; <see cref="Errors"/> is keyed by the request's camelCase field names.</summary>
public sealed class ApiFieldException : InvalidOperationException
{
    public IReadOnlyDictionary<string, string> Errors { get; }
    public string? Code { get; }

    public ApiFieldException(string message, string? code, IReadOnlyDictionary<string, string> errors) : base(message)
    {
        Code = code;
        Errors = errors;
    }

    public string? For(string field) => Errors.TryGetValue(field, out var message) ? message : null;

    /// <summary>Throw the right exception for a failed response: field errors when the body carries
    /// them, otherwise the API's message.</summary>
    public static async Task ThrowAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        string? message = null, code = null;
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
            if (root.TryGetProperty("error", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
            if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in e.EnumerateObject())
                {
                    var text = field.Value.ValueKind switch
                    {
                        JsonValueKind.String => field.Value.GetString(),
                        JsonValueKind.Array => field.Value.EnumerateArray().Select(v => v.GetString()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(text)) errors[field.Name] = text!;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the generic wording.
        }

        message ??= ApiErrorService.GetErrorMessageFromBody(body, $"Request failed — {(int)response.StatusCode} {response.StatusCode}");
        throw new ApiFieldException(message, code, errors);
    }
}

public class PaymentApiService : IPaymentApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public PaymentApiService(HttpClient http, JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    // ------------------------------------------------------------------ tenant

    public Task<PaymentProvidersDto> GetProvidersAsync() => GetAsync<PaymentProvidersDto>("api/v1/billing/payment-providers");

    public Task<RenewalNumberDto> GetRenewalNumberAsync() => GetAsync<RenewalNumberDto>("api/v1/billing/renewal-number");

    public Task<RenewalNumberDto> UpdateRenewalNumberAsync(string phoneNumber) =>
        SendAsync<RenewalNumberDto>(HttpMethod.Put, "api/v1/billing/renewal-number", new UpdateRenewalNumberRequest(phoneNumber));

    public async Task<RenewalNumberDto> ClearRenewalNumberAsync()
    {
        var response = await _http.DeleteAsync("api/v1/billing/renewal-number");
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
        return (await response.Content.ReadFromJsonAsync<RenewalNumberDto>(_json))!;
    }

    public Task<PaymentStartDto> PayInvoiceAsync(Guid invoiceId, string phoneNumber) =>
        SendAsync<PaymentStartDto>(HttpMethod.Post, $"api/v1/billing/invoices/{invoiceId}/pay", new PayInvoiceRequest(phoneNumber));

    public Task<PaymentStatusDto?> GetPaymentAsync(Guid referenceId) => TryGetAsync<PaymentStatusDto>($"api/v1/billing/payments/{referenceId}");

    // ------------------------------------------------------------------ platform

    public Task<GatewaySettingsDto> GetGatewayAsync() => GetAsync<GatewaySettingsDto>("api/v1/platform/payments/gateway");

    public Task<GatewaySettingsDto> UpdateGatewayAsync(UpdateGatewaySettingsRequest request) =>
        SendAsync<GatewaySettingsDto>(HttpMethod.Put, "api/v1/platform/payments/gateway", request);

    public Task<GatewayCheckDto> CheckGatewayAsync() =>
        SendAsync<GatewayCheckDto>(HttpMethod.Post, "api/v1/platform/payments/gateway/check", new { });

    public Task<WebhookRegistrationDto> RegisterWebhookAsync(string? callbackUrl) =>
        SendAsync<WebhookRegistrationDto>(HttpMethod.Post, "api/v1/platform/payments/gateway/webhook", new RegisterWebhookRequest(callbackUrl));

    public Task<TestPromptStatusDto> SendTestPromptAsync(string phoneNumber) =>
        SendAsync<TestPromptStatusDto>(HttpMethod.Post, "api/v1/platform/payments/gateway/test-prompt", new TestPromptRequest(phoneNumber));

    public Task<TestPromptStatusDto?> GetTestPromptAsync(Guid referenceId) =>
        TryGetAsync<TestPromptStatusDto>($"api/v1/platform/payments/gateway/test-prompt/{referenceId}");

    public Task<ReconciliationDto> GetReconciliationAsync(DateOnly from, DateOnly to) =>
        GetAsync<ReconciliationDto>($"api/v1/platform/payments/reconciliation?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public async Task ResolveReviewAsync(Guid referenceId, ResolveReviewRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/v1/platform/payments/{referenceId}/resolve")
        {
            Content = JsonContent.Create(request, options: _json)
        };
        var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }

    private async Task<T?> TryGetAsync<T>(string url) where T : class
    {
        try
        {
            var response = await _http.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<T>(_json);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object body)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: _json) };
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }
}
