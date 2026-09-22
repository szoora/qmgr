using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace QMgr.API.Controllers.v1.MobileShell;

/// <summary>
/// Q-Mgr's own mark, drawn to order in light or dark ink.
///
/// <para><b>What this is NOT for, and the reason is a hard platform limit.</b> The mobile shell's
/// contract has the app fetch a product logo from the server so it can wear the identity the user
/// already knows from the web. That works when the artwork is a PNG. Q-Mgr's mark is an SVG — eight
/// SVG primitives, a queue loop resolving into a forward arrow — and <b>MAUI cannot decode SVG at
/// run time on any platform</b>: the build converts SVG resources to PNG ahead of time, and a URL
/// handed to an <c>Image</c> goes to the platform's bitmap decoder, which does not know SVG. Serving
/// this to the app would render a broken image that looks exactly like a failed download.</para>
///
/// <para>So <c>tenant/info</c> deliberately does NOT advertise it, and the app uses the mark
/// compiled into it — which is Q-Mgr's own, because this is Q-Mgr's app rather than one shell serving
/// many products. A TENANT's logo is a different matter and is still sent: those are uploaded
/// raster files (<c>ImageProbe</c> refuses SVG outright, on OWASP's reasoning that SVG carries
/// script in almost every context), so the app can display them.</para>
///
/// <para><b>What it IS for:</b> browsers. The download page, the sign-in page and anything else
/// rendering HTML can use it, and a browser renders SVG natively. It is also the one place the
/// mark's geometry is defined for server-rendered surfaces, so it cannot drift from
/// <c>wwwroot/images/icon-512.svg</c> by being redrawn by hand somewhere else.</para>
/// </summary>
[ApiController]
[Route("api/v1/branding")]
[AllowAnonymous]
public class ProductLogoController : ControllerBase
{
    /// <summary>
    /// The brand wine, light theme. Matches <c>--qm-primary</c> in <c>qm-theme.css</c>, which is the
    /// single source of truth for every <c>--qm-*</c> token — the standing colour decision of
    /// 2026-08-19, and not to be reverted to blue.
    /// </summary>
    private const string Wine = "#7a2847";

    /// <summary>
    /// <c>GET /api/v1/branding/product-logo</c> — the mark in LIGHT ink, for a dark ground.
    /// <c>?ink=dark</c> gives the same geometry in wine, for a light ground.
    ///
    /// <para><b>The ink parameter is not decoration.</b> The app's sign-in ground is dark; serving a
    /// dark-ink mark there renders wine on near-black, which is an invisible logo that reads as a
    /// loading failure rather than as a colour mistake. The ERP found that by measuring the mean ink
    /// of the served file, not by looking at it.</para>
    /// </summary>
    [HttpGet("product-logo")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public IActionResult ProductLogo([FromQuery] string? ink = null)
    {
        var dark = string.Equals(ink, "dark", StringComparison.OrdinalIgnoreCase);
        var stroke = dark ? Wine : "#ffffff";

        // The same geometry as wwwroot/images/icon-512.svg, with no plate: a stroked circle (the
        // queue — people cycling through service) and a rotated arrow (next). Emitted rather than
        // read off disk so this route cannot 404 because a file moved, and so the ink is a parameter
        // rather than two files that can fall out of step.
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="Q-Mgr">
              <circle cx="256" cy="256" r="120" fill="none" stroke="{stroke}" stroke-width="40"/>
              <g transform="rotate(45 256 256)">
                <rect x="376" y="236" width="50" height="40" rx="5" fill="{stroke}"/>
                <polygon points="426,220 426,292 470,256" fill="{stroke}"/>
              </g>
            </svg>
            """;

        // image/svg+xml, and the bytes are ours rather than a caller's, so there is nothing here for
        // the SVG-carries-script problem to act on: `ink` never reaches the document — it only
        // chooses between two constants above.
        return Content(svg, "image/svg+xml");
    }
}
