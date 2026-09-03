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
