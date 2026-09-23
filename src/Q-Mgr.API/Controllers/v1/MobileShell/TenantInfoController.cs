using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.Branding;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1.MobileShell;

/// <summary>
/// What the mobile app learns about an address BEFORE a password is typed.
///
/// <para><b>Why it exists at all.</b> Without it, a mistyped workspace surfaces much later as a
/// failed login that the user reads as a wrong password. With it, the app can say "no Q-Mgr
/// workspace at that address" while they are still on the field that caused it.</para>
///
/// <para><b>Deliberately flat, not wrapped</b> in the <c>{ success, data }</c> envelope the rest of
/// the mobile surface uses. It is the first call made against an unknown host, so it is the one
/// shape that has to be readable without knowing anything about this server.</para>
///
/// <para><b>Q-Mgr answers two ways, and the difference is the whole reason this file needed
/// thought.</b> On a tenant's own verified domain it names the school. On the shared platform host
/// every school shares one address and there is NO tenant, so it answers with the platform's
/// identity and a null tenant — never an invented school.</para>
///
/// <para><b>503 is reserved for a host that is not a Q-Mgr install.</b> It is tempting to return it
/// whenever no tenant resolves, and that would be wrong here: the shared host is a perfectly
/// legitimate workspace for every school that has not bought a domain, which is most of them.
/// Answering 503 there would make the app refuse the address almost every school uses.</para>
/// </summary>
[ApiController]
[Route("api/v1/tenant")]
[AllowAnonymous]
[Produces("application/json")]
public class TenantInfoController : ControllerBase
{
    private readonly QMgrDbContext _db;
    private readonly IFeatureFlagService _features;
    private readonly ILogger<TenantInfoController> _log;

    /// <summary>
    /// The product's own descriptor, in the words the product uses about itself. "Front Office" is
    /// two words by decision (2026-09-22): the hyphen went, and "Platform" went before it as
    /// redundant. The app displays what it is told and never composes this itself.
    /// </summary>
    private const string ProductName = "Q-Mgr";
    private const string ProductDescriptor = "Front Office";

    public TenantInfoController(QMgrDbContext db, IFeatureFlagService features,
                               ILogger<TenantInfoController> log)
    {
        _db = db;
        _features = features;
        _log = log;
    }

