using System.Diagnostics;

namespace QMgr.Infrastructure.Services.Domains;

/// <summary>
/// Start serving a verified tenant domain, and stop. Behind an interface so the verification flow
/// can run with no host to touch.
///
/// THE NAME IS "ACTIVATOR" RATHER THAN "ISSUER" BECAUSE ISSUING IS THE EXCEPTION, NOT THE JOB.
/// The deployment host carries a certificate shared with the other applications on it, maintained
/// outside this application (user decision, 2026-09-21: "we already have the certificate
/// configured, and nicely running for other projects"), and a tenant on a subdomain of the
/// platform's own host is brought live by pointing at it — nothing issued, nothing to renew.
///
/// A tenant's OWN domain can never be covered by that certificate, and those must work too (the
/// same day: "external domains like dashboard.maryhillug.net should be supported also. this is the
/// reason for whitelabelling"), so the helper issues one for it over http-01. Whichever branch
/// runs, what this owes the caller is a REFUSAL rather than a domain marked live behind a browser
/// warning: that is worse than a domain that is not live yet.
/// </summary>
public interface ITenantDomainActivator
{
    Task<DomainActivationResult> ActivateAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>Stop serving the host: remove its server block and reload. Touches no certificate.</summary>
    Task DeactivateAsync(string domain, CancellationToken cancellationToken = default);
}

/// <param name="CertificateExpiresAt">
/// When the certificate now serving this domain expires — the shared one, or the domain's own.
/// Logged, never stored against the organization: neither is renewed by this application, so a
/// per-tenant copy would go stale the moment something else renewed it and would then be a wrong
/// date shown confidently.
/// </param>
public sealed record DomainActivationResult(bool Ok, string? Error, DateTime? ActivatedAt, DateTime? CertificateExpiresAt = null);

/// <summary>
/// Asks a single root-owned helper, <c>/usr/local/bin/qmgr-tenant-domain</c>, to do the one thing
/// this process must not do itself — and asks it through a spool directory, never by escalating.
///
/// WHY A HELPER: nginx's configuration directory has to be written and nginx reloaded, both of
/// which need root. The API runs as <c>www-data</c> under <c>ProtectSystem=strict</c> and must
/// keep running that way — the alternative is a service that can rewrite the web server's
/// configuration, which is a far larger thing to hand a web application.
///
/// WHY NOT SUDO, WHICH IS WHAT THIS USED TO DO: the same unit sets <c>NoNewPrivileges=true</c>, the
/// flag whose purpose is to stop setuid binaries escalating. sudo is one. So the call could never
/// succeed and never did — every activation failed with "sudo: The 'no new privileges' flag is set",
/// a second after the domain verified, and no suite could see it because they run with
/// <c>CustomDomains:SkipCertificate</c> and never reach the helper. The fix was not to relax the
/// flag for one call a tenant makes once: it was to stop escalating. See <see cref="SpoolPath"/>.
///
/// EVERY FAILURE PATH RETURNS A SENTENCE NAMING THE STEP. Where this runs and the helper is not
/// installed — a developer's machine — it says so plainly rather than pretending, because the one
/// thing worse than "the domain could not be brought live" is a domain marked live that serves
/// nothing.
/// </summary>
public sealed class NginxTenantDomainActivator : ITenantDomainActivator
{
    private readonly ILogger<NginxTenantDomainActivator> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public NginxTenantDomainActivator(ILogger<NginxTenantDomainActivator> logger, IConfiguration configuration, IHostEnvironment environment)
    {
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
    }

    private string HelperPath => _configuration["CustomDomains:HelperPath"] ?? "/usr/local/bin/qmgr-tenant-domain";

