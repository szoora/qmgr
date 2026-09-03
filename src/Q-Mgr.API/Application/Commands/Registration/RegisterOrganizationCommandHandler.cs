using Mediator;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Platform;
using QMgr.Infrastructure.Email;

namespace QMgr.Application.Commands.Registration;

/// <summary>
/// Handles organization registration command
/// </summary>
public class RegisterOrganizationCommandHandler : IRequestHandler<RegisterOrganizationCommand, RegisterOrganizationResult>
{
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IEmailSender _emailSender;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly IModuleAccessService _moduleAccessService;
    private readonly ILogger<RegisterOrganizationCommandHandler> _logger;

    public RegisterOrganizationCommandHandler(
        ITenantProvisioningService provisioningService,
        IEmailSender emailSender,
        IPlatformSettingsService platformSettingsService,
        IModuleAccessService moduleAccessService,
        ILogger<RegisterOrganizationCommandHandler> logger)
    {
        _provisioningService = provisioningService;
        _emailSender = emailSender;
        _platformSettingsService = platformSettingsService;
        _moduleAccessService = moduleAccessService;
        _logger = logger;
    }

    public async ValueTask<RegisterOrganizationResult> Handle(RegisterOrganizationCommand request, CancellationToken cancellationToken)
    {
        // Validate request
        var validationResult = ValidateRequest(request);
        if (!validationResult.Success)
        {
            return validationResult;
        }

        // Check slug availability if provided
        if (!string.IsNullOrEmpty(request.Slug))
        {
            var isAvailable = await _provisioningService.ValidateSlugAvailabilityAsync(request.Slug, cancellationToken);
            if (!isAvailable)
            {
                return RegisterOrganizationResult.Failed("SLUG_TAKEN", $"The slug '{request.Slug}' is already in use. Please choose a different one.");
            }
        }

        try
        {
            // Provision the tenant
            var provisionResult = await _provisioningService.ProvisionTenantAsync(new ProvisionTenantRequest
            {
                OrganizationName = request.OrganizationName,
                Slug = request.Slug,
                AdminEmail = request.Email,
                AdminPassword = request.Password,
                AdminFirstName = request.FirstName,
                AdminLastName = request.LastName,
                AdminPhone = request.Phone,
                ContactPhone = request.ContactPhone,
                IndustryType = request.IndustryType,
                PreferredCurrency = request.PreferredCurrency,
                Source = request.Source,
                ReferralCode = request.ReferralCode
            }, cancellationToken);

            if (!provisionResult.Success)
            {
                _logger.LogWarning("Tenant provisioning failed: {Error}", provisionResult.ErrorMessage);
                return RegisterOrganizationResult.Failed("PROVISIONING_FAILED", provisionResult.ErrorMessage ?? "Failed to create organization.");
            }

            // Start a no-card trial for the one module picked in the registration wizard —
            // ValidateRequest above already enforces exactly one. This is the organization's only
            // trial, ever: every module purchased afterward (via ModulesController) always
            // collects real payment immediately, never a trial.
            foreach (var moduleCode in request.SelectedModuleCodes.Distinct())
            {
                try
                {
                    await _moduleAccessService.StartTrialAsync(provisionResult.OrganizationId, moduleCode);
                }
                catch (Exception ex)
                {
                    // A bad/unknown module code shouldn't fail the whole registration — the
                    // organization and admin account already exist at this point.
                    _logger.LogError(ex, "Failed to start trial for module {ModuleCode} on org {OrgId}", moduleCode, provisionResult.OrganizationId);
                }
            }

            // Platform Settings > SaaS > "Require email verification" — when switched off the org is
            // verified straight away through the same path an admin's "Verify Now" uses (status,
            // default branch, service types), so nothing is left half-provisioned.
            var saasSettings = await _platformSettingsService.GetSettingsAsync<SaasSettings>("SaaS");
            var requireVerification = saasSettings?.RequireEmailVerification ?? true;

            bool emailSent;
            if (requireVerification)
            {
                emailSent = await SendVerificationEmailAsync(
                    request.Email,
                    request.FirstName,
                    provisionResult.OrganizationId,
                    provisionResult.VerificationToken!,
                    provisionResult.Slug,
                    cancellationToken);
            }
            else
            {
                await _provisioningService.AdminVerifyAsync(provisionResult.OrganizationId, cancellationToken);
                emailSent = true; // nothing to send; the client treats this as "no email problem to report"
                _logger.LogInformation("Email verification disabled in Platform Settings — organization {OrganizationId} auto-verified", provisionResult.OrganizationId);
            }

            _logger.LogInformation(
                "Organization {OrganizationId} registered successfully with slug {Slug}",
                provisionResult.OrganizationId,
                provisionResult.Slug);

            return RegisterOrganizationResult.Succeeded(
                provisionResult.OrganizationId,
                provisionResult.AdminUserId,
                provisionResult.Slug,
                emailSent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during organization registration for {Email}", request.Email);
            return RegisterOrganizationResult.Failed("REGISTRATION_ERROR", "An error occurred during registration. Please try again.");
        }
    }

    private static RegisterOrganizationResult ValidateRequest(RegisterOrganizationCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.OrganizationName))
        {
            return RegisterOrganizationResult.Failed("INVALID_ORG_NAME", "Organization name is required.");
        }