    [HttpGet("info")]
    [ProducesResponseType(typeof(TenantInfoResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Info()
    {
        var host = (Request.Host.Host ?? string.Empty).Trim().ToLowerInvariant();

        // A LIVE tenant domain, and only that. CustomDomain is written only once a domain is
        // verified AND its certificate is in place, so matching it IS the verification check —
        // exactly as GetBrandingForHost does it, and for the same reason: a half-configured host
        // must be indistinguishable from the platform host, or a mistyped DNS record half-brands
        // a sign-in screen.
        var org = host.Length == 0
            ? null
            : await _db.Organizations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.CustomDomain != null && o.CustomDomain.ToLower() == host);

        if (org != null)
            return Ok(await ForTenantAsync(org));

        return Ok(ForPlatform());
    }

    /// <summary>
    /// A school's own domain. The app wears their name, their logo and their colours from the
    /// sign-in screen onwards, and carries their organisation id so the workspace is keyed per host
    /// AND school.
    /// </summary>
    private async Task<TenantInfoResponse> ForTenantAsync(QMgr.Domain.Entities.Organization.Organization org)
    {
        var features = await _features.GetFeaturesAsync(org.Id);

        // The same two gates the web sign-in page applies, and they are the tenant's rather than
        // this endpoint's: the switch means what it says everywhere, and the entitlement, because
        // branding a whole installed app is a larger promise than branding a kiosk was.
        var branded = org.WhitelabelEnabled && features.WhiteLabel;

        return new TenantInfoResponse
        {
            CompanyName = branded && !string.IsNullOrWhiteSpace(org.BrandName) ? org.BrandName! : org.Name,
            Product = ProductName,
            Descriptor = ProductDescriptor,
            Tenant = org.Slug,
            OrganizationId = org.Id,
            Status = org.Status.ToString(),
            LogoUrl = branded ? Absolute(org.LogoUrl) : null,
            // DELIBERATELY NULL — see ProductLogoController. Q-Mgr's mark is an SVG and MAUI cannot
            // decode SVG at run time, so advertising it would render a broken image that reads as a
            // failed download. The app carries Q-Mgr's mark compiled in; a TENANT's LogoUrl above is
            // a different case and is sent, because uploaded logos are raster (ImageProbe refuses
            // SVG outright).
            ProductLogoUrl = null,
            Brand = branded
                ? new TenantBrandDto
                {
                    Primary = Safe(org.PrimaryColor),
                    Secondary = Safe(org.SecondaryColor),
                    Accent = Safe(org.AccentColor),

                    // THE SERVER DERIVES, THE APP DOES NOT. An earlier comment here said the app
                    // would work out its own hover and dark tones from three colours — it did, and
                    // it got them wrong, because the one value that cannot be guessed is the text
                    // colour ON the brand (WCAG luminance) and the one that must not be is the
                    // hover, which has to match the web pixel for pixel. BrandPalette is the single
                    // home for that arithmetic and the web reads the same method.
                    Colors = BrandPalette.ColorsFor(
                        Safe(org.PrimaryColor), Safe(org.SecondaryColor), Safe(org.AccentColor))
                }
                : null,
            // Attribution removal sits ON TOP of white-labelling and is never beside it. A tenant
            // who paid for it and then reads "Q-Mgr" on the app's own splash has not had it.
            AttributionRemoved = branded && features.RemoveAttribution,
            Api = Capabilities
        };
    }

    /// <summary>
    /// The shared platform host. There is no tenant here — the school comes from whoever signs in
    /// — so this names the PRODUCT and nothing else. Inventing a company name from the first
    /// organisation in the table, or from a guess at the subdomain, would put one school's name in
    /// front of another school's staff.
    /// </summary>
    private TenantInfoResponse ForPlatform() => new()
    {
        CompanyName = ProductName,
        Product = ProductName,
        Descriptor = ProductDescriptor,
        Tenant = null,
        OrganizationId = null,
        Status = "Active",
        LogoUrl = null,
        ProductLogoUrl = null,   // as above — MAUI cannot render the SVG mark at run time
        Brand = null,
        AttributionRemoved = false,
        Api = Capabilities
    };

    /// <summary>
    /// What this build can do, so the app detects an older deployment without probing each route
    /// for a 404. Every one of these is true here; they exist because the app is expected to run
    /// against a server that predates them.
    /// </summary>
    private static TenantApiCapabilitiesDto Capabilities => new()
    {
        Version = "v1",
        Refresh = true,
        WebSession = true,
        DeviceSessions = true,
        Push = true
    };

    /// <summary>
    /// Absolute, from the host the app actually reached.
    ///
    /// <para>This is the one place in the codebase where building a link from the request host is
    /// CORRECT, and it is worth saying why, because the standing rule is the opposite: uploads,
    /// billing returns and email links must never use the request host, because they are built
    /// during a Web-to-API call that arrives on the loopback. This request came from the handset
    /// itself, over nginx, so <c>Request.Host</c> IS the public address — and it has to be, because
    /// the correct answer differs per tenant domain and no single configured value could serve
    /// them all.</para>
    /// </summary>
    private string? Absolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;

        var scheme = Request.Scheme;
        var host = Request.Host.Value;
        if (string.IsNullOrWhiteSpace(host)) return path;

        return $"{scheme}://{host}/{path.TrimStart('/')}";
    }

    /// <summary>
    /// A colour reaches an inline style attribute in the app's own chrome. Validated on write, but
    /// a row predating that check must not be able to close the attribute and open a tag.
    /// </summary>
    private static string? Safe(string? colour)
        => !string.IsNullOrWhiteSpace(colour) && System.Text.RegularExpressions.Regex.IsMatch(colour, "^#[0-9a-fA-F]{6}$")
            ? colour
            : null;
}
