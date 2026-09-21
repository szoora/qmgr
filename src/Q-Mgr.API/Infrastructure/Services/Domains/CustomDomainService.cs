using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.API.Application.Services;
using QMgr.Domain.Entities.Platform;
using QMgr.Infrastructure.Data;
using QMgr.Middleware;

namespace QMgr.Infrastructure.Services.Domains;

/// <inheritdoc cref="ICustomDomainService"/>
public sealed class CustomDomainService : ICustomDomainService
{
    /// <summary>The name a tenant publishes the token at. A prefix, so it never collides with the site's own TXT records (SPF, DMARC, other vendors' verifications).</summary>
    public const string VerificationPrefix = "_qmgr-verify";

    /// <summary>
    /// Failed attempts before the unattended sweep stops trying. Seven, because the sweep runs
    /// daily: a week is long enough for a registrar change to propagate and short enough that a
    /// domain somebody typed wrong is not retried until the end of time.
    /// </summary>
    public const int MaxAttempts = 7;

    /// <summary>The sweep will not re-try a domain within this window, however often it runs.</summary>
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromHours(1);

    private readonly QMgrDbContext _db;
    private readonly IDnsTxtLookup _dns;
    private readonly ITenantDomainActivator _routing;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CustomDomainService> _logger;

    public CustomDomainService(
        QMgrDbContext db,
        IDnsTxtLookup dns,
        ITenantDomainActivator routing,
        IPlatformSettingsService platformSettings,
        IConfiguration configuration,
        ILogger<CustomDomainService> logger)
    {
        _db = db;
        _dns = dns;
        _routing = routing;
        _platformSettings = platformSettings;
        _configuration = configuration;
        _logger = logger;
    }

    // =====================================================================================
    // Normalisation and refusals
    // =====================================================================================

