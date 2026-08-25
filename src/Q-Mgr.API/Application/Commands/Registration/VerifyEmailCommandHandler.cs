using Mediator;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Domain.Interfaces;

namespace QMgr.Application.Commands.Registration;

/// <summary>
/// Handles email verification command
/// </summary>
public class VerifyEmailCommandHandler : IRequestHandler<VerifyEmailCommand, VerifyEmailResult>
{
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<VerifyEmailCommandHandler> _logger;

    public VerifyEmailCommandHandler(
        ITenantProvisioningService provisioningService,
        IUnitOfWork unitOfWork,
        ILogger<VerifyEmailCommandHandler> logger)
    {
        _provisioningService = provisioningService;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async ValueTask<VerifyEmailResult> Handle(VerifyEmailCommand request, CancellationToken cancellationToken)
    {
        if (request.OrganizationId == Guid.Empty)
        {
            return VerifyEmailResult.Failed("INVALID_REQUEST", "Invalid organization ID.");
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return VerifyEmailResult.Failed("INVALID_TOKEN", "Verification token is required.");
        }

        try
        {
            // Get organization
            var organization = await _unitOfWork.Organizations.GetByIdAsync(request.OrganizationId, cancellationToken);
            if (organization == null)
            {
                return VerifyEmailResult.Failed("ORG_NOT_FOUND", "Organization not found.");
            }

            // Check if already verified
            if (organization.Status != TenantStatus.Pending)
            {
                if (organization.VerifiedAt != null)
                {
                    return VerifyEmailResult.Failed("ALREADY_VERIFIED", "This email has already been verified. You can log in to your account.");
                }
            }

            // Verify the token
            var verified = await _provisioningService.VerifyEmailAsync(
                request.OrganizationId,
                request.Token,
                cancellationToken);

            if (!verified)
            {
                return VerifyEmailResult.Failed("INVALID_TOKEN", "The verification link is invalid or has expired. Please request a new verification email.");
            }

            _logger.LogInformation("Email verified for organization {OrganizationId}", request.OrganizationId);

            return VerifyEmailResult.Succeeded(organization.Slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying email for organization {OrganizationId}", request.OrganizationId);
            return VerifyEmailResult.Failed("VERIFICATION_ERROR", "An error occurred during verification. Please try again.");
        }
    }
}

/// <summary>
/// Handles resend verification email command
/// </summary>
public class ResendVerificationCommandHandler : IRequestHandler<ResendVerificationCommand, ResendVerificationResult>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IEmailSender _emailSender;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly ILogger<ResendVerificationCommandHandler> _logger;

    public ResendVerificationCommandHandler(
        IUnitOfWork unitOfWork,
        ITenantProvisioningService provisioningService,
        IEmailSender emailSender,
        IPlatformSettingsService platformSettingsService,
        ILogger<ResendVerificationCommandHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _provisioningService = provisioningService;
        _emailSender = emailSender;
        _platformSettingsService = platformSettingsService;
        _logger = logger;
    }

    public async ValueTask<ResendVerificationResult> Handle(ResendVerificationCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return ResendVerificationResult.Failed("INVALID_EMAIL", "Email address is required.");
        }

        try
        {
            // Find user by email
            var user = await _unitOfWork.Users.FirstOrDefaultAsync(
                u => u.Email.ToLower() == request.Email.ToLower(),
                cancellationToken);

            // Return success even if not found (security - don't reveal if email exists)
            if (user == null)
            {
                return ResendVerificationResult.Succeeded();
            }

            // Get organization
            var organization = await _unitOfWork.Organizations.GetByIdAsync(user.OrganizationId, cancellationToken);
            if (organization == null || organization.Status != TenantStatus.Pending)
            {
                return ResendVerificationResult.Succeeded();
            }

            // Generate new token
            var token = await _provisioningService.GenerateVerificationTokenAsync(
                organization.Id,
                cancellationToken);

            // Send verification email
            await SendVerificationEmailAsync(user, organization, token, cancellationToken);

            _logger.LogInformation("Resent verification email to {Email}", request.Email);

            return ResendVerificationResult.Succeeded();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resending verification email to {Email}", request.Email);
            // Still return success for security reasons
            return ResendVerificationResult.Succeeded();
        }
    }

    private async Task SendVerificationEmailAsync(
        Domain.Entities.Identity.User user,
        Domain.Entities.Organization.Organization organization,
        string token,
        CancellationToken cancellationToken)
    {
        var saas = await _platformSettingsService.GetSettingsAsync<SaasSettings>("SaaS");
        var baseUrl = (saas?.BaseUrl ?? "https://qmgr.app").TrimEnd('/');
        var verificationUrl = $"{baseUrl}/verify?org={organization.Id}&token={token}";

        var subject = "Verify your Q-Mgr account";
        var htmlBody = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Verify your email</title>
</head>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #2563eb;'>Verify your email</h1>
        <p>Hi {user.FirstName},</p>
        <p>You requested a new verification link for your Q-Mgr account. Click the button below to verify your email:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='{verificationUrl}' style='background-color: #2563eb; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px; display: inline-block;'>Verify Email Address</a>
        </div>
        <p>Or copy and paste this link into your browser:</p>
        <p style='word-break: break-all; color: #666;'>{verificationUrl}</p>
        <p>This link will expire in 24 hours.</p>
        <hr style='border: none; border-top: 1px solid #eee; margin: 30px 0;'>
        <p style='color: #666; font-size: 14px;'>
            If you didn't request this email, you can safely ignore it.
        </p>
    </div>
</body>
</html>";

        await _emailSender.SendAsync(
            user.Email,
            subject,
            htmlBody,
            cancellationToken);
    }
}
