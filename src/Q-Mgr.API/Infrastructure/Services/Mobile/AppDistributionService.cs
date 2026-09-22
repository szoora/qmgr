using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using QMgr.Application.DTOs;

namespace QMgr.Infrastructure.Services.Mobile;

/// <summary>
/// Reads the published-build manifest so the app can be kept current WITHOUT Google Play.
///
/// <para><b>This server does not store or serve the artefact</b>, and that is deliberate. Every app
/// in the estate is published once to a central distribution host — <c>apps.cashbook.ug/&lt;app&gt;</c>,
/// one folder per app — and every product reads the same manifest from it. Before that, each product
/// staged its own copy of the APK and its own copy of the manifest, and two copies can disagree:
/// the same app is then told two different things about what the latest build is, with nothing
/// erroring anywhere. The ERP learned this and moved; Q-Mgr starts where the ERP ended up.</para>
///
/// <para><b>Where to point it:</b> <c>Mobile:DistributionUrl</c>. A site with no internet points it
/// at a host on its own LAN serving the same static layout. There is no second mechanism and no
/// silent fallback, because a fallback that activates on its own is how "where does the app come
/// from?" acquires two answers.</para>
/// </summary>
public interface IAppDistributionService
{
    /// <summary>Is there a newer build than <paramref name="versionCode"/> for this platform?</summary>
    Task<AppUpdateResponse> CheckAsync(string? platform, long versionCode, CancellationToken ct = default);

    /// <summary>Every published build for a platform, newest first.</summary>
    Task<AppReleasesResponse> ReleasesAsync(string? platform, CancellationToken ct = default);

    /// <summary>The distribution root, without a trailing slash. Empty means none is configured.</summary>
    string DistributionUrl { get; }
}

public sealed class AppDistributionService : IAppDistributionService
{
    public const string HttpClientName = "app-distribution";

    /// <summary>
    /// Where Q-Mgr's builds are published. Overridable per deployment; never per tenant — one
    /// estate, one answer, and a per-tenant override is how two schools end up on different builds
    /// of the same app with nobody able to say why.
    /// </summary>
    public const string DefaultDistributionUrl = "https://apps.cashbook.ug/qmgr";

    // The manifest is a few hundred bytes and changes only on release, but a school full of
    // handsets opening at 08:00 would otherwise hit the distribution host together.
    private const string CacheKey = "mobile:releases";
    private const string LastGoodCacheKey = "mobile:releases:lastgood";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<AppDistributionService> _log;

