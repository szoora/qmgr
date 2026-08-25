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
    public string? BrandName { get; set; }
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