    /// <summary>
    /// A host, or null when the text is not one. Strips a scheme, a port, a path and a trailing
    /// dot, lower-cases, and accepts only letters, digits, hyphens and dots — a host that reached
    /// the database with anything else in it would end up inside a generated nginx server block.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var value = input.Trim().ToLowerInvariant();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) value = value[(scheme + 3)..];

        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];

        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[..colon];

        value = value.Trim().TrimEnd('.');
        if (value.Length == 0 || value.Length > 253) return null;

        foreach (var label in value.Split('.'))
        {
            if (label.Length is 0 or > 63) return null;
            if (label.StartsWith('-') || label.EndsWith('-')) return null;
            foreach (var c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return null;
        }

        return value;
    }

    /// <summary>
    /// Why this host cannot be claimed, or null. Two of the three refusals are about correctness
    /// rather than policy, so they are stated plainly to the person typing.
    /// </summary>
    private async Task<string?> RefusalAsync(Guid organizationId, string host, CancellationToken ct)
    {
        // An APEX cannot be a tenant domain, and that is DNS, not a preference: the standard
        // forbids a CNAME at the root of a zone, so "maryhillug.net" could only be pointed here
        // with a provider-specific ALIAS/ANAME record that many registrars do not offer. Requiring
        // a subdomain is what every platform in this category does, and it is what was asked for:
        // "dashboard.maryhillug.net".
        if (host.Count(c => c == '.') < 2)
            return "Use a subdomain, for example dashboard.yourschool.com. A domain's root cannot be pointed at us — DNS does not allow a CNAME there.";

        var saas = await _platformSettings.GetSettingsAsync<SaasSettings>("SaaS");
        var baseDomain = Normalize(saas?.BaseDomain ?? _configuration["SaaS:BaseDomain"]);
        if (baseDomain != null && (host == baseDomain || host.EndsWith("." + baseDomain, StringComparison.Ordinal)))
            return $"That host is part of the platform's own domain ({baseDomain}). A tenant on there already has a subdomain and needs nothing set up.";

        // Two tenants answering on one host is a cross-tenant leak, not a clash. The unique index
        // on CustomDomain catches the live half; the pending half has no index, so it is checked
        // here — a second tenant should be told now, not after publishing a TXT record.
        var claimed = await _db.Organizations.IgnoreQueryFilters()
            .AnyAsync(o => o.Id != organizationId &&
                           ((o.CustomDomain != null && o.CustomDomain == host) ||
                            (o.CustomDomainPending != null && o.CustomDomainPending == host)), ct);
        if (claimed)
            return "That domain is already claimed by another organisation on this platform.";

        return null;
    }

    // =====================================================================================
    // The three actions
    // =====================================================================================

    public async Task<CustomDomainStatusDto> GetStatusAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var org = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        return org == null ? new CustomDomainStatusDto() : StatusOf(org);
    }

    public async Task<CustomDomainResult> RequestAsync(Guid organizationId, string domain, CancellationToken cancellationToken = default)
    {
        var org = await _db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (org == null) return Fail(new CustomDomainStatusDto(), "Organisation not found.");

        var host = Normalize(domain);
        if (host == null) return Fail(StatusOf(org), "That is not a valid domain name.");

        var refusal = await RefusalAsync(organizationId, host, cancellationToken);
        if (refusal != null) return Fail(StatusOf(org), refusal);

        // A NEW claim always starts a NEW token. Reusing the old one would let a domain released
        // by one tenant be verified by the next with a record the first tenant published.
        org.CustomDomainPending = host;
        org.CustomDomainVerificationToken = NewToken();
        org.CustomDomainVerifiedAt = null;
        org.CustomDomainCertificateAt = null;
        org.CustomDomainAttempts = 0;
        org.CustomDomainLastAttemptAt = null;
        org.CustomDomainLastError = null;

        // The live domain is NOT cleared here: a tenant moving from one host to another keeps the
        // old one serving until the new one is actually ready. Going live is what swaps them.

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Organization {OrganizationId} claimed the domain {Domain}", organizationId, host);
        return new CustomDomainResult(true, null, StatusOf(org));
    }

    public async Task<CustomDomainResult> VerifyAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var org = await _db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (org == null) return Fail(new CustomDomainStatusDto(), "Organisation not found.");

        var host = org.CustomDomainPending;
        if (string.IsNullOrEmpty(host))
            return Fail(StatusOf(org), "There is no domain waiting to be verified.");

        org.CustomDomainLastAttemptAt = DateTime.UtcNow;

        // STEP 1 — ownership. Skipped once already proved: a verified domain whose certificate
        // failed must not be sent back to square one, and asking a tenant to keep a TXT record
        // forever is not something anybody else in this category does.
        if (org.CustomDomainVerifiedAt == null)
        {
            var expected = org.CustomDomainVerificationToken;
            if (string.IsNullOrEmpty(expected))
                return await SaveFailureAsync(org, "This claim has no verification token. Request the domain again.", cancellationToken);

            IReadOnlyList<string> records;
            try
            {
                records = await _dns.GetTxtRecordsAsync($"{VerificationPrefix}.{host}", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TXT lookup threw while verifying {Domain}", host);
                return await SaveFailureAsync(org, "We could not read DNS for that domain just now. Try again in a few minutes.", cancellationToken);
            }

            if (!records.Any(r => FixedTimeEquals(r, expected)))
            {
                return await SaveFailureAsync(org,
                    records.Count == 0
                        ? $"We could not find the TXT record at {VerificationPrefix}.{host} yet. DNS changes can take up to an hour to appear."
                        : $"A TXT record exists at {VerificationPrefix}.{host} but its value does not match. Check it was copied whole.",
                    cancellationToken);
            }

            org.CustomDomainVerifiedAt = DateTime.UtcNow;
            org.CustomDomainLastError = null;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Domain {Domain} verified for organization {OrganizationId}", host, organizationId);
        }

        // STEP 2 — DOES THE NAME ACTUALLY POINT HERE? Proving ownership and pointing the name at
        // this server are two different records at the registrar, and until 2026-09-21 the panel
        // asked for only the first — so a domain could be "Verified" while still resolving to the
        // tenant's old web host.
        //
        // It is checked BEFORE the certificate step rather than warned about afterwards, and that
        // is about the ACME budget, not tidiness: for a domain the shared certificate cannot cover,
        // http-01 fetches a file over port 80 AT THIS HOSTNAME, so a domain pointing elsewhere is a
        // guaranteed failed validation, and Let's Encrypt allows five of those per hostname per
        // hour for the WHOLE BOX. Spending one to discover what a lookup already knew would take
        // the budget from every other tenant.
        //
        // A failure here is the same soft shape as a missing TXT record: it names the step, counts
        // an attempt, and the human retries once DNS is right.
        var (pointsHere, routingHint) = await CheckRoutingAsync(host, cancellationToken);
        if (pointsHere == false)
            return await SaveFailureAsync(org, routingHint!, cancellationToken);

        // STEP 3 — serve it. A domain the shared certificate covers is served off that certificate
        // with nothing issued; anything else is issued one of its own. The helper decides, and it
        // REFUSES rather than take a tenant live behind a name mismatch. Unverified never reaches
        // this — an unproved claim must not be served, whatever certificate would answer for it.
        var routed = await _routing.ActivateAsync(host, cancellationToken);
        if (!routed.Ok)
            return await SaveFailureAsync(org, routed.Error ?? "The domain could not be brought live.", cancellationToken);

        var previous = org.CustomDomain;

        org.CustomDomainCertificateAt = routed.ActivatedAt ?? DateTime.UtcNow;
        org.CustomDomain = host;
        org.CustomDomainPending = null;
        org.CustomDomainAttempts = 0;
        org.CustomDomainLastError = null;
        await _db.SaveChangesAsync(cancellationToken);

        // The resolver caches a host for 30 minutes, including the NEGATIVE answer it just gave
        // every request that arrived while this was being set up. Without both evictions the new
        // domain 'does not work' for half an hour and the old one keeps resolving.
        TenantResolutionMiddleware.ForgetCustomDomain(host);
        TenantResolutionMiddleware.ForgetCustomDomain(previous);

        if (!string.IsNullOrEmpty(previous) && previous != host)
            await _routing.DeactivateAsync(previous, cancellationToken);

        _logger.LogInformation("Domain {Domain} is live for organization {OrganizationId}", host, organizationId);
        return new CustomDomainResult(true, null, StatusOf(org));
    }

    public async Task<CustomDomainResult> ReleaseAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var org = await _db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (org == null) return Fail(new CustomDomainStatusDto(), "Organisation not found.");

        var live = org.CustomDomain;
        var pending = org.CustomDomainPending;

        org.CustomDomain = null;
        org.CustomDomainPending = null;
        org.CustomDomainVerificationToken = null;
        org.CustomDomainVerifiedAt = null;
        org.CustomDomainCertificateAt = null;
        org.CustomDomainAttempts = 0;
        org.CustomDomainLastAttemptAt = null;
        org.CustomDomainLastError = null;
        await _db.SaveChangesAsync(cancellationToken);

        TenantResolutionMiddleware.ForgetCustomDomain(live);
        TenantResolutionMiddleware.ForgetCustomDomain(pending);

        if (!string.IsNullOrEmpty(live))
            await _routing.DeactivateAsync(live, cancellationToken);

        _logger.LogInformation("Organization {OrganizationId} released the domain {Domain}", organizationId, live ?? pending);
        return new CustomDomainResult(true, null, StatusOf(org));
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - MinSweepInterval;

        var due = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.CustomDomainPending != null
                        && o.CustomDomainAttempts < MaxAttempts
                        && (o.CustomDomainLastAttemptAt == null || o.CustomDomainLastAttemptAt < cutoff))
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        var completed = 0;
        foreach (var id in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await VerifyAsync(id, cancellationToken);
            if (result.Ok) completed++;
        }

        if (due.Count > 0)
            _logger.LogInformation("Custom-domain sweep: {Checked} checked, {Completed} went live", due.Count, completed);

        return completed;
    }

    // =====================================================================================

    private async Task<CustomDomainResult> SaveFailureAsync(QMgr.Domain.Entities.Organization.Organization org, string error, CancellationToken ct)
    {
        org.CustomDomainAttempts += 1;
        org.CustomDomainLastError = error;
        await _db.SaveChangesAsync(ct);
        return Fail(StatusOf(org), error);
    }

    private static CustomDomainResult Fail(CustomDomainStatusDto status, string error) => new(false, error, status with { LastError = error });

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Does <paramref name="host"/> resolve to the same address this server answers on?
    ///
    /// NULL MEANS "COULD NOT TELL" AND IS NOT A FAILURE — no answer, a resolver that timed out, or
    /// a platform host that does not itself resolve. Only a confident mismatch returns false, and
    /// the caller treats null as "carry on": a tenant whose DNS is right must never be blocked by
    /// our own lookup being unable to run.
    ///
    /// .NET resolves A records perfectly well; it is only TXT it has no API for, which is the
    /// entire reason IDnsTxtLookup is hand-written. So this needs no new parsing and no package.
    /// A CNAME needs no special handling either: a resolver follows it, so the address that comes
    /// back for a correctly-pointed tenant domain is this server's.
    /// </summary>
    private async Task<(bool? PointsHere, string? Hint)> CheckRoutingAsync(string host, CancellationToken ct)
    {
        // The stub exists so the verification flow can be exercised with no zone to publish in.
        // Where TXT is answered from memory, a real A lookup for the same invented hostname would
        // fail and contradict it.
        if (_configuration.GetValue("Dns:Stub", false)) return (null, null);

        var platformHost = PlatformHost();
        if (string.IsNullOrEmpty(platformHost)) return (null, null);

        try
        {
            var theirs = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            var ours = await System.Net.Dns.GetHostAddressesAsync(platformHost, ct);
            if (theirs.Length == 0 || ours.Length == 0) return (null, null);

            var oursSet = ours.Select(a => a.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (theirs.Any(a => oursSet.Contains(a.ToString()))) return (true, null);

            return (false,
                $"{host} does not point at this server yet — it currently resolves to {theirs[0]}. " +
                $"Create a CNAME from {host} to {platformHost}, then check again. DNS changes can take up to an hour to appear.");
        }
        catch (Exception ex)
        {
            // A name that does not resolve at all is the ordinary "they have not made the record
            // yet" case, and it is worth saying so plainly rather than going quiet.
            _logger.LogDebug(ex, "Routing lookup could not be completed for {Domain}", host);
            return (null, null);
        }
    }

    /// <summary>
    /// The host a tenant points their own domain AT. It is whatever this install actually answers
    /// on — the same resolution every link a person follows uses, never the request host of an
    /// internal call, which in production is the loopback.
    /// </summary>
    private string PlatformHost()
    {
        var configured = PublicWebBase.FromDeployment(_configuration)
                         ?? PublicWebBase.Clean(_configuration["SaaS:BaseUrl"]);
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    private CustomDomainStatusDto StatusOf(QMgr.Domain.Entities.Organization.Organization org)
    {
        // A CLAIM IN PROGRESS WINS OVER THE LIVE DOMAIN, and that is not cosmetic. RequestAsync
        // deliberately leaves the old domain serving while a new one is being set up, so a tenant
        // MOVING from one host to another has both columns set at once. Reading the live column
        // first made the whole panel say "Live" and withhold the TXT record for the new claim —
        // so the move could never be completed. Found by the e2e (19.1f/g/h) on a second run,
        // which is the only way to reach this state at all.
        var state = org.CustomDomainPending != null
            ? (org.CustomDomainVerifiedAt != null ? CustomDomainState.Verified : CustomDomainState.Pending)
            : org.CustomDomain != null ? CustomDomainState.Live
            : CustomDomainState.None;

        var showRecord = state is CustomDomainState.Pending or CustomDomainState.Verified;

        return new CustomDomainStatusDto
        {
            State = state,
            Domain = org.CustomDomain,
            PendingDomain = org.CustomDomainPending,
            VerificationRecordName = showRecord && org.CustomDomainPending != null ? $"{VerificationPrefix}.{org.CustomDomainPending}" : null,
            VerificationRecordValue = showRecord ? org.CustomDomainVerificationToken : null,

            // THE SECOND RECORD, and the one that was missing entirely until 2026-09-21. Proving
            // ownership and pointing the name here are different acts at the registrar, and asking
            // for only the first produced a "Verified" domain that still resolved to the tenant's
            // old web host. A CNAME rather than an A record on purpose: this box's address is not
            // the tenant's to depend on, and it has already moved once.
            RoutingRecordName = showRecord ? org.CustomDomainPending : null,
            RoutingRecordValue = showRecord ? NullIfEmpty(PlatformHost()) : null,
            VerifiedAt = org.CustomDomainVerifiedAt,
            ServingSince = org.CustomDomainCertificateAt,
            Attempts = org.CustomDomainAttempts,
            LastError = org.CustomDomainLastError,
            GaveUp = org.CustomDomainPending != null && org.CustomDomainAttempts >= MaxAttempts
        };
    }

    /// <summary>160 bits, URL-safe, prefixed so a human reading a zone file can see what it is for.</summary>
    private static string NewToken()
        => "qmgr-domain-verification=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(20))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Constant-time, because the comparison is against a secret the caller is trying to guess.
    /// The length check leaks only the length, which the instructions already state.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(a.Trim().Trim('"'));
        var right = System.Text.Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
