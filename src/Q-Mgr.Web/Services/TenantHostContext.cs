using QMgr.Application.Branding;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Which host this browser arrived on, and what that host should look like.
///
/// ONE HOME, resolved once in <c>App.razor</c> and cascaded. App.razor is server-rendered on every
/// request whatever the render mode — which is why it already stamps <c>data-theme</c> — and it is
/// the only place in the app that can see <c>Request.Host</c> at all. A page reading the host any
/// other way would be a second copy of the rule, and on a Blazor Server circuit there is no
/// request to read it from after the first render anyway.
///
/// <see cref="IsTenantHost"/> is false for the platform's own host AND for any host that is not a
/// LIVE tenant domain. An unverified or half-configured host must be indistinguishable from the
/// platform host: a mistyped DNS record that half-branded a sign-in page would be worse than one
/// that does nothing.
/// </summary>
public sealed record TenantHostContext(string Host, TenantHostBrandingDto Branding)
{
    /// <summary>Nothing branded, nothing hidden. The fallback for every failure, by design.</summary>
    public static readonly TenantHostContext Platform = new(string.Empty, new TenantHostBrandingDto { Resolved = false });

    public bool IsTenantHost => Branding.Resolved;

    /// <summary>
    /// The origin the BROWSER should call the API on, for this host — or null when this is not a
    /// tenant host, so the configured <c>ApiPublicUrl</c> still wins there and in development.
    ///
    /// <para>In production nginx serves both halves from one name: "/" to Web, "/api/" and "/hubs/"
    /// to the API. On a tenant's own domain that origin is therefore the TENANT'S name, not the
    /// platform's — and the baked <c>ApiPublicUrl</c> is the platform's. The difference is invisible
    /// until a school is actually on their own domain, and then it is two things at once: an upload
    /// posts cross-origin to a host the API's CORS does not allow, and the notification hub connects
    /// to the wrong origin and sits on "Reconnecting…" — the same shape as the 2026-09-09 hub bug,
    /// where a config value that was coincidentally right on one host was wrong on another.</para>
    /// </summary>
    public string? ApiOrigin => IsTenantHost && !string.IsNullOrWhiteSpace(Host) ? $"https://{Host}" : null;

    /// <summary>
    /// What to call the app on this host: the school's own name exactly as typed on a white-labelled
    /// tenant domain, otherwise ours. Resolved by the API (<c>ProductBrand.NameFor</c>); this only reads it.
    /// </summary>
    public string AppName => IsTenantHost && !string.IsNullOrWhiteSpace(Branding.ProductName) ? Branding.ProductName! : ProductBrand.Name;

    /// <summary>The same, short enough for a phone's home-screen label.</summary>
    public string AppShortName => IsTenantHost && !string.IsNullOrWhiteSpace(Branding.ProductShortName)
        ? Branding.ProductShortName!
        : ProductBrand.ShortNameFor(ProductBrand.Name);

    /// <summary>The organisation on this host, or null on the platform host.</summary>
    public string? OrganizationName => IsTenantHost ? Branding.OrganizationName : null;

    /// <summary>
    /// Whether the copyright line credits the school rather than us. Resolved server-side — this only
    /// reports it. The platform's own host always credits us, whatever any tenant holds.
    /// </summary>
    public bool AttributionRemoved => IsTenantHost && Branding.AttributionRemoved;

    /// <summary>
    /// The tenant's palette as <c>--qm-*</c> overrides for a wrapper's style attribute, or empty.
    /// Through <see cref="BrandPalette"/>, which derives the WHOLE family — the hover, the tint,
    /// the rgba triple — rather than the three colours a tenant typed. Overriding only those three
    /// is what made the first cut of this look broken.
    /// </summary>
    public string BrandingStyle => IsTenantHost
        ? BrandPalette.StyleFor(Branding.PrimaryColor, Branding.SecondaryColor, Branding.AccentColor)
        : string.Empty;
}
