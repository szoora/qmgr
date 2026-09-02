using Mediator;

namespace QMgr.Application.Commands.Registration;

/// <summary>
/// Command to register a new organization (tenant) on the platform
/// </summary>
public record RegisterOrganizationCommand : IRequest<RegisterOrganizationResult>
{
    /// <summary>Organization/Company name</summary>
    public string OrganizationName { get; init; } = string.Empty;

    /// <summary>Desired URL slug (optional, auto-generated if not provided)</summary>
    public string? Slug { get; init; }

    /// <summary>Admin user email address</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>Admin user password</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Confirm password</summary>
    public string ConfirmPassword { get; init; } = string.Empty;

    /// <summary>Admin first name</summary>
    public string FirstName { get; init; } = string.Empty;

    /// <summary>Admin last name</summary>
    public string LastName { get; init; } = string.Empty;

    /// <summary>Admin phone number (optional)</summary>
    public string? Phone { get; init; }

    /// <summary>Organization contact phone</summary>
    public string? ContactPhone { get; init; }

    /// <summary>Industry type (healthcare, banking, government, retail, general)</summary>
    public string? IndustryType { get; init; }

    /// <summary>Preferred currency for billing (USD, UGX)</summary>
    public string PreferredCurrency { get; init; } = "USD";

    /// <summary>Accept terms and conditions</summary>
    public bool AcceptTerms { get; init; }

    /// <summary>Registration source (web, api, referral)</summary>
    public string? Source { get; init; }

    /// <summary>Referral code if applicable</summary>
    public string? ReferralCode { get; init; }

    /// <summary>Modules picked in the registration wizard's module-picker step (at least one
    /// required — enforced client-side and re-checked here). Each starts a no-card trial.</summary>
    public List<string> SelectedModuleCodes { get; init; } = new();
}

/// <summary>
/// Result of organization registration
/// </summary>
public record RegisterOrganizationResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Created organization ID</summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>Created admin user ID</summary>
    public Guid? UserId { get; init; }

    /// <summary>Organization slug for URL</summary>
    public string? Slug { get; init; }

    /// <summary>Message to display to user</summary>
    public string? Message { get; init; }

    /// <summary>Whether email verification is required</summary>
    public bool RequiresEmailVerification { get; init; }

    /// <summary>
    /// Whether the verification email actually went out (IEmailSender.SendAsync's real result) —
    /// distinct from RequiresEmailVerification, which is just "this account needs verifying"
    /// regardless of whether the email attempt succeeded. False whenever platform SMTP isn't
    /// configured or the send throws; the account still exists and can be verified via a resend,
    /// but the client needs to know not to claim "check your email" when nothing was sent.
    /// </summary>
    public bool EmailSent { get; init; }

    public static RegisterOrganizationResult Succeeded(Guid orgId, Guid userId, string slug, bool emailSent) => new()
    {
        Success = true,
        OrganizationId = orgId,
        UserId = userId,
        Slug = slug,
        RequiresEmailVerification = true,
        EmailSent = emailSent,
        Message = emailSent
            ? "Registration successful! Please check your email to verify your account."
            : "Registration successful! We couldn't send a verification email right now — you can request one to be resent below."
    };

    public static RegisterOrganizationResult Failed(string errorCode, string message) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        ErrorMessage = message
    };
}
