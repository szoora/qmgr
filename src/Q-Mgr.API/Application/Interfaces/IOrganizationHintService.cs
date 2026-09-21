using QMgr.Application.DTOs;

namespace QMgr.Application.Interfaces;

/// <summary>
/// "Does the organization behind this email address already use Q-Mgr?" — asked by the registration
/// form before somebody creates a SECOND copy of their own school (2026-09-20).
///
/// <para>The failure this exists to stop: a teacher at a school that already runs Q-Mgr reads
/// "Don't have an account? Create one", tells the truth, and ends up with a duplicate tenant on a
/// trial, with themselves as its administrator and no connection to the real school. The fix that
/// every B2B product with tenants eventually builds is domain discovery — Slack's whole
/// duplicate-workspace guidance is this one problem.</para>
///
/// <para><b>DISCLOSURE IS CONSENT-BASED AND THAT IS THE DESIGN.</b> A tenant is named only when it
/// has switched staff self-sign-up ON and listed the domain in its own allowed-domain list, which is
/// a published statement that people at that domain may ask to join. Nothing else is matched — not
/// the tenant's contact address, not a similar organization name. Naming a school on a guess would
/// be a leak; naming one that published the domain itself is repeating the school's own invitation.</para>
///
/// <para><b>It never returns the join code.</b> The code is the school's secret and rotating it is
/// how a school closes the door; the answer names the school and sends the applicant to the page
/// that takes a code.</para>
/// </summary>
public interface IOrganizationHintService
{
    Task<OrganizationHintDto> LookUpAsync(string? email, CancellationToken cancellationToken = default);
}
