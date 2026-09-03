using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface IOrganizationApiService
{
    Task<OrganizationBrandingDto> GetBranchBrandingAsync(Guid branchId);

    /// <summary>
    /// Anonymous public identity of a branch (GET api/v1/branches/{id}/public).
    /// Returns null when the branch doesn't exist / is inactive (404) — or when the
    /// lookup fails outright — so the public pages can show a "this branch link is
    /// not valid" state instead of rendering against a branch they can't confirm.
    /// </summary>
    Task<BranchPublicDto?> GetBranchPublicAsync(Guid branchId);

    Task<OrganizationBrandingDto?> GetOrganizationBrandingAsync(Guid organizationId);
    Task<HttpResponseMessage> UpdateOrganizationBrandingAsync(Guid organizationId, OrganizationBrandingDto branding);
    Task<HttpResponseMessage> UpdateDisplayThemeAsync(Guid organizationId, string displayTheme);

    Task<DisplayBannerSettingsDto> GetDisplayBannerAsync(Guid branchId);
    Task<HttpResponseMessage> UpdateDisplayBannerAsync(Guid branchId, DisplayBannerSettingsDto banner);

    Task<IndustrySettingsDto?> GetIndustrySettingsAsync(Guid organizationId);
    Task<HttpResponseMessage> UpdateIndustrySettingsAsync(Guid organizationId, IndustrySettingsDto settings);

    Task<AdsConfigDto> GetAdsConfigAsync(Guid branchId);
}

public class OrganizationApiService : IOrganizationApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OrganizationApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    private static readonly OrganizationBrandingDto Disabled = new() { WhitelabelEnabled = false };
    private static readonly DisplayBannerSettingsDto BannerDisabled = new() { Enabled = false };

    public OrganizationApiService(HttpClient httpClient, ILogger<OrganizationApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public async Task<OrganizationBrandingDto> GetBranchBrandingAsync(Guid branchId)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<OrganizationBrandingDto>(
                $"api/v1/branches/{branchId}/branding", _jsonOptions);
            return result ?? Disabled;
        }
        catch (Exception ex)
        {
            // Branding is cosmetic, never let a lookup failure break the display/kiosk screen.
            _logger.LogWarning(ex, "Failed to get branch branding for {BranchId} — falling back to default branding", branchId);
            return Disabled;
        }
    }

    public async Task<BranchPublicDto?> GetBranchPublicAsync(Guid branchId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/v1/branches/{branchId}/public");
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<BranchPublicDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to look up public branch info for {BranchId}", branchId);
            return null;
        }
    }

    public async Task<OrganizationBrandingDto?> GetOrganizationBrandingAsync(Guid organizationId)
    {
        var response = await _httpClient.GetAsync($"api/v1/organizations/{organizationId}/branding");
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<OrganizationBrandingDto>(_jsonOptions);
    }

    public Task<HttpResponseMessage> UpdateOrganizationBrandingAsync(Guid organizationId, OrganizationBrandingDto branding)
    {
        return _httpClient.PutAsJsonAsync($"api/v1/organizations/{organizationId}/branding", branding, _jsonOptions);
    }

    public Task<HttpResponseMessage> UpdateDisplayThemeAsync(Guid organizationId, string displayTheme)
    {
        return _httpClient.PutAsJsonAsync(
            $"api/v1/organizations/{organizationId}/display-theme",
            new UpdateDisplayThemeRequest { DisplayTheme = displayTheme },
            _jsonOptions);
    }

    public async Task<DisplayBannerSettingsDto> GetDisplayBannerAsync(Guid branchId)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<DisplayBannerSettingsDto>(
                $"api/v1/branches/{branchId}/display-banner", _jsonOptions);
            return result ?? BannerDisabled;
        }
        catch (Exception ex)
        {
            // Same fail-safe as branding: never let a lookup failure break the public display.
            _logger.LogWarning(ex, "Failed to get display banner for branch {BranchId} — banner will not render", branchId);
            return BannerDisabled;
        }
    }

    public Task<HttpResponseMessage> UpdateDisplayBannerAsync(Guid branchId, DisplayBannerSettingsDto banner)
    {
        return _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/display-banner", banner, _jsonOptions);
    }

    public async Task<IndustrySettingsDto?> GetIndustrySettingsAsync(Guid organizationId)
    {
        var response = await _httpClient.GetAsync($"api/v1/organizations/{organizationId}/industry-settings");
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<IndustrySettingsDto>(_jsonOptions);
    }

    public Task<HttpResponseMessage> UpdateIndustrySettingsAsync(Guid organizationId, IndustrySettingsDto settings)
    {
        return _httpClient.PutAsJsonAsync($"api/v1/organizations/{organizationId}/industry-settings", settings, _jsonOptions);
    }

    public async Task<AdsConfigDto> GetAdsConfigAsync(Guid branchId)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<AdsConfigDto>(
                $"api/v1/branches/{branchId}/ads-config", _jsonOptions);
            return result ?? new AdsConfigDto { ShouldShowAds = false };
        }
        catch (Exception ex)
        {
            // Same fail-safe as branding/banner: never let a lookup failure break the display.
            _logger.LogWarning(ex, "Failed to get ads config for branch {BranchId} — ads will not render", branchId);
            return new AdsConfigDto { ShouldShowAds = false };
        }
    }
}
