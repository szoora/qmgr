using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1")]
[Authorize]
[Produces("application/json")]
public class OrganizationsController : ControllerBase
{
    private static readonly Regex HexColor = new(@"^#[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);

    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<OrganizationsController> _logger;
    private readonly IFeatureFlagService _featureFlagService;
    private readonly IPlatformSettingsService _platformSettingsService;

    public OrganizationsController(QMgrDbContext dbContext, ITenantContextAccessor tenantAccessor, ILogger<OrganizationsController> logger, IFeatureFlagService featureFlagService, IPlatformSettingsService platformSettingsService)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
        _featureFlagService = featureFlagService;
        _platformSettingsService = platformSettingsService;
    }

    /// <summary>
    /// SECURITY: organizationId is a client-supplied route parameter, and Organization
    /// has no global tenant query filter (Super Admin needs to see all orgs elsewhere),
    /// so every org-scoped endpoint here must verify ownership explicitly — without this,
    /// any authenticated tenant admin could read or overwrite another tenant's branding
    /// just by passing a different org GUID.
    /// </summary>
    private IActionResult? VerifyOrganizationOwnership(Guid organizationId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
            return null;

        if (organizationId != tenantContext.OrganizationId)
            return NotFound(new ProblemDetails
            {
                Title = "Organization not found",
                Detail = $"Organization with ID '{organizationId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }

    /// <summary>
    /// Gets the public whitelabel branding for the organization that owns a branch.
    /// Anonymous by design — this is read by unauthenticated kiosk/customer-display
    /// terminals — and deliberately returns only a narrow, safe subset of
    /// Organization (see OrganizationBrandingDto). Returns default/disabled
    /// branding (never a 404/error) for an unknown branch or a tenant that hasn't
    /// enabled whitelabel, so callers can always fall back to the standard Q-Mgr
    /// look without special-casing.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/branding")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(OrganizationBrandingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBranchBranding(Guid branchId)
    {
        var org = await _dbContext.Branches
            .Where(b => b.Id == branchId)
            .Select(b => b.Organization)
            .FirstOrDefaultAsync();

        if (org == null)
        {
            return Ok(new OrganizationBrandingDto { WhitelabelEnabled = false, DisplayTheme = "dark" });
        }

        if (!org.WhitelabelEnabled)
        {
            // DisplayTheme still applies even when whitelabel (colors/logo) isn't enabled —
            // it's a basic display preference, not a paid customization.
            return Ok(new OrganizationBrandingDto { WhitelabelEnabled = false, DisplayTheme = org.DisplayTheme });
        }

        return Ok(new OrganizationBrandingDto
        {
            WhitelabelEnabled = true,
            BrandName = org.BrandName,
            LogoUrl = org.LogoUrl,
            FaviconUrl = org.FaviconUrl,
            PrimaryColor = org.PrimaryColor,
            SecondaryColor = org.SecondaryColor,
            AccentColor = org.AccentColor,
            DisplayTheme = org.DisplayTheme
        });
    }

    /// <summary>
    /// Gets whether ads should show for this branch's organization, and which provider/client-id
    /// to render with — anonymous, for public kiosk/display screens (same pattern as
    /// GetBranchBranding above). Folds together the org's plan entitlement
    /// (IFeatureFlagService.ShowAds) and the platform-wide Ads.ShowAdsOnFreePlan toggle so the
    /// caller only needs one boolean.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/ads-config")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AdsConfigDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAdsConfig(Guid branchId)
    {
        var org = await _dbContext.Branches
            .Where(b => b.Id == branchId)
            .Select(b => b.Organization)
            .FirstOrDefaultAsync();

        if (org == null)
            return Ok(new AdsConfigDto { ShouldShowAds = false });

        var features = await _featureFlagService.GetFeaturesAsync(org.Id);
        if (!features.ShowAds)
            return Ok(new AdsConfigDto { ShouldShowAds = false });

        var adsSettings = await _platformSettingsService.GetSettingsAsync<AdsSettings>("Ads");
        if (adsSettings == null || !adsSettings.ShowAdsOnFreePlan)
            return Ok(new AdsConfigDto { ShouldShowAds = false });

        return Ok(new AdsConfigDto
        {
            ShouldShowAds = true,
            Provider = adsSettings.Provider,
            GoogleAdSenseClientId = adsSettings.GoogleAdSenseClientId
        });
    }

    /// <summary>
    /// Gets the current whitelabel branding settings for the caller's own organization,
    /// for the admin branding-settings page (as opposed to the anonymous, branch-scoped,
    /// public-display-facing endpoint above). Viewing your own current settings — even
    /// if the org isn't entitled to whitelabel — is harmless, so this isn't feature-gated;
    /// only the write endpoint below is.
    /// </summary>
    [HttpGet("organizations/{organizationId:guid}/branding")]
    [RequirePermission(Permissions.SettingsView)]
    [ProducesResponseType(typeof(OrganizationBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrganizationBranding(Guid organizationId)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var org = await _dbContext.Organizations.FindAsync(organizationId);
        if (org == null)
            return NotFound();

        var whiteLabelEntitled = await _featureFlagService.IsFeatureEnabledAsync(organizationId, FeatureCodes.WhiteLabel);

        return Ok(new OrganizationBrandingDto
        {
            WhitelabelEnabled = org.WhitelabelEnabled,
            BrandName = org.BrandName,
            LogoUrl = org.LogoUrl,
            FaviconUrl = org.FaviconUrl,
            PrimaryColor = org.PrimaryColor,
            SecondaryColor = org.SecondaryColor,
            AccentColor = org.AccentColor,
            DisplayTheme = org.DisplayTheme,
            WhiteLabelEntitled = whiteLabelEntitled
        });
    }

    /// <summary>
    /// Updates the whitelabel branding settings for the caller's own organization.
    /// Gated on the "white_label" feature (not just a permission) since this is a
    /// paid-tier capability, not just an authorization boundary.
    /// </summary>
    [HttpPut("organizations/{organizationId:guid}/branding")]
    [RequirePermission(Permissions.SettingsEdit)]
    [RequireFeature(FeatureCodes.WhiteLabel)]
    [ProducesResponseType(typeof(OrganizationBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateOrganizationBranding(Guid organizationId, [FromBody] UpdateOrganizationBrandingRequest request)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var org = await _dbContext.Organizations.FindAsync(organizationId);
        if (org == null)
            return NotFound();

        foreach (var color in new[] { request.PrimaryColor, request.SecondaryColor, request.AccentColor })
        {
            if (!string.IsNullOrEmpty(color) && !HexColor.IsMatch(color))
                return BadRequest(new { message = $"'{color}' is not a valid hex color." });
        }

        org.WhitelabelEnabled = request.WhitelabelEnabled;
        org.BrandName = request.BrandName;
        org.LogoUrl = request.LogoUrl;
        org.FaviconUrl = request.FaviconUrl;
        org.PrimaryColor = request.PrimaryColor;
        org.SecondaryColor = request.SecondaryColor;
        org.AccentColor = request.AccentColor;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Whitelabel branding updated for organization {OrganizationId}", organizationId);

        return Ok(new OrganizationBrandingDto
        {
            WhitelabelEnabled = org.WhitelabelEnabled,
            BrandName = org.BrandName,
            LogoUrl = org.LogoUrl,
            FaviconUrl = org.FaviconUrl,
            PrimaryColor = org.PrimaryColor,
            SecondaryColor = org.SecondaryColor,
            AccentColor = org.AccentColor,
            DisplayTheme = org.DisplayTheme
        });
    }

    /// <summary>
    /// Updates the public-display theme ("dark"/"light") for the caller's own organization.
    /// Deliberately separate from UpdateOrganizationBranding above: no [RequireFeature]
    /// gate, since this is a basic display preference available on every plan, not a
    /// paid whitelabel customization.
    /// </summary>
    [HttpPut("organizations/{organizationId:guid}/display-theme")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(OrganizationBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDisplayTheme(Guid organizationId, [FromBody] UpdateDisplayThemeRequest request)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        if (request.DisplayTheme != "dark" && request.DisplayTheme != "light")
            return BadRequest(new { message = "DisplayTheme must be 'dark' or 'light'." });

        var org = await _dbContext.Organizations.FindAsync(organizationId);
        if (org == null)
            return NotFound();

        org.DisplayTheme = request.DisplayTheme;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Display theme updated to '{DisplayTheme}' for organization {OrganizationId}", org.DisplayTheme, organizationId);

        return Ok(new OrganizationBrandingDto
        {
            WhitelabelEnabled = org.WhitelabelEnabled,
            BrandName = org.BrandName,
            LogoUrl = org.LogoUrl,
            FaviconUrl = org.FaviconUrl,
            PrimaryColor = org.PrimaryColor,
            SecondaryColor = org.SecondaryColor,
            AccentColor = org.AccentColor,
            DisplayTheme = org.DisplayTheme
        });
    }

    /// <summary>
    /// Gets the caller's own organization's industry type and kiosk feature toggles.
    /// Features persist as JSON inside Organization.Settings (a generic settings blob with no
    /// other consumer) under the "IndustryFeatures" key.
    /// </summary>
    [HttpGet("organizations/{organizationId:guid}/industry-settings")]
    [RequirePermission(Permissions.SettingsView)]
    [ProducesResponseType(typeof(IndustrySettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetIndustrySettings(Guid organizationId)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var org = await _dbContext.Organizations.FindAsync(organizationId);
        if (org == null)
            return NotFound();

        return Ok(new IndustrySettingsDto
        {
            IndustryType = org.IndustryType,
            Features = ReadIndustryFeatures(org.Settings)
        });
    }

    /// <summary>
    /// Updates the caller's own organization's industry type and kiosk feature toggles.
    /// </summary>
    [HttpPut("organizations/{organizationId:guid}/industry-settings")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(IndustrySettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateIndustrySettings(Guid organizationId, [FromBody] IndustrySettingsDto request)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var org = await _dbContext.Organizations.FindAsync(organizationId);
        if (org == null)
            return NotFound();

        org.IndustryType = request.IndustryType;

        var settingsRoot = string.IsNullOrEmpty(org.Settings)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(org.Settings) ?? new();
        var merged = new Dictionary<string, object>();
        foreach (var (key, value) in settingsRoot)
        {
            merged[key] = value;
        }
        merged["IndustryFeatures"] = request.Features;
        org.Settings = JsonSerializer.Serialize(merged);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Industry settings updated to '{IndustryType}' for organization {OrganizationId}", org.IndustryType, organizationId);

        return Ok(new IndustrySettingsDto
        {
            IndustryType = org.IndustryType,
            Features = request.Features
        });
    }

    private static Dictionary<string, bool> ReadIndustryFeatures(string? orgSettingsJson)
    {
        if (string.IsNullOrEmpty(orgSettingsJson))
            return new();

        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(orgSettingsJson);
            if (root != null && root.TryGetValue("IndustryFeatures", out var featuresElement))
            {
                return JsonSerializer.Deserialize<Dictionary<string, bool>>(featuresElement.GetRawText()) ?? new();
            }
        }
        catch (JsonException)
        {
            // Malformed/legacy Settings blob — treat as no saved feature toggles yet.
        }

        return new();
    }
}
