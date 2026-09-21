using QMgr.Application.DTOs;

namespace QMgr.Application.Interfaces;

/// <summary>
/// The ONE home for a tenant's own domain: claim it, prove they own it, get a certificate, serve
/// it, give it back. Nothing else writes <c>Organization.CustomDomain</c>.
///
/// The order is the category's, and it is the order for a reason (Slack, Atlassian, Shopify,
/// Vercel and Zendesk all do the same four steps): accept the domain, give DNS instructions,
/// verify, issue the certificate, and only THEN route. Traffic never reaches a host nobody has
/// proved they own, and a half-finished claim is indistinguishable from no claim at all to
/// everyone except the platform administrator watching it.
/// </summary>
public interface ICustomDomainService
{
    Task<CustomDomainStatusDto> GetStatusAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a domain for verification. Normalises it, refuses an apex, refuses the platform's
    /// own base domain, refuses a host another tenant already holds, and mints the TXT token.
    /// Writes <c>CustomDomainPending</c> only — never the live column.
    /// </summary>
    Task<CustomDomainResult> RequestAsync(Guid organizationId, string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the outstanding steps: find and match the TXT record, then issue the certificate, then
    /// go live. EACH STEP IS SEPARATELY RETRYABLE AND SEPARATELY REPORTED — a verified domain whose
    /// certificate failed does not go back to square one, and the administrator is told which of the
    /// two failed, because one sends them to a registrar and the other sends them to us.
    /// </summary>
    Task<CustomDomainResult> VerifyAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives the domain back: clears every column and removes the server block. The certificate is
    /// left to expire rather than revoked — revocation is for a compromised key, and a tenant
    /// moving their domain elsewhere has not lost one.
    /// </summary>
    Task<CustomDomainResult> ReleaseAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The daily sweep over pending domains. BACKS OFF AND GIVES UP — a failing domain retried in a
    /// loop spends the box's weekly ACME budget and blocks issuance for every other tenant, so this
    /// skips anything tried within the hour and stops entirely after a week of failures. A human
    /// pressing "Check now" is always allowed through; it is the unattended loop that has to stop.
    /// </summary>
    Task<int> SweepAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether the step worked, and — when it did not — the sentence to put in front of a person.
/// <see cref="Error"/> names the STEP, never just "failed".
/// </summary>
public sealed record CustomDomainResult(bool Ok, string? Error, CustomDomainStatusDto Status);