    /// <summary>
    /// THE SPOOL, and why this process does not simply run the helper itself.
    ///
    /// <para>The API unit sets <c>NoNewPrivileges=true</c>, whose entire purpose is to stop a setuid
    /// binary from gaining privilege. sudo is a setuid binary. So <c>sudo qmgr-tenant-domain</c>
    /// could never run from here, and did not: every activation of a tenant domain failed with
    /// <c>sudo: The "no new privileges" flag is set, which prevents sudo from running as root</c>,
    /// one second after the domain had verified successfully. Switching the flag off for one call a
    /// tenant makes once would weaken an internet-facing process to reach a setuid path; instead
    /// this process stops escalating at all.</para>
    ///
    /// <para>It writes <c>&lt;id&gt;.req</c> holding "action domain" and waits for
    /// <c>&lt;id&gt;.res</c>, which the root-owned <c>qmgr-domain-worker</c> writes after draining
    /// the request through the same helper. The grant is strictly narrower than the sudoers entry it
    /// replaces: a compromised API can cause that one helper to run and nothing else.</para>
    /// </summary>
    private string SpoolPath => _configuration["CustomDomains:SpoolPath"] ?? "/var/lib/qmgr/domain-spool";

    /// <summary>
    /// How long to wait for the worker. Writing a server block and reloading nginx takes a moment;
    /// issuing a certificate for a domain the shared one cannot cover means a round trip to Let's
    /// Encrypt, so the ceiling is generous. The request fails with a sentence rather than hanging.
    /// </summary>
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// DEVELOPMENT ONLY, and guarded on the environment as well as the key. It marks the domain
    /// served without touching anything, so the verification flow above it can be exercised end to
    /// end on a machine with no nginx — the same call as the payments gateway stub. On any other
    /// environment the key is ignored and a missing helper is a failure.
    ///
    /// The key is still called SkipCertificate although the helper now does more than choose one:
    /// it is read by the e2e suites and by whatever a running install already has configured, so
    /// it is a wire format. What it skips is the whole activation step, certificate included.
    /// </summary>
    private bool SkipInDevelopment =>
        _environment.IsDevelopment() && _configuration.GetValue("CustomDomains:SkipCertificate", false);

    public async Task<DomainActivationResult> ActivateAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (SkipInDevelopment)
        {
            _logger.LogWarning("CustomDomains:SkipCertificate is set — marking {Domain} served WITHOUT touching nginx. Development only.", domain);
            return new DomainActivationResult(true, null, DateTime.UtcNow);
        }

