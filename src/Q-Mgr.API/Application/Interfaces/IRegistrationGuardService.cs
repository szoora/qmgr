using QMgr.Domain.Enums;

namespace QMgr.Application.Interfaces;

/// <summary>
/// Decides whether a sign-up looks like a repeat of an existing customer, and enforces the velocity
/// limits that stop one script opening accounts in bulk.
/// </summary>
public interface IRegistrationGuardService
{
    /// <summary>
    /// Assesses a sign-up before anything is created. Never throws for a risk reason: a refusal
    /// comes back as a <see cref="RegistrationRiskDecision.Block"/> result with a message safe to
    /// show the applicant.
    /// </summary>
    Task<RegistrationRiskAssessment> AssessAsync(RegistrationRiskInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome once the sign-up has finished, so a flagged account is reviewable and the
    /// thresholds can be tuned against real data. Best-effort: a failure here must never fail the
    /// registration that already succeeded.
    /// </summary>
    Task RecordAsync(RegistrationRiskInput input, RegistrationRiskAssessment assessment, Guid? organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts one sign-up attempt against the per-address budget. Returns false when the caller has
    /// exceeded it, along with how long until the window resets.
    /// </summary>
    Task<(bool Allowed, int RetryAfterSeconds)> TryConsumeAttemptAsync(string? clientAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives an address its sign-up budget back. DEVELOPMENT ONLY — the endpoint that calls it is
    /// 404 anywhere else. It exists because the purge suite has to create a tenant to destroy one,
    /// and three sign-ups an hour is correct in production and unworkable for a suite that is run
    /// repeatedly against a dev box. Same shape as the DNS stub and the weekly-analysis trigger.
    /// </summary>
    Task ResetAttemptBudgetAsync(string? clientAddress, CancellationToken cancellationToken = default);
}

/// <summary>Everything the check needs about one sign-up. Raw values; the service normalizes them.</summary>
public record RegistrationRiskInput
{
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public bool PhoneVerified { get; init; }
    public string OrganizationName { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? ClientAddress { get; init; }

    /// <summary>
    /// The applicant was TOLD their organization already uses Q-Mgr — by name, because that school
    /// published their email domain as one it invites people at — and chose to register a new one
    /// anyway (2026-09-20).
    ///
    /// <para>It raises the score and is never a block. CLAUDE.md's standing rule: a wrongly refused
    /// sign-up is a lost customer who cannot appeal, and there ARE honest reasons to continue — a
    /// second campus, a school that wants its own tenant, somebody who no longer works there. So it
    /// is put in front of a human rather than decided by a rule.</para>
    /// </summary>
    public bool AcknowledgedExistingOrganization { get; init; }

    /// <summary>The organization they were shown, for the reviewer. Never trusted as a fact about the caller.</summary>
    public string? AcknowledgedOrganizationName { get; init; }

    /// <summary>
    /// Hidden field no human ever sees. Anything in it means a bot filled the form automatically.
    /// </summary>
    public string? HoneypotValue { get; init; }

    /// <summary>
    /// When the form was rendered, so an implausibly fast submission can be spotted. Null when the
    /// caller did not supply it, which is treated as unknown rather than suspicious.
    /// </summary>
    public DateTime? FormRenderedAt { get; init; }
}

/// <summary>The verdict, the score behind it, and the reasons in plain language.</summary>
public record RegistrationRiskAssessment
{
    public RegistrationRiskDecision Decision { get; init; }
    public int Score { get; init; }

    /// <summary>Reasons, phrased for a platform administrator reading the review queue.</summary>
    public IReadOnlyList<string> Signals { get; init; } = Array.Empty<string>();

    /// <summary>Safe to show the applicant. Only set when the decision is a block.</summary>
    public string? ApplicantMessage { get; init; }

    /// <summary>The closest existing organization, when one was found.</summary>
    public Guid? MatchedOrganizationId { get; init; }

    public bool IsBlocked => Decision == RegistrationRiskDecision.Block;
}
