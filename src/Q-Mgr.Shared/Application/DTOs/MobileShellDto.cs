namespace QMgr.Application.DTOs;

// ─────────────────────────────────────────────────────────────────────────────
//  The wire between Q-Mgr and its native mobile shell.
//
//  WHY THESE SHAPES LOOK UNLIKE THE REST OF THIS FOLDER. Every other DTO here is
//  returned bare; these are wrapped in { success, message, data }. That is not a
//  second opinion about response envelopes — it is the shell's published contract
//  (`docs/product-onboarding.md` in the mobile repo), and the shell is one binary
//  whose parsing is already written. A field renamed on this side is not a
//  difference of style; it is an app that does not work, and it cannot be fixed by
//  deploying the server again.
//
//  THE ONE DELIBERATE DEPARTURE, recorded here so nobody "fixes" it later:
//  `auth/login`, `auth/refresh` and `auth/logout` ALREADY EXIST in Q-Mgr and are
//  what the Blazor web signs in with. Bolting a second response shape onto those
//  three routes would give one endpoint two answers depending on who asked, which
//  is worse than either shape. So those three keep Q-Mgr's own bare
//  `LoginResponse` and merely GAIN optional device fields, and the app's
//  `AuthService` reads Q-Mgr's shape. Everything genuinely new below — tenant
//  info, the handoff, devices, app distribution, check-in — uses the contract's
//  wrapped shape exactly, because nothing collided there.
//
//  See docs/plans/MOBILE_APP_INTEGRATION.md §3.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The shell's envelope. Non-generic form, for endpoints that carry only an outcome.
/// </summary>
public record MobileEnvelope
{
    public bool Success { get; init; }
    public string? Message { get; init; }

    public static MobileEnvelope Ok(string? message = null) => new() { Success = true, Message = message };
    public static MobileEnvelope Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>The shell's envelope with a payload.</summary>
public record MobileEnvelope<T> : MobileEnvelope
{
    public T? Data { get; init; }

    public static MobileEnvelope<T> Ok(T data, string? message = null)
        => new() { Success = true, Message = message, Data = data };

    public static new MobileEnvelope<T> Fail(string message)
        => new() { Success = false, Message = message };
}

// ── 1. tenant/info ───────────────────────────────────────────────────────────

/// <summary>
/// What the app learns about a host BEFORE a password is typed.
///
/// <para><b>Deliberately flat, not wrapped</b> — it is the first call made against an
/// unknown address, and the contract fixes it that way.</para>
///
/// <para><b>Q-Mgr answers this two ways</b> and the difference matters. On a tenant's own
/// verified domain it names the school. On the shared platform host every school shares
/// one address and there is no tenant to name, so it answers with the PLATFORM's identity
/// and <see cref="Tenant"/> is null — never an invented school. A 503 is reserved for a
/// host that is not a Q-Mgr install at all; the shared host is a legitimate workspace for
/// every tenant that has not bought a domain, which is most of them.</para>
/// </summary>
public record TenantInfoResponse
{
    public bool Success { get; init; } = true;

    /// <summary>Shown before sign-in so the user confirms they have reached the right place.</summary>
    public string CompanyName { get; init; } = string.Empty;

    /// <summary>The product, displayed by the app. The app never guesses this.</summary>
    public string Product { get; init; } = "Q-Mgr";

    /// <summary>A one-line descriptor under the product name.</summary>
    public string? Descriptor { get; init; }

    /// <summary>The tenant slug, or null on the shared platform host.</summary>
    public string? Tenant { get; init; }

    /// <summary>
    /// The organisation this host belongs to, or null on the shared host. The app carries it
    /// so a workspace is keyed per host AND school — otherwise two schools on the shared
    /// address collapse into one entry and one stored session slot.
    /// </summary>
    public Guid? OrganizationId { get; init; }

    public string Status { get; init; } = "Active";

    /// <summary>Absolute or root-relative. Absent means the app shows the name as text.</summary>
    public string? LogoUrl { get; init; }

    /// <summary>Advertised only when the artwork actually responds — see BrandingController.</summary>
    public string? ProductLogoUrl { get; init; }

    public TenantBrandDto? Brand { get; init; }

    /// <summary>
    /// True when this tenant has paid to have our name taken off. The app honours it in its
    /// own chrome, or a school that bought attribution removal still reads "Q-Mgr" on the
    /// surface they look at most.
    /// </summary>
    public bool AttributionRemoved { get; init; }

    public TenantApiCapabilitiesDto Api { get; init; } = new();
}

public record TenantBrandDto
{
    public string? Primary { get; init; }
    public string? Secondary { get; init; }
    public string? Accent { get; init; }
}

/// <summary>
/// Lets the app detect an older deployment without probing each route for a 404.
/// </summary>
public record TenantApiCapabilitiesDto
{
    public string Version { get; init; } = "v1";
    public bool Refresh { get; init; } = true;
    public bool WebSession { get; init; } = true;
    public bool DeviceSessions { get; init; } = true;
    public bool Push { get; init; } = true;
}

// ── 3-4. device sessions ─────────────────────────────────────────────────────

/// <summary>A device holding a live session, for a "your devices" screen and a lost phone.</summary>
public record DeviceSessionDto
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string? Platform { get; init; }
    public DateTime IssuedUtc { get; init; }
    public DateTime LastUsedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }

    /// <summary>True for the device that asked. Lets the UI say "this device".</summary>
    public bool IsCurrent { get; init; }

    /// <summary>Whether this device can be reached by a push notification right now.</summary>
    public bool PushEnabled { get; init; }
}

