namespace QMgr.Application.Interfaces;

/// <summary>
/// Proves that whoever is signing up actually controls the phone number they gave, by sending a
/// one-time code to it and checking what comes back.
/// </summary>
public interface IPhoneVerificationService
{
    /// <summary>
    /// Sends a code to the number. Rate limited per number, because each message costs money.
    /// </summary>
    /// <param name="organizationId">
    /// Whose SMS configuration to send through. During registration no organization exists yet, so
    /// the platform organization is used.
    /// </param>
    Task<PhoneVerificationSendResult> SendCodeAsync(Guid organizationId, string phone, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a submitted code. On success returns a short-lived proof token; the sign-up endpoint
    /// requires that token, so a caller cannot simply claim a number it never received a code for.
    /// </summary>
    Task<PhoneVerificationCheckResult> VerifyCodeAsync(string phone, string code, CancellationToken cancellationToken = default);

    /// <summary>True when the token was issued for this number and has not expired.</summary>
    Task<bool> IsProofValidAsync(string? proofToken, string? phone, CancellationToken cancellationToken = default);

    /// <summary>Retires a proof token once it has been used to create an account.</summary>
    Task ConsumeProofAsync(string? proofToken, CancellationToken cancellationToken = default);
}

/// <param name="Sent">False when nothing was sent; <paramref name="Message"/> says why.</param>
/// <param name="Message">Safe to show the applicant.</param>
/// <param name="ExpiresInSeconds">How long the code remains usable.</param>
public record PhoneVerificationSendResult(bool Sent, string? Message, int ExpiresInSeconds);

/// <param name="Verified">Whether the code matched.</param>
/// <param name="Message">Safe to show the applicant, including how many attempts remain.</param>
/// <param name="ProofToken">Present only on success; passed back when submitting the registration.</param>
public record PhoneVerificationCheckResult(bool Verified, string? Message, string? ProofToken);