    public AppDistributionService(IHttpClientFactory httpFactory, IMemoryCache cache,
                                 IConfiguration config, ILogger<AppDistributionService> log)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _config = config;
        _log = log;
    }

    public string DistributionUrl
    {
        get
        {
            var configured = _config["Mobile:DistributionUrl"];
            var url = string.IsNullOrWhiteSpace(configured) ? DefaultDistributionUrl : configured;
            return url.TrimEnd('/');
        }
    }

    // ── Check ────────────────────────────────────────────────────────────────

    public async Task<AppUpdateResponse> CheckAsync(string? platform, long versionCode, CancellationToken ct = default)
    {
        var key = NormalisePlatform(platform);
        var releases = await LoadAsync(ct);

        var forPlatform = releases
            .Where(r => string.Equals(r.Platform, key, StringComparison.OrdinalIgnoreCase))
            // Ordered by the INTEGER, never the version string: "1.10.0" sorts before "1.9.0" as
            // text, and that mistake ships an update every device then refuses.
            .OrderByDescending(r => r.VersionCode)
            .ToList();

        // A product that publishes no build is a legitimate state, not a fault.
        var newest = forPlatform.FirstOrDefault(r => r.Available);
        if (newest == null || newest.VersionCode <= versionCode)
        {
            return new AppUpdateResponse { UpdateAvailable = false, CanSelfInstall = CanSelfInstall(key) };
        }

        // MANDATORY IS STICKY ACROSS VERSIONS. If any release BETWEEN the device's build and the
        // newest is mandatory, this update is mandatory — otherwise a device three versions behind
        // skips a security fix by jumping straight to the newest optional build.
        var mandatory = forPlatform.Any(r => r.VersionCode > versionCode
                                             && r.VersionCode <= newest.VersionCode
                                             && r.Mandatory);

        return new AppUpdateResponse
        {
            UpdateAvailable = true,
            Version = newest.Version,
            VersionCode = newest.VersionCode,
            Mandatory = mandatory,
            Notes = newest.Notes,
            Highlights = newest.Highlights,
            SizeBytes = newest.SizeBytes,
            Sha256 = newest.Sha256,
            Url = newest.Url,
            CanSelfInstall = CanSelfInstall(key)
        };
    }

    public async Task<AppReleasesResponse> ReleasesAsync(string? platform, CancellationToken ct = default)
    {
        var key = NormalisePlatform(platform);
        var all = await LoadAsync(ct);

        var forPlatform = all
            .Where(r => string.Equals(r.Platform, key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.VersionCode)
            .ToList();

        return new AppReleasesResponse
        {
            // The newest INSTALLABLE build, not merely the newest entry — the manifest is history
            // and the disk is what can be installed.
            Latest = forPlatform.FirstOrDefault(r => r.Available),
            Releases = forPlatform
        };
    }

    /// <summary>
    /// Whether a device on this platform can install the update itself.
    ///
    /// <para>Android can, and does. Windows reports only: the build is unpackaged
    /// (<c>WindowsPackageType=None</c>), so there is nothing for the app to hand an installer.
    /// iOS reports only because Apple permits no self-distribution to customers at all — the App
    /// Store, TestFlight and Apple Business Manager Custom Apps are the only routes and all three
    /// go through review. Saying so lets the app offer the right words instead of a button that
    /// cannot work.</para>
    /// </summary>
    private static bool CanSelfInstall(string platform)
        => string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase);

    // ── Loading ──────────────────────────────────────────────────────────────

    private async Task<List<AppReleaseDto>> LoadAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out List<AppReleaseDto>? cached) && cached != null)
            return cached;

        var root = DistributionUrl;
        if (string.IsNullOrWhiteSpace(root)) return new List<AppReleaseDto>();

        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            var json = await http.GetStringAsync($"{root}/releases.json", ct);

            var manifest = JsonSerializer.Deserialize<ManifestShape>(json, JsonOpts);
            var releases = (manifest?.Releases ?? new List<ManifestEntry>())
                .Where(e => e is not null && e.VersionCode > 0)
                .Select(e => Map(e, root))
                .ToList();

            _cache.Set(CacheKey, releases, Ttl);
            // Kept far longer than the live entry so a distribution host that goes down does not
            // immediately make every handset believe it is up to date — which is the one wrong
            // answer here, because it is indistinguishable from the truth.
            _cache.Set(LastGoodCacheKey, releases, TimeSpan.FromDays(7));
            return releases;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the app release manifest from {Root}", root);

            if (_cache.TryGetValue(LastGoodCacheKey, out List<AppReleaseDto>? lastGood) && lastGood != null)
            {
                // Held briefly so a flapping host is not fetched on every single request.
                _cache.Set(CacheKey, lastGood, TimeSpan.FromMinutes(1));
                return lastGood;
            }

            return new List<AppReleaseDto>();
        }
    }

    private static AppReleaseDto Map(ManifestEntry e, string root)
    {
        var file = e.File ?? string.Empty;

        return new AppReleaseDto
        {
            Platform = string.IsNullOrWhiteSpace(e.Platform) ? "android" : e.Platform!.ToLowerInvariant(),
            Version = e.Version ?? string.Empty,
            VersionCode = e.VersionCode,
            Mandatory = e.Mandatory,
            Notes = e.Notes,
            Highlights = e.Highlights is null ? null : new AppReleaseHighlightsDto
            {
                Added = e.Highlights.Added ?? new List<string>(),
                Improved = e.Highlights.Improved ?? new List<string>(),
                Removed = e.Highlights.Removed ?? new List<string>()
            },
            SizeBytes = e.SizeBytes,
            Sha256 = e.Sha256,
            // Built from the distribution root and the manifest's own file name. Never from
            // anything a caller sent: a path assembled from a request is a traversal.
            Url = string.IsNullOrWhiteSpace(file) ? string.Empty : $"{root}/{file}",
            PublishedUtc = e.PublishedUtc,
            // The install script regenerates the manifest from the sidecars that survive pruning,
            // so an entry being present means its file is present — but the flag is honoured if the
            // host ever says otherwise, because rendering a button that 404s is worse than hiding it.
            Available = e.Available ?? true
        };
    }

    private static string NormalisePlatform(string? platform)
    {
        var p = (platform ?? string.Empty).Trim().ToLowerInvariant();
        return p switch
        {
            "android" or "ios" or "windows" => p,
            // An unknown platform is answered as Android rather than refused. The app only ever
            // sends one of three values; anything else is a stale build or a probe, and "no update"
            // is the safe reply either way.
            _ => "android"
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // The distribution host's own shape. Kept private and deliberately tolerant: this is somebody
    // else's file format, read over the network, and a new field appearing in it must not break
    // every handset's update check.
    private sealed class ManifestShape
    {
        public List<ManifestEntry>? Releases { get; set; }
    }

    private sealed class ManifestEntry
    {
        public string? Platform { get; set; }
        public string? Version { get; set; }
        public long VersionCode { get; set; }
        public string? File { get; set; }
        public bool Mandatory { get; set; }
        public string? Notes { get; set; }
        public ManifestHighlights? Highlights { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
        public DateTime? PublishedUtc { get; set; }
        public bool? Available { get; set; }
    }

    private sealed class ManifestHighlights
    {
        public List<string>? Added { get; set; }
        public List<string>? Improved { get; set; }
        public List<string>? Removed { get; set; }
    }
}