/// <summary>
/// `{ deviceId }` or `{ allDevices: true }`. Neither supplied is a 400 — a "signed out"
/// that revoked nothing is the worst available outcome, so it is never reported as success.
/// </summary>
public record MobileLogoutRequest
{
    public string? DeviceId { get; init; }
    public bool AllDevices { get; init; }

    /// <summary>
    /// Fallback: the device is taken from the token's middle segment. Safe because the USER
    /// comes from the bearer token, never from this body, so a caller cannot revoke somebody
    /// else's device by sending their token.
    /// </summary>
    public string? RefreshToken { get; init; }
}

// ── 5-6. the web handoff ─────────────────────────────────────────────────────

/// <summary>
/// A one-shot code that carries a signed-in identity from the app into its own WebView.
/// Two steps because a WebView navigation is a GET, and putting an access token in a query
/// string writes a live credential into proxy logs, history and referrers.
/// </summary>
public record HandoffCodeDto
{
    public string Code { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
}

// ── 7-8. app distribution ────────────────────────────────────────────────────

/// <summary>
/// One published build. <see cref="Available"/> is not decoration: the manifest is history
/// and the disk is what can actually be installed, so an artefact that has been pruned is
/// reported as unavailable rather than offered as a link that 404s.
/// </summary>
public record AppReleaseDto
{
    public string Platform { get; init; } = "android";
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// The integer the app compares. NEVER compare version STRINGS: "1.10.0" sorts before
    /// "1.9.0" as text, and that mistake ships an update every device then refuses.
    /// </summary>
    public long VersionCode { get; init; }

    public bool Mandatory { get; init; }
    public string? Notes { get; init; }
    public AppReleaseHighlightsDto? Highlights { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string Url { get; init; } = string.Empty;
    public DateTime? PublishedUtc { get; init; }
    public bool Available { get; init; } = true;
}

public record AppReleaseHighlightsDto
{
    public List<string> Added { get; init; } = new();
    public List<string> Improved { get; init; } = new();
    public List<string> Removed { get; init; } = new();
}

/// <summary>
/// The answer to "is there a newer build than mine".
///
/// <para><b><see cref="Mandatory"/> is sticky across versions.</b> If ANY release between the
/// device's build and the newest is mandatory, this is mandatory — otherwise a device three
/// versions behind skips a security fix by jumping straight to the newest optional build.</para>
/// </summary>
public record AppUpdateResponse
{
    public bool UpdateAvailable { get; init; }
    public string? Version { get; init; }
    public long VersionCode { get; init; }
    public bool Mandatory { get; init; }
    public string? Notes { get; init; }
    public AppReleaseHighlightsDto? Highlights { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string? Url { get; init; }

    /// <summary>
    /// False on iOS and Windows: the update is reported but the app cannot install it there.
    /// Apple permits no self-distribution at all, and the Windows build is unpackaged.
    /// Saying so lets the app offer the right words instead of a button that cannot work.
    /// </summary>
    public bool CanSelfInstall { get; init; }
}

/// <summary>Every published build, newest first, plus the newest on its own.</summary>
public record AppReleasesResponse
{
    public AppReleaseDto? Latest { get; init; }
    public List<AppReleaseDto> Releases { get; init; } = new();
}

// ── 10. device check-in ──────────────────────────────────────────────────────

/// <summary>
/// What the app tells the server about itself, on sign-in and periodically after.
/// The push token arrives here; it is absent on a first run and on Windows.
/// </summary>
public record DeviceCheckInRequest
{
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public string? Platform { get; init; }
    public string? AppVersion { get; init; }
    public long AppVersionCode { get; init; }

    /// <summary>The FCM registration token, when the app has one.</summary>
    public string? PushToken { get; init; }

    /// <summary>
    /// What the OS permission prompt actually returned. BOTH halves matter: a device can
    /// hold a perfectly good token and still display nothing because the user declined,
    /// and a fleet that cannot tell those apart cannot explain a silent handset.
    /// </summary>
    public bool PushPermitted { get; init; }
}

public record DeviceCheckInResponse
{
    /// <summary>Seconds until the app should check in again. Server-controlled on purpose.</summary>
    public int NextCheckInSeconds { get; init; } = 21600;

    /// <summary>Unread notifications for this user, so the bell is right before the list loads.</summary>
    public int UnreadCount { get; init; }

    /// <summary>True when the server has this device's push token and will use it.</summary>
    public bool PushRegistered { get; init; }
}
