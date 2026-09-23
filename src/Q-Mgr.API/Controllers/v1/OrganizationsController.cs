using QMgr.API.Application.Services;
using QMgr.Infrastructure.Services;
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
using QMgr.Infrastructure.Services.Storage;

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
    private readonly IMediaStorageService _mediaStorage;
    private readonly IUploadAuthorizer _uploadAuthorizer;

    public OrganizationsController(
        QMgrDbContext dbContext,
        ITenantContextAccessor tenantAccessor,
        ILogger<OrganizationsController> logger,
        IFeatureFlagService featureFlagService,
        IPlatformSettingsService platformSettingsService,
        IMediaStorageService mediaStorage,
        IUploadAuthorizer uploadAuthorizer)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
        _featureFlagService = featureFlagService;
        _platformSettingsService = platformSettingsService;
        _mediaStorage = mediaStorage;
        _uploadAuthorizer = uploadAuthorizer;
    }

    /// <summary>
    /// One home for the branding shape. There were four hand-written copies of this object
    /// initializer in this file and a fifth was about to be added for the shell — which is exactly
    /// how <c>DisplayTheme</c> once got added to one and forgotten on another.
    /// </summary>
    private static OrganizationBrandingDto ToDto(QMgr.Domain.Entities.Organization.Organization org, bool? whiteLabelEntitled = null, bool attributionRemoved = false)
        => new()
        {
            WhitelabelEnabled = org.WhitelabelEnabled,
            BrandName = org.BrandName,
            LogoUrl = org.LogoUrl,
            FaviconUrl = org.FaviconUrl,
            PrimaryColor = org.PrimaryColor,
            SecondaryColor = org.SecondaryColor,
            AccentColor = org.AccentColor,
            DisplayTheme = org.DisplayTheme,
            CustomDomain = org.CustomDomain,
            AttributionRemoved = attributionRemoved,
            // The public/display callers never ask about entitlement; `true` is this property's own
            // documented default and keeping it means those responses read exactly as they did.
            WhiteLabelEntitled = whiteLabelEntitled ?? true
        };

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
                Title = "Organization not resolved",
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

        return Ok(ToDto(org));
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

        var features = await _featureFlagService.GetFeaturesAsync(organizationId);

        return Ok(ToDto(org, features.WhiteLabel, features.RemoveAttribution && org.WhitelabelEnabled));
    }

    /// <summary>
    /// Updates the whitelabel branding settings for the caller's own organization.
    /// Gated on the "white_label" feature (not just a permission) since this is a
    /// a module-granted capability, not just an authorization boundary.
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

        var saved = await _featureFlagService.GetFeaturesAsync(organizationId);
        return Ok(ToDto(org, saved.WhiteLabel, saved.RemoveAttribution && org.WhitelabelEnabled));
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

        return Ok(ToDto(org));
    }

    /// <summary>
    /// How this organisation writes a person's name (PeopleNameSettingsDto). Readable by everyone
    /// signed in to the organisation: the Web writes a name itself in one or two places (an import
    /// preview), and a name written in the wrong order there is the inconsistency the setting exists
    /// to remove. Nothing here is sensitive — it is two display choices.
    /// </summary>
    [HttpGet("organizations/{organizationId:guid}/people-names")]
    [ProducesResponseType(typeof(PeopleNameSettingsDto), StatusCodes.Status200OK)]
    public IActionResult GetPeopleNames(Guid organizationId)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;
        return Ok(PersonNames.For(organizationId));
    }

    /// <summary>
    /// Saves the name order (2026-09-23). Through the one writer of Organization.Settings, and the
    /// cache is dropped so the very next name the API writes follows the choice.
    /// </summary>
    [HttpPut("organizations/{organizationId:guid}/people-names")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(PeopleNameSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdatePeopleNames(Guid organizationId, [FromBody] PeopleNameSettingsDto request,
        [FromServices] PersonNameSettingsCache names)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        if (!Enum.IsDefined(request.DisplayOrder) || (request.SortOrder is { } so && !Enum.IsDefined(so)))
            return BadRequest(new ProblemDetails { Title = "Unknown name order", Status = StatusCodes.Status400BadRequest });

        var stored = JsonSerializer.SerializeToElement(request, PersonNames.SettingsJson);
        var found = await OrganizationSettingsLock.MutateAsync(_dbContext, organizationId, org =>
        {
            org.Settings = OrganizationSettingsLock.WithKey(org.Settings, PersonNames.SettingsKey, stored);
            return true;
        });
        if (!found) return NotFound();

        names.Forget(organizationId);
        _logger.LogInformation("Name order set to {Display} (sorted by {Sort}) for organization {OrganizationId}",
            request.DisplayOrder, request.EffectiveSortOrder, organizationId);
        return Ok(PersonNames.For(organizationId));
    }

    // =========================================================================================
    // Brand-asset uploads (logo, favicon).
    //
    // OWASP's File Upload Cheat Sheet is the reference and this codebase already satisfied most
    // of it for every other surface — stored name generated, store outside wwwroot, extension
    // chosen server-side from UploadFileTypes, size capped. Two rules it did NOT satisfy for
    // images are added here: the bytes are PROBED (ImageProbe) and must be the type they claim,
    // and the dimensions are read and bounded.
    //
    // SVG IS REFUSED. OWASP is explicit that SVG allows ECMAScript in almost every context and
    // that the mitigation is to serve it as text/plain or from a separate content domain — a logo
    // served as text/plain is not a logo. PNG, JPEG and WebP only; a favicon has never needed SVG.
    //
    // The gate is settings.edit + the white_label feature, exactly the gate that makes these two
    // fields editable at all on the Branding page. A separate permission code would mean adding it
    // to all three permission catalogues for no boundary the caller has not already crossed.
    // =========================================================================================

    /// <summary>Logo: up to 1 MB, up to 2048px a side.</summary>
    private const long LogoMaxBytes = 1024 * 1024;
    private const int LogoMaxDimension = 2048;

    /// <summary>Favicon: up to 512 KB, square, 64-1024px.</summary>
    private const long FaviconMaxBytes = 512 * 1024;
    private const int FaviconMinDimension = 64;
    private const int FaviconMaxDimension = 1024;

    [HttpPost("organizations/{organizationId:guid}/branding/logo")]
    [RequirePermission(Permissions.SettingsEdit)]
    [RequireFeature(FeatureCodes.WhiteLabel)]
    [RequestSizeLimit(4 * 1024 * 1024)]
    [ProducesResponseType(typeof(OrganizationBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> UploadBrandingLogo(Guid organizationId, IFormFile file, CancellationToken ct)
        => StoreBrandAssetAsync(organizationId, file, isFavicon: false, ct);

    [HttpPost("organizations/{organizationId:guid}/branding/favicon")]
    [RequirePermission(Permissions.SettingsEdit)]
    [RequireFeature(FeatureCodes.WhiteLabel)]
    [RequestSizeLimit(4 * 1024 * 1024)]
    [ProducesResponseType(typeof(OrganizationBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> UploadBrandingFavicon(Guid organizationId, IFormFile file, CancellationToken ct)
        => StoreBrandAssetAsync(organizationId, file, isFavicon: true, ct);

    /// <summary>Clears the asset and deletes the stored file if we were the ones holding it.</summary>
    [HttpDelete("organizations/{organizationId:guid}/branding/{asset:regex(^(logo|favicon)$)}")]
    [RequirePermission(Permissions.SettingsEdit)]
    [RequireFeature(FeatureCodes.WhiteLabel)]
    [ProducesResponseType(typeof(OrganizationBrandingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveBrandAsset(Guid organizationId, string asset, CancellationToken ct)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var org = await _dbContext.Organizations.FindAsync([organizationId], ct);
        if (org == null) return NotFound();

        var isFavicon = asset == "favicon";
        var previous = isFavicon ? org.FaviconUrl : org.LogoUrl;
        if (isFavicon) org.FaviconUrl = null; else org.LogoUrl = null;
        await _dbContext.SaveChangesAsync(ct);
        await DiscardBrandAssetAsync(previous, ct);

        var cleared = await _featureFlagService.GetFeaturesAsync(organizationId);
        return Ok(ToDto(org, cleared.WhiteLabel, cleared.RemoveAttribution && org.WhitelabelEnabled));
    }

    private async Task<IActionResult> StoreBrandAssetAsync(Guid organizationId, IFormFile? file, bool isFavicon, CancellationToken ct)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var label = isFavicon ? "favicon" : "logo";
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "Choose a file to upload." });

        var maxBytes = isFavicon ? FaviconMaxBytes : LogoMaxBytes;
        if (file.Length > maxBytes)
            return BadRequest(new { message = $"That {label} is {Readable(file.Length)}. The limit is {Readable(maxBytes)}." });

        var org = await _dbContext.Organizations.FindAsync([organizationId], ct);
        if (org == null) return NotFound();

        // PROBE THE BYTES, then store under the type the bytes actually are. The client's declared
        // Content-Type and its file name are both ignored for this decision: a ".png" whose bytes
        // are a script is refused here rather than stored and later served as an image.
        await using var incoming = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await incoming.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var probed = ImageProbe.Read(buffer);
        if (probed == null)
            return BadRequest(new { message = "That file is not a PNG, JPEG or WebP image. SVG is not accepted." });

        if (isFavicon)
        {
            if (probed.Width != probed.Height)
                return BadRequest(new { message = $"A favicon has to be square. That one is {probed.Width}x{probed.Height}." });
            if (probed.Width < FaviconMinDimension || probed.Width > FaviconMaxDimension)
                return BadRequest(new { message = $"A favicon has to be between {FaviconMinDimension} and {FaviconMaxDimension} pixels square. That one is {probed.Width}x{probed.Height}." });
        }
        else if (probed.Width > LogoMaxDimension || probed.Height > LogoMaxDimension)
        {
            return BadRequest(new { message = $"A logo can be up to {LogoMaxDimension} pixels a side. That one is {probed.Width}x{probed.Height}." });
        }

        var extension = probed.ContentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => null
        };
        if (extension == null)
            return BadRequest(new { message = "That image type is not accepted." });

        buffer.Position = 0;

        // The name handed to the storage layer carries the PROBED extension, so the extension it
        // stores under — and therefore the Content-Type it will later be served as — comes from
        // the bytes. Passing the client's own name would let "x.webp" holding PNG bytes be stored
        // as .webp, since UploadFileTypes lets a listed extension win within the same family.
        var result = await _mediaStorage.UploadAsync(buffer, $"{label}{extension}", probed.ContentType, ct);
        if (!result.Success || string.IsNullOrEmpty(result.FileUrl))
            return BadRequest(new { message = result.ErrorMessage ?? "The upload could not be stored." });

        var previous = isFavicon ? org.FaviconUrl : org.LogoUrl;
        if (isFavicon) org.FaviconUrl = result.FileUrl; else org.LogoUrl = result.FileUrl;
        await _dbContext.SaveChangesAsync(ct);

        // The classifier caches for 30 seconds and this file has just become public; without the
        // eviction a screen fetching it inside that window gets the orphan answer (token only).
        if (!string.IsNullOrEmpty(result.FilePath))
            _uploadAuthorizer.Invalidate(Path.GetFileName(result.FilePath));

        await DiscardBrandAssetAsync(previous, ct);

        _logger.LogInformation("Brand {Asset} uploaded for organization {OrganizationId} ({Width}x{Height} {ContentType})",
            label, organizationId, probed.Width, probed.Height, probed.ContentType);

        var features = await _featureFlagService.GetFeaturesAsync(organizationId);
        return Ok(ToDto(org, features.WhiteLabel, features.RemoveAttribution && org.WhitelabelEnabled));
    }

    /// <summary>
    /// Deletes the file a brand asset used to point at. Without this a school trying five logos
    /// leaves four files behind that nothing points at — and an orphan is exactly the class the
    /// authorizer can only ever serve token-gated, so the orphan set quietly grows for ever.
    ///
    /// Only ever touches a file inside OUR store: the older free-text field means a tenant may
    /// have typed a link to their own website, and deleting is not what "replace" means there.
    /// </summary>
    private async Task DiscardBrandAssetAsync(string? previousUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(previousUrl)) return;
        if (!previousUrl.Contains("/uploads/media/", StringComparison.OrdinalIgnoreCase)) return;

        var name = Path.GetFileName(Uri.TryCreate(previousUrl, UriKind.Absolute, out var absolute)
            ? absolute.AbsolutePath
            : previousUrl);
        if (string.IsNullOrEmpty(name)) return;

        // Still referenced somewhere else (a tenant pointing both fields at one file) — leave it.
        var stillUsed = await _dbContext.Organizations.IgnoreQueryFilters()
            .AnyAsync(o => (o.LogoUrl != null && o.LogoUrl.EndsWith(name)) || (o.FaviconUrl != null && o.FaviconUrl.EndsWith(name)), ct);
        if (stillUsed) return;

        await _mediaStorage.DeleteAsync(name, ct);
        _uploadAuthorizer.Invalidate(name);
    }

    private static string Readable(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{bytes / 1024d:0} KB";

    // =========================================================================================
    // Branding for the app shell and for a tenant's own host.
    // =========================================================================================

    /// <summary>
    /// The branding the SIGNED-IN SHELL should wear, for the caller's own organization.
    ///
    /// NO PERMISSION CODE, deliberately, and it is the ProfileController rule: this is the caller
    /// asking what their own workspace looks like. The existing organizations/{id}/branding read
    /// is gated on settings.view because it is the EDITOR's read — every member of staff would
    /// need that permission just to see their own school's logo in the sidebar, which is not a
    /// boundary anybody meant to draw.
    ///
    /// Everything that could be withheld is withheld HERE, server-side: an organization that has
    /// switched white-labelling off, or is not entitled to it, gets the standard Q-Mgr look back,
    /// and the client never has to decide.
    /// </summary>
    [HttpGet("branding/mine")]
    [ProducesResponseType(typeof(TenantHostBrandingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyBranding()
    {
        var tenant = _tenantAccessor.TenantContext;
        if (tenant == null || !tenant.IsResolved)
            return Ok(new TenantHostBrandingDto { Resolved = false });

        var org = await _dbContext.Organizations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == tenant.OrganizationId);
        if (org == null)
            return Ok(new TenantHostBrandingDto { Resolved = false });

        return Ok(await ResolveHostBrandingAsync(org));
    }

    /// <summary>
    /// What a browser arriving on this host should look like, before anybody signs in.
    ///
    /// Anonymous — the sign-in page reads it — and it answers for a LIVE tenant domain only. An
    /// unverified or half-configured host is indistinguishable from the platform host, which is
    /// the rule that stops a mistyped DNS record from half-branding a page.
    /// </summary>
    [HttpGet("public/branding/host/{host}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(TenantHostBrandingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBrandingForHost(string host)
    {
        var normalized = (host ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return Ok(new TenantHostBrandingDto { Resolved = false });

        var org = await _dbContext.Organizations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.CustomDomain != null && o.CustomDomain.ToLower() == normalized);

        // CustomDomain is only ever written once verified AND certificated, so matching it IS the
        // verification check — there is no second condition here to forget.
        if (org == null)
            return Ok(new TenantHostBrandingDto { Resolved = false });

        return Ok(await ResolveHostBrandingAsync(org));
    }

    private async Task<TenantHostBrandingDto> ResolveHostBrandingAsync(QMgr.Domain.Entities.Organization.Organization org)
    {
        var features = await _featureFlagService.GetFeaturesAsync(org.Id);

        // Two gates, and both are the tenant's rather than the page's: the switch means what it
        // says, everywhere and not only on the kiosk; and the entitlement, because branding the
        // whole app is a bigger commercial promise than branding the kiosk was.
        if (!org.WhitelabelEnabled || !features.WhiteLabel)
            return new TenantHostBrandingDto { Resolved = false };

        return new TenantHostBrandingDto
        {
            Resolved = true,
            BrandName = string.IsNullOrWhiteSpace(org.BrandName) ? org.Name : org.BrandName,
            LogoUrl = org.LogoUrl,
            FaviconUrl = org.FaviconUrl,
            PrimaryColor = Safe(org.PrimaryColor),
            SecondaryColor = Safe(org.SecondaryColor),
            AccentColor = Safe(org.AccentColor),
            // Attribution removal sits ON TOP of white-labelling, never beside it: taking
            // "Powered by SACC Software" off a page that still says Q-Mgr everywhere is a gap,
            // not a product. Both gates above have already passed by the time this is read.
            AttributionRemoved = features.RemoveAttribution
        };

        // Colours end up inside an inline style attribute on a page served to strangers. They are
        // validated on write, but a row predating that check (or edited by hand) must not be able
        // to close the attribute and open a tag.
        static string? Safe(string? colour) => !string.IsNullOrWhiteSpace(colour) && HexColor.IsMatch(colour) ? colour : null;
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

        // Through the one writer of Organization.Settings (2026-09-23): this read-modify-wrote the
        // whole blob with no lock, so a save here could drop a key another writer set a moment before.
        var found = await OrganizationSettingsLock.MutateAsync(_dbContext, organizationId, org =>
        {
            org.IndustryType = request.IndustryType;
            org.Settings = OrganizationSettingsLock.WithKey(org.Settings, "IndustryFeatures", request.Features);
            return true;
        });
        if (!found)
            return NotFound();

        _logger.LogInformation("Industry settings updated to '{IndustryType}' for organization {OrganizationId}", request.IndustryType, organizationId);

        return Ok(new IndustrySettingsDto
        {
            IndustryType = request.IndustryType,
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
