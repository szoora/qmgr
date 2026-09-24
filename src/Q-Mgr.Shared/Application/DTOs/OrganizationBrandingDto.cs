namespace QMgr.Application.DTOs;

/// <summary>
/// Safe-to-expose-publicly subset of an organization's branding. Deliberately
/// excludes everything else on Organization (contact info, billing, slug,
/// etc.) — this is served anonymously to public kiosk/display screens.
///
/// Single source of truth for this shape: lives in Q-Mgr.Shared (like TokenDto/
/// CounterDto) specifically so both Q-Mgr.API and Q-Mgr.Web reference the same
/// type instead of each maintaining an independent copy that can silently drift
/// out of sync (e.g. a new field added to one and forgotten on the other).
/// Properties use `set` rather than `init` because BrandingSettings.razor
/// two-way binds directly against an instance of this record as its form model.
/// </summary>
public record OrganizationBrandingDto
{
    public bool WhitelabelEnabled { get; set; }

    /// <summary>
    /// The school's own name for its APP, exactly as typed — the raw value, for the editor. Not the
    /// organisation's name (that is <see cref="OrganizationName"/>) and not necessarily what is shown
    /// (that is <see cref="ProductName"/>, which falls back to ours unless white-labelling is on).
    /// </summary>
    public string? BrandName { get; set; }

    /// <summary>The organisation itself — Settings → General. What a printed header names.</summary>
    public string? OrganizationName { get; set; }

    /// <summary>
    /// The app's name as it is SHOWN, resolved server-side by <c>ProductBrand.NameFor</c>: the school's
    /// Brand Name while white-labelling is entitled and on, otherwise "SACC Dashboard".
    /// </summary>
    public string? ProductName { get; set; }

    /// <summary>The same, short enough for a home-screen label (<c>ProductBrand.ShortNameFor</c>).</summary>
    public string? ProductShortName { get; set; }

    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }
    public string? AccentColor { get; set; }

    /// <summary>
    /// "dark" or "light" — not gated by WhitelabelEnabled, unlike everything else
    /// on this DTO. Always reflects the organization's real setting.
    /// </summary>
    public string DisplayTheme { get; set; } = "dark";

    /// <summary>
    /// Whether the org's plan actually includes the white-label feature (from
    /// IFeatureFlagService), independent of WhitelabelEnabled (the org's own
    /// on/off toggle). Lets the UI disable the logo/color editor up front
    /// instead of only discovering non-entitlement when Save gets a 403.
    /// Not set on the anonymous public-display endpoint — irrelevant there.
    /// </summary>
    public bool WhiteLabelEntitled { get; set; } = true;

    /// <summary>
    /// Whether this tenant's copyright lines (<c>QCopyright</c>) credit the TENANT rather than us on
    /// its screens. Resolved SERVER-side from <c>FeatureCodes.RemoveAttribution</c> — an add-on, or a
    /// platform override on the organization — and never inferred by the client.
    ///
    /// DEFAULTS TO FALSE, unlike <see cref="WhiteLabelEntitled"/> beside it, and that asymmetry is
    /// deliberate: a DTO fails to load as easily as it loads, and a generous default for branding
    /// is merely generous while a generous default here would REMOVE the attribution on a dropped
    /// request. Attribution is on until somebody has paid for it to be off.
    /// </summary>
    public bool AttributionRemoved { get; set; }

    /// <summary>
    /// The tenant's own live domain, read-only on the Branding page. Tenants do not self-serve a
    /// domain — the certificate step touches the host — so this is shown with a line telling them
    /// who to ask. Null when there is none.
    /// </summary>
    public string? CustomDomain { get; set; }
}

/// <summary>
/// Write model for the authenticated branding-settings admin page. Colors are
/// validated as hex strings server-side (not just by the client) since they end
/// up injected into an inline `style` attribute on public kiosk/display pages —
/// see DisplayLayout.razor/KioskLayout.razor's own HexColor regex check on read.
/// </summary>
public record UpdateOrganizationBrandingRequest
{
    public bool WhitelabelEnabled { get; init; }
    public string? BrandName { get; init; }
    public string? LogoUrl { get; init; }
    public string? FaviconUrl { get; init; }
    public string? PrimaryColor { get; init; }
    public string? SecondaryColor { get; init; }
    public string? AccentColor { get; init; }
}

/// <summary>
/// Write model for the public-display theme setting. Deliberately separate from
/// UpdateOrganizationBrandingRequest since that endpoint is gated behind the
/// paid-tier whitelabel feature — dark/light display theme is a basic setting
/// available to every organization regardless of plan.
/// </summary>
public record UpdateDisplayThemeRequest
{
    public string DisplayTheme { get; init; } = "dark";
}
