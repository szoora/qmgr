using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;

namespace QMgr.Web.Services;

public interface IModuleApiService
{
    /// <summary>Full 4-module catalog — anonymous-safe, used by the registration wizard too.</summary>
    Task<List<ModuleCatalogItem>> GetCatalogAsync();

    /// <summary>This organization's status for every module.</summary>
    Task<List<OrganizationModuleStatusDto>> GetMineAsync();

    /// <summary>Self-service purchase via Mobile Money. Throws InvalidOperationException with the
    /// API's message on failure.</summary>
    Task<ModulePurchaseResult> PurchaseAsync(string moduleCode, string phoneNumber, string billingCycle);

    /// <summary>Polls a pending purchase after PurchaseAsync returns a transaction id.</summary>
    Task<string> CheckPurchaseStatusAsync(string transactionId);

    /// <summary>Self-service purchase via Stripe (card). Throws InvalidOperationException with the
    /// API's message on failure — including the "Stripe isn't configured for this module yet"
    /// case, which is expected until real Stripe products/prices are set up for production.</summary>
    Task<ModuleCardPurchaseResult> PurchaseWithCardAsync(string moduleCode, string billingCycle);

    Task RemoveModuleAsync(string moduleCode, string? reason);
}

public record ModulePurchaseResult(bool Simulated, string Status, string Message, string? TransactionId);

public record ModuleCardPurchaseResult(bool RequiresCheckout, string? CheckoutUrl, string? Status, string? Message);

public class ModuleApiService : IModuleApiService
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public ModuleApiService(HttpClient httpClient, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _jsonOptions = jsonOptions;
    }

    public async Task<List<ModuleCatalogItem>> GetCatalogAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<ModuleCatalogItem>>("api/v1/modules", _jsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<OrganizationModuleStatusDto>> GetMineAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<OrganizationModuleStatusDto>>("api/v1/modules/mine", _jsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<ModulePurchaseResult> PurchaseAsync(string moduleCode, string phoneNumber, string billingCycle)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/modules/{moduleCode}/purchase",
            new { PhoneNumber = phoneNumber, BillingCycle = billingCycle }, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var simulated = root.TryGetProperty("simulated", out var s) && s.GetBoolean();
        var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        var txId = root.TryGetProperty("transactionId", out var t) ? t.GetString() : null;
        return new ModulePurchaseResult(simulated, status, message, txId);
    }

    public async Task<string> CheckPurchaseStatusAsync(string transactionId)
    {
        var response = await _httpClient.GetAsync($"api/v1/modules/purchase-status/{transactionId}");
        if (!response.IsSuccessStatusCode) return "Failed";
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "Failed" : "Failed";
    }

    public async Task<ModuleCardPurchaseResult> PurchaseWithCardAsync(string moduleCode, string billingCycle)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/modules/{moduleCode}/purchase-card",
            new { BillingCycle = billingCycle }, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var requiresCheckout = root.TryGetProperty("requiresCheckout", out var rc) && rc.GetBoolean();
        var checkoutUrl = root.TryGetProperty("checkoutUrl", out var u) ? u.GetString() : null;
        var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
        var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        return new ModuleCardPurchaseResult(requiresCheckout, checkoutUrl, status, message);
    }

    public async Task RemoveModuleAsync(string moduleCode, string? reason)
    {
        var url = $"api/v1/modules/{moduleCode}";
        if (!string.IsNullOrWhiteSpace(reason)) url += $"?reason={Uri.EscapeDataString(reason)}";
        var response = await _httpClient.DeleteAsync(url);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }
}