        if (request.OrganizationName.Length < 2 || request.OrganizationName.Length > 100)
        {
            return RegisterOrganizationResult.Failed("INVALID_ORG_NAME", "Organization name must be between 2 and 100 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return RegisterOrganizationResult.Failed("INVALID_EMAIL", "Email address is required.");
        }

        if (!IsValidEmail(request.Email))
        {
            return RegisterOrganizationResult.Failed("INVALID_EMAIL", "Please enter a valid email address.");
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return RegisterOrganizationResult.Failed("INVALID_PASSWORD", "Password is required.");
        }

        if (request.Password.Length < 8)
        {
            return RegisterOrganizationResult.Failed("WEAK_PASSWORD", "Password must be at least 8 characters long.");
        }

        if (request.Password != request.ConfirmPassword)
        {
            return RegisterOrganizationResult.Failed("PASSWORD_MISMATCH", "Passwords do not match.");
        }

        if (string.IsNullOrWhiteSpace(request.FirstName))
        {
            return RegisterOrganizationResult.Failed("INVALID_NAME", "First name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.LastName))
        {
            return RegisterOrganizationResult.Failed("INVALID_NAME", "Last name is required.");
        }

        if (!request.AcceptTerms)
        {
            return RegisterOrganizationResult.Failed("TERMS_NOT_ACCEPTED", "You must accept the terms and conditions to register.");
        }

        // Exactly one, not "at least one" — the trial privilege is used once per organization,
        // at registration, and never granted again (see ModulesController's TRIAL_IN_PROGRESS
        // gate). A subscriber unlocks further modules by fully paying for this first one.
        if (request.SelectedModuleCodes.Count != 1)
        {
            return RegisterOrganizationResult.Failed("MODULE_SELECTION_INVALID", "Select exactly one module to start your trial — you can add more once this one is fully paid.");
        }

        var unknownModule = request.SelectedModuleCodes.FirstOrDefault(m => !ModuleCodes.All.Contains(m));
        if (unknownModule != null)
        {
            return RegisterOrganizationResult.Failed("INVALID_MODULE", $"'{unknownModule}' is not a valid module.");
        }

        // Validate slug format if provided
        if (!string.IsNullOrEmpty(request.Slug))
        {
            if (!IsValidSlug(request.Slug))
            {
                return RegisterOrganizationResult.Failed("INVALID_SLUG", "Slug must contain only lowercase letters, numbers, and hyphens.");
            }

            if (request.Slug.Length < 3 || request.Slug.Length > 50)
            {
                return RegisterOrganizationResult.Failed("INVALID_SLUG", "Slug must be between 3 and 50 characters.");
            }
        }

        return new RegisterOrganizationResult { Success = true };
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidSlug(string slug)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(slug, @"^[a-z0-9][a-z0-9-]*[a-z0-9]$|^[a-z0-9]$");
    }

    private async Task<bool> SendVerificationEmailAsync(
        string email,
        string firstName,
        Guid organizationId,
        string verificationToken,
        string slug,
        CancellationToken cancellationToken)
    {
        try
        {
            // Build verification URL from the configured platform base URL rather than a
            // hardcoded "qmgr.app" literal, so this actually points somewhere real in any
            // environment other than that exact production domain (this was previously
            // hardcoded and never caught because dev SMTP is unconfigured, so the email — and
            // therefore this URL — never actually got sent/seen).
            var saas = await _platformSettingsService.GetSettingsAsync<SaasSettings>("SaaS");
            var baseUrl = (saas?.BaseUrl ?? "https://qmgr.app").TrimEnd('/');
            var verificationUrl = $"{baseUrl}/verify?org={organizationId}&token={verificationToken}";

            var subject = "Verify your Q-Mgr account";
            var htmlBody = EmailTemplates.Layout(
                $"Welcome to {EmailTemplates.AppName}!",
                firstName,
                new[]
                {
                    $"Thank you for registering with {EmailTemplates.AppName}. To complete your registration and activate your account, please verify your email address by clicking the button below.",
                    "This link will expire in 24 hours."
                },
                "Verify Email Address",
                verificationUrl,
                footerNote: $"You can sign in any time at {EmailTemplates.B(baseUrl)}. If you didn't create this account, you can safely ignore this email.",
                showLinkFallback: true);

            return await _emailSender.SendAsync(
                email,
                subject,
                htmlBody,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send verification email to {Email}", email);
            // Don't fail registration if email fails - they can request a new one
            return false;
        }
    }
}
