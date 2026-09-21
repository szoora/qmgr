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

    /// <summary>What to call the product on this host.</summary>
    public string AppName => IsTenantHost && !string.IsNullOrWhiteSpace(Branding.BrandName) ? Branding.BrandName! : "Q-Mgr";

    /// <summary>
    /// Whether "Powered by SACC Software" comes off. Resolved server-side — this only reports it.
    /// The platform's own host always keeps it, whatever any tenant holds.
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
