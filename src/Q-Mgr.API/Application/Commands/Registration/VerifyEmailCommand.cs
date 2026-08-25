using Mediator;

namespace QMgr.Application.Commands.Registration;

/// <summary>
/// Command to verify email and activate a tenant account
/// </summary>
public record VerifyEmailCommand : IRequest<VerifyEmailResult>
{
    /// <summary>Organization ID</summary>
    public Guid OrganizationId { get; init; }

    /// <summary>Email verification token</summary>
    public string Token { get; init; } = string.Empty;
}

/// <summary>
/// Result of email verification
/// </summary>
public record VerifyEmailResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Slug { get; init; }
    public string? Message { get; init; }

    /// <summary>Redirect URL after verification</summary>
    public string? RedirectUrl { get; init; }

    // Relative, not a per-tenant subdomain ("https://{slug}.qmgr.app/...") — this app resolves
    // the tenant from the JWT after login on a single shared host (every page in this codebase
    // is reached via that one host), not via subdomain routing, so a subdomain URL here would
    // 404 in this deployment. Slug is kept on the result for display purposes even though it's
    // no longer part of the redirect.
    public static VerifyEmailResult Succeeded(string slug) => new()
    {
        Success = true,
        Slug = slug,
        Message = "Your email has been verified successfully! You can now log in to your account.",
        RedirectUrl = "/login?verified=true"
    };

    public static VerifyEmailResult Failed(string errorCode, string message) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        ErrorMessage = message
    };
}

/// <summary>
/// Command to resend verification email
/// </summary>
public record ResendVerificationCommand : IRequest<ResendVerificationResult>
{
    /// <summary>Email address to resend verification to</summary>
    public string Email { get; init; } = string.Empty;
}

/// <summary>
/// Result of resend verification request
/// </summary>
public record ResendVerificationResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Message { get; init; }

    public static ResendVerificationResult Succeeded() => new()
    {
        Success = true,
        Message = "If an account exists with this email, a new verification link has been sent."
    };

    public static ResendVerificationResult Failed(string errorCode, string message) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        ErrorMessage = message
    };
}
