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

    /// <summary>What a browser arriving on this host should look like. Never throws — an unbranded page is always the right fallback.</summary>
    Task<TenantHostBrandingDto> GetBrandingForHostAsync(string host);

    /// <summary>The branding the signed-in shell should wear, for the caller's own organization. Never throws.</summary>
    Task<TenantHostBrandingDto> GetMyBrandingAsync();

    /// <summary>Uploads a logo or favicon. Returns the updated branding, or throws with the API's own message.</summary>
    Task<OrganizationBrandingDto> UploadBrandAssetAsync(Guid organizationId, bool isFavicon, Stream content, string fileName, string contentType);

    /// <summary>Clears a logo or favicon and deletes the stored file.</summary>
    Task<OrganizationBrandingDto> RemoveBrandAssetAsync(Guid organizationId, bool isFavicon);

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
    private static readonly TenantHostBrandingDto Unbranded = new() { Resolved = false };
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

    public async Task<TenantHostBrandingDto> GetBrandingForHostAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return Unbranded;
        try
        {
            var result = await _httpClient.GetFromJsonAsync<TenantHostBrandingDto>(
                $"api/v1/public/branding/host/{Uri.EscapeDataString(host)}", _jsonOptions);
            return result ?? Unbranded;
        }
        catch (Exception ex)
        {
            // The sign-in page must render even when the API is unreachable. Unbranded is the
            // standard Q-Mgr look, which is what every visitor saw before this existed — the same
            // fail-open as every module gate here.
            _logger.LogWarning(ex, "Failed to resolve branding for host {Host} — falling back to the platform look", host);
            return Unbranded;
        }
    }

    public async Task<TenantHostBrandingDto> GetMyBrandingAsync()
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<TenantHostBrandingDto>("api/v1/branding/mine", _jsonOptions);
            return result ?? Unbranded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve the caller's own branding — falling back to the platform look");
            return Unbranded;
        }
    }

    public async Task<OrganizationBrandingDto> UploadBrandAssetAsync(Guid organizationId, bool isFavicon, Stream content, string fileName, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        var response = await _httpClient.PostAsync(
            $"api/v1/organizations/{organizationId}/branding/{(isFavicon ? "favicon" : "logo")}", form);
        return await ReadBrandingAsync(response);
    }

    public async Task<OrganizationBrandingDto> RemoveBrandAssetAsync(Guid organizationId, bool isFavicon)
    {
        var response = await _httpClient.DeleteAsync(
            $"api/v1/organizations/{organizationId}/branding/{(isFavicon ? "favicon" : "logo")}");
        return await ReadBrandingAsync(response);
    }

    /// <summary>
    /// Throws with the API's OWN sentence. These two say exactly why a file was refused — the size
    /// it was against the limit, the dimensions it was — and swallowing that for a generic failure
    /// would leave somebody guessing which of four rules they broke.
    /// </summary>
    private async Task<OrganizationBrandingDto> ReadBrandingAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));

        return await response.Content.ReadFromJsonAsync<OrganizationBrandingDto>(_jsonOptions)
               ?? throw new InvalidOperationException("The server did not return the updated branding.");
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
