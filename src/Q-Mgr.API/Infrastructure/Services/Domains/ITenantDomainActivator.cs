using System.Diagnostics;

namespace QMgr.Infrastructure.Services.Domains;

/// <summary>
/// Start serving a verified tenant domain, and stop. Behind an interface so the verification flow
/// can run with no host to touch.
///
/// IT DOES NOT ISSUE CERTIFICATES, and the name says so (user decision, 2026-09-21: "we already
/// have the certificate configured, and nicely running for other projects"). The deployment host
/// carries one certificate, shared with the other applications on it and maintained outside this
/// application; a tenant domain is brought live by pointing it at that certificate. What this
/// still owes the caller is a REFUSAL when the certificate does not cover the domain — a domain
/// marked live behind a browser warning is worse than one that is not live yet.
/// </summary>
public interface ITenantDomainActivator
{
    Task<DomainActivationResult> ActivateAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>Stop serving the host: remove its server block and reload. Touches no certificate.</summary>
    Task DeactivateAsync(string domain, CancellationToken cancellationToken = default);
}

/// <param name="CertificateExpiresAt">
/// When the certificate now serving this domain expires. It belongs to the whole server, not to
/// the tenant, so it is logged rather than stored against the organization — a per-tenant copy
/// would go stale the moment the certificate was renewed and would then be a wrong date shown
/// confidently.
/// </param>
public sealed record DomainActivationResult(bool Ok, string? Error, DateTime? ActivatedAt, DateTime? CertificateExpiresAt = null);

/// <summary>
/// Shells out to a single root-owned helper, <c>/usr/local/bin/qmgr-tenant-domain</c>, which
/// <c>install.sh</c> writes and a sudoers drop-in lets this process run — and nothing else.
///
/// WHY A HELPER: nginx's configuration directory has to be written and nginx reloaded, both of
/// which need root. The API runs as <c>www-data</c> under <c>ProtectSystem=strict</c> and must
/// keep running that way — the alternative is a service that can rewrite the web server's
/// configuration, which is a far larger thing to hand a web application. One narrow,
/// argument-validated command with a no-password sudoers entry is the smallest grant that works.
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
    /// DEVELOPMENT ONLY, and guarded on the environment as well as the key. It marks the domain
    /// served without touching anything, so the verification flow above it can be exercised end to
    /// end on a machine with no nginx — the same call as the payments gateway stub. On any other
    /// environment the key is ignored and a missing helper is a failure.
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
            // The shared certificate's own expiry, in the server log where the person who
            // maintains it will look. Not stored: see DomainActivationResult.
            _logger.LogInformation(
                "{Domain} is served by this server's certificate, which expires {Expiry:yyyy-MM-dd}.", domain, expiry);
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

        try
        {
            var psi = new ProcessStartInfo("sudo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-n");          // never prompt; a password prompt would hang the request
            psi.ArgumentList.Add(HelperPath);
            psi.ArgumentList.Add(verb);
            psi.ArgumentList.Add(domain);

            using var process = Process.Start(psi);
            if (process == null)
                return new DomainActivationResult(false, "The domain could not be brought live: the domain helper would not start.", null);

            // Writing a file and reloading nginx is fast; the timeout is here so a reload that
            // hangs on a bad configuration fails the request rather than holding it open.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            var outText = await stdout;
            var output = (outText + "\n" + (await stderr)).Trim();
            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Domain helper {Verb} succeeded for {Domain}", verb, domain);
                return new DomainActivationResult(true, null, DateTime.UtcNow, ReadExpiry(outText));
            }

            _logger.LogError("Domain helper {Verb} failed for {Domain} (exit {Exit}): {Output}", verb, domain, process.ExitCode, output);
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

        if (output.Contains("sudo:", StringComparison.OrdinalIgnoreCase))
            return "The domain could not be brought live: this server is not allowing the domain helper to run. The sudoers drop-in ships with the deploy package.";

        return "The domain could not be brought live. The server log has the helper's own output.";
    }
}