        var result = await RunHelperAsync("enable", domain, cancellationToken);
        if (result.Ok && result.CertificateExpiresAt is { } expiry)
        {
            // The expiry of whichever certificate the helper chose — the shared one for a
            // subdomain of the platform host, the domain's own otherwise — in the server log where
            // the person who maintains it will look. Not stored: see DomainActivationResult.
            _logger.LogInformation(
                "{Domain} is served by a certificate that expires {Expiry:yyyy-MM-dd}.", domain, expiry);
        }
        return result;
    }

    public async Task DeactivateAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (SkipInDevelopment) return;

        var result = await RunHelperAsync("disable", domain, cancellationToken);
        if (!result.Ok)
        {
            // A domain that has been released is released whether or not its server block could be
            // removed; the database is the record. Logged loudly so a stale block is visible.
            _logger.LogError("Could not remove the nginx server block for {Domain}: {Error}", domain, result.Error);
        }
    }

    private async Task<DomainActivationResult> RunHelperAsync(string verb, string domain, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            return new DomainActivationResult(false, "The domain could not be brought live: this server is not the deployment host. A tenant domain is set up on the live server.", null);

        if (!File.Exists(HelperPath))
            return new DomainActivationResult(false, $"The domain could not be brought live: the domain helper ({HelperPath}) is not installed on this server. It ships with the deploy package.", null);

        if (!Directory.Exists(SpoolPath))
            return new DomainActivationResult(false, $"The domain could not be brought live: the request directory ({SpoolPath}) is not on this server. It is created by install.sh — the deploy is older than the worker.", null);

        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(SpoolPath, id + ".req");
        var resultPath = Path.Combine(SpoolPath, id + ".res");

        try
        {
            // The domain has already passed this service's own validation, and the helper validates
            // it again on the other side of the privilege boundary. One line, two fields, no shell.
            await File.WriteAllTextAsync(requestPath, $"{verb} {domain}\n", cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(WorkerTimeout);

            string? answer = null;
            while (!timeout.IsCancellationRequested)
            {
                if (File.Exists(resultPath))
                {
                    answer = await File.ReadAllTextAsync(resultPath, timeout.Token);
                    break;
                }
                await Task.Delay(250, timeout.Token);
            }

            if (answer is null)
            {
                // The request file is left behind on purpose: the worker may yet pick it up, and a
                // half-finished activation is visible in the spool rather than invisible.
                _logger.LogError("Domain worker did not answer for {Domain} within {Seconds}s — is qmgr-domain-worker.path enabled?",
                    domain, WorkerTimeout.TotalSeconds);
                return new DomainActivationResult(false,
                    "The domain could not be brought live: the server's domain worker did not answer. Check that qmgr-domain-worker.path is enabled on this server.", null);
            }

            var (exitCode, output) = ReadAnswer(answer);
            TryDelete(resultPath);

            if (exitCode == 0)
            {
                _logger.LogInformation("Domain helper {Verb} succeeded for {Domain}", verb, domain);
                return new DomainActivationResult(true, null, DateTime.UtcNow, ReadExpiry(output));
            }

            _logger.LogError("Domain helper {Verb} failed for {Domain} (exit {Exit}): {Output}", verb, domain, exitCode, output);
            return new DomainActivationResult(false, Explain(output), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DomainActivationResult(false, "The domain could not be brought live: the server did not finish reloading in time. Try again in a few minutes.", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain helper {Verb} threw for {Domain}", verb, domain);
            return new DomainActivationResult(false, "The domain could not be brought live. The server log has the detail.", null);
        }
    }

    /// <summary>
    /// The worker's answer: a first line of <c>exit=&lt;n&gt;</c>, then everything the helper wrote.
    /// An answer that does not begin that way is treated as a failure rather than a success, because
    /// the one thing worse than a domain that will not go live is one reported live that is not.
    /// </summary>
    private static (int ExitCode, string Output) ReadAnswer(string answer)
    {
        var newline = answer.IndexOf('\n');
        var first = (newline < 0 ? answer : answer[..newline]).Trim();
        var rest = newline < 0 ? string.Empty : answer[(newline + 1)..].Trim();

        if (first.StartsWith("exit=", StringComparison.Ordinal) && int.TryParse(first[5..], out var code))
            return (code, rest);

        return (-1, answer.Trim());
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not remove the domain worker's answer at {Path}", path); }
    }

    /// <summary>The helper prints <c>expires=&lt;ISO 8601&gt;</c> for the certificate it used.</summary>
    private static DateTime? ReadExpiry(string stdout)
    {
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("expires=", StringComparison.Ordinal)) continue;
            if (DateTime.TryParse(trimmed[8..], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        return null;
    }

    /// <summary>
    /// Turns the helper's output into the sentence a platform administrator needs.
    ///
    /// THE HELPER'S OWN REFUSALS ARE PASSED THROUGH, because they are already written for this
    /// reader and are the only ones that can name the specific fix — above all "the certificate on
    /// this server does not cover this domain", which is answered by adding the domain to that
    /// certificate and has nothing to do with Q-Mgr. Anything else is summarised, since it is a
    /// configuration fault on the box and the log has the detail.
    /// </summary>
    private static string Explain(string output)
    {
        var refusal = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("refused:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["refused:".Length..].Trim())
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(refusal))
        {
            // Everything the helper printed after the refusal is the instruction that goes with it.
            var detail = output.Split('\n')
                .Select(l => l.Trim())
                .SkipWhile(l => !l.StartsWith("refused:", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .Where(l => l.Length > 0 && !l.StartsWith("(", StringComparison.Ordinal))
                .ToList();
            return detail.Count > 0
                ? $"{char.ToUpperInvariant(refusal[0])}{refusal[1..]} {string.Join(" ", detail)}"
                : $"{char.ToUpperInvariant(refusal[0])}{refusal[1..]}";
        }

        if (output.Contains("nginx rejected", StringComparison.OrdinalIgnoreCase))
            return "The domain could not be brought live: nginx rejected the configuration and it was rolled back. The server log has nginx's own output.";

        // Kept although nothing should produce it any more: an install still carrying the old
        // sudoers path would otherwise report this as an unexplained failure.
        if (output.Contains("sudo:", StringComparison.OrdinalIgnoreCase))
            return "The domain could not be brought live: this server is still trying to run the domain helper through sudo, which its own hardening forbids. Re-deploy — the current package replaces that with a worker.";

        return "The domain could not be brought live. The server log has the helper's own output.";
    }
}
