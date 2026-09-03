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
    private readonly ILogger<ResendVerificationCommandHandler> _logger;

    public ResendVerificationCommandHandler(
        IUnitOfWork unitOfWork,
        ITenantProvisioningService provisioningService,
        ILogger<ResendVerificationCommandHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _provisioningService = provisioningService;
        _logger = logger;
    }

    // Thin wrapper over ITenantProvisioningService.ResendVerificationEmailAsync — the single
    // source of truth for "regenerate token + send verification email," shared with the
    // platform-admin "Resend Verification" action on SuperAdminController. This handler's own
    // job is just resolving an email to an organization ID and preserving the existing
    // security-through-obscurity contract (always return success, never reveal whether the
    // email exists or why sending failed).
    public async ValueTask<ResendVerificationResult> Handle(ResendVerificationCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return ResendVerificationResult.Failed("INVALID_EMAIL", "Email address is required.");
        }

        try
        {
            var user = await _unitOfWork.Users.FirstOrDefaultAsync(
                u => u.Email.ToLower() == request.Email.ToLower(),
                cancellationToken);

            if (user != null)
            {
                await _provisioningService.ResendVerificationEmailAsync(user.OrganizationId, cancellationToken);
                _logger.LogInformation("Resend verification requested for {Email}", request.Email);
            }

            return ResendVerificationResult.Succeeded();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resending verification email to {Email}", request.Email);
            // Still return success for security reasons
            return ResendVerificationResult.Succeeded();
        }
    }
}
