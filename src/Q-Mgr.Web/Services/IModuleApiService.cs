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

    /// <summary>Buy a module through the sacc.ug gateway — Mobile Money or card (2026-09-19). Throws
    /// <see cref="ApiFieldException"/> with the API's message when it is refused.</summary>
    Task<PaymentStartDto> PurchaseAsync(string moduleCode, ModulePurchaseRequest request);

    /// <summary>Where a purchase stands, by the reference PurchaseAsync returned. Null on a failed read,
    /// so one dropped poll does not end the wait.</summary>
    Task<PaymentStatusDto?> GetPurchaseStatusAsync(Guid referenceId);

    /// <summary>Self-service purchase via Stripe (card) — used only when the platform has configured
    /// Stripe, which it is not by default (2026-09-19). Cards otherwise go through the gateway.</summary>
    Task<ModuleCardPurchaseResult> PurchaseWithCardAsync(string moduleCode, string billingCycle);

    Task RemoveModuleAsync(string moduleCode, string? reason);
}

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

    /// <summary>
    /// Throws when the call fails, unlike <see cref="GetCatalogAsync"/>. An empty list here means
    /// "owns nothing", and every caller turns that into a decision: the layout's module guard sent a
    /// paying customer to Billing, and after a refused refresh it sent a signed-out one there on the
    /// way to the sign-in page (found 2026-09-17). Swallowing the error made the fail-open branches in
    /// ModuleStateService and Dashboard unreachable; they now run.
    /// </summary>
    public async Task<List<OrganizationModuleStatusDto>> GetMineAsync()
        => await _httpClient.GetFromJsonAsync<List<OrganizationModuleStatusDto>>("api/v1/modules/mine", _jsonOptions) ?? new();

    public async Task<PaymentStartDto> PurchaseAsync(string moduleCode, ModulePurchaseRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/modules/{Uri.EscapeDataString(moduleCode)}/purchase", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
        return (await response.Content.ReadFromJsonAsync<PaymentStartDto>(_jsonOptions))!;
    }

    public async Task<PaymentStatusDto?> GetPurchaseStatusAsync(Guid referenceId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/v1/modules/purchase-status/{referenceId}");
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<PaymentStatusDto>(_jsonOptions) : null;
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
