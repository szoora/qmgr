using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.Application.Commands.Registration;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Controller for self-service organization registration and onboarding
/// </summary>
[ApiController]
[Route("api/v1/register")]
[Produces("application/json")]
public class RegistrationController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly IRegistrationGuardService _registrationGuard;
    private readonly IPhoneVerificationService _phoneVerification;
    private readonly IOrganizationHintService _organizationHint;
    private readonly ILogger<RegistrationController> _logger;

    /// <summary>The platform's own organization, whose SMS configuration sends sign-up codes.</summary>
    private static readonly Guid PlatformOrganizationId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public RegistrationController(
        IMediator mediator,
        ITenantProvisioningService provisioningService,
        IPlatformSettingsService platformSettingsService,
        IRegistrationGuardService registrationGuard,
        IPhoneVerificationService phoneVerification,
        IOrganizationHintService organizationHint,
        ILogger<RegistrationController> logger)
    {
        _registrationGuard = registrationGuard;
        _phoneVerification = phoneVerification;
        _organizationHint = organizationHint;
        _mediator = mediator;
        _provisioningService = provisioningService;
        _platformSettingsService = platformSettingsService;
        _logger = logger;
    }

    /// <summary>
    /// Real trial length and base domain, for the registration page's own marketing copy and
    /// subdomain-preview text to render instead of hardcoded values — the trial length previously
    /// said "14-day free trial" regardless of what PlatformSettings.SaaS.TrialDays actually was,
    /// and the subdomain suffix shown next to the slug field was a bare ".qmgr.app" string
    /// literal, independent of PlatformSettings.SaaS.BaseDomain — both are exactly the kind of
    /// drift that happens when the same fact is asserted in two places (see CLAUDE.md's SSoT
    /// note). The platform's actual base domain belongs entirely to Platform Settings; nothing in
    /// application code should assume what it is.
    /// </summary>
    /// <summary>
    /// "Does the organization behind this email address already use Q-Mgr?" (2026-09-20).
    ///
    /// <para>The registration form asks this before somebody creates a SECOND copy of their own
    /// school. It answers only for a tenant that switched staff self-sign-up on and listed the
    /// domain ITSELF — see <see cref="IOrganizationHintService"/> for why that consent is the whole
    /// design, and why the join code is never part of the answer.</para>
    ///
    /// <para>Anonymous by necessity, like every other step of sign-up, and charged against the same
    /// IP budget. It reveals strictly less than the join link it points at, which is a URL anybody
    /// holding it can already open.</para>
    /// </summary>
    [HttpGet("organization-hint")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(OrganizationHintDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrganizationHint([FromQuery] string? email, CancellationToken cancellationToken)
        => Ok(await _organizationHint.LookUpAsync(email, cancellationToken));

    [HttpGet("trial-info")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTrialInfo()
    {
        var saas = await _platformSettingsService.GetSettingsAsync<QMgr.Domain.Entities.Platform.SaasSettings>("SaaS");
        return Ok(new { trialDays = saas?.TrialDays ?? 14, baseDomain = saas?.BaseDomain ?? "" });
    }

    /// <summary>
    /// Sends a one-time code to a phone number so the applicant can prove they control it.
    /// </summary>
    /// <remarks>
    /// Anonymous by necessity, since no account exists yet. Abuse is bounded two ways: the number
    /// itself is capped at a few codes an hour inside the service, and the caller's connection is
    /// charged against the same sign-up budget the registration endpoint uses, so this cannot be
    /// turned into a free SMS pump.
    /// </remarks>
    [HttpPost("phone/send-code")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendPhoneCode([FromBody] SendPhoneCodeRequest request)
    {
        var clientAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (allowed, retryAfterSeconds) = await _registrationGuard.TryConsumeAttemptAsync(clientAddress);
        if (!allowed)
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponse
            {
                Error = "TOO_MANY_ATTEMPTS",
                Message = "Too many attempts from this connection. Please try again later."
            });
        }

        // No organization exists during sign-up, so this goes out through the platform's own SMS
        // configuration rather than a tenant's.
        var result = await _phoneVerification.SendCodeAsync(PlatformOrganizationId, request.Phone ?? string.Empty);

        return result.Sent
            ? Ok(new { sent = true, expiresInSeconds = result.ExpiresInSeconds })
            : BadRequest(new ErrorResponse { Error = "CODE_NOT_SENT", Message = result.Message ?? "Could not send the code." });
    }

    /// <summary>
    /// Checks a one-time code and, on success, returns the proof token the sign-up request needs.
    /// </summary>
    [HttpPost("phone/verify-code")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyPhoneCode([FromBody] VerifyPhoneCodeRequest request)
    {
        var result = await _phoneVerification.VerifyCodeAsync(request.Phone ?? string.Empty, request.Code ?? string.Empty);

        return result.Verified
            ? Ok(new { verified = true, token = result.ProofToken })
            : BadRequest(new ErrorResponse { Error = "CODE_INVALID", Message = result.Message ?? "That code is not right." });
    }

    /// <summary>
    /// Register a new organization on the platform
    /// </summary>
    /// <remarks>
    /// Creates a new organization with an admin user account.
    /// A verification email will be sent to the provided email address.
    /// </remarks>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var clientAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        // Velocity budget first, before any database work, so a script cannot make the expensive
        // duplicate check run over and over.
        var (allowed, retryAfterSeconds) = await _registrationGuard.TryConsumeAttemptAsync(clientAddress);
        if (!allowed)
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponse
            {
                Error = "TOO_MANY_ATTEMPTS",
                Message = "Too many sign-up attempts from this connection. Please try again later."
            });
        }

        // A phone number is only treated as verified when the caller can produce the proof token
        // issued by the SMS step. Without this the flag could simply be asserted in the request.
        var phoneVerified = await _phoneVerification.IsProofValidAsync(request.PhoneVerificationToken, request.Phone);

        var riskInput = new RegistrationRiskInput
        {
            Email = request.Email,
            Phone = request.Phone,
            PhoneVerified = phoneVerified,
            OrganizationName = request.OrganizationName,
            FirstName = request.FirstName,
            LastName = request.LastName,
            ClientAddress = clientAddress,
            HoneypotValue = request.ContactReference,
            FormRenderedAt = request.FormRenderedAt,
            AcknowledgedExistingOrganization = request.AcknowledgedExistingOrganization,
            AcknowledgedOrganizationName = request.AcknowledgedOrganizationName
        };

        var assessment = await _registrationGuard.AssessAsync(riskInput);
        if (assessment.IsBlocked)
        {
            await _registrationGuard.RecordAsync(riskInput, assessment, null);
            _logger.LogInformation("Registration blocked for {Email}: {Signals}", request.Email, string.Join(" | ", assessment.Signals));

            return StatusCode(StatusCodes.Status409Conflict, new ErrorResponse
            {
                Error = "DUPLICATE_REGISTRATION",
                Message = assessment.ApplicantMessage ?? "An account already exists for these details."
            });
        }

        var command = new RegisterOrganizationCommand
        {
            PhoneVerifiedAt = phoneVerified ? DateTime.UtcNow : null,
            OrganizationName = request.OrganizationName,
            BrandName = request.BrandName,
            Slug = request.Slug,
            Email = request.Email,
            Password = request.Password,
            ConfirmPassword = request.ConfirmPassword,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Phone = request.Phone,
            ContactPhone = request.ContactPhone,
            IndustryType = request.IndustryType,
            PreferredCurrency = request.PreferredCurrency ?? "UGX",
            AcceptTerms = request.AcceptTerms,
            Source = "web",
            ReferralCode = request.ReferralCode,
            SelectedModuleCodes = request.SelectedModuleCodes ?? new()
        };

        var result = await _mediator.Send(command);

        // Record every outcome, successful or not. A flagged account has to be reviewable, and the
        // weights can only be tuned against real decisions rather than guesses.
        await _registrationGuard.RecordAsync(riskInput, assessment, result.Success ? result.OrganizationId : null);

        if (result.Success && phoneVerified)
        {
            await _phoneVerification.ConsumeProofAsync(request.PhoneVerificationToken);
        }

        if (!result.Success)
        {
            _logger.LogWarning("Registration failed for {Email}: {Error}", request.Email, result.ErrorCode);

            var statusCode = result.ErrorCode switch
            {
                "SLUG_TAKEN" => StatusCodes.Status409Conflict,
                "EMAIL_EXISTS" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            };

            return StatusCode(statusCode, new ErrorResponse
            {
                Error = result.ErrorCode ?? "REGISTRATION_FAILED",
                Message = result.ErrorMessage ?? "Registration failed."
            });
        }

        _logger.LogInformation(
            "Organization {OrganizationId} registered with slug {Slug}",
            result.OrganizationId, result.Slug);

        return StatusCode(StatusCodes.Status201Created, new RegisterResponse
        {
            Success = true,
            OrganizationId = result.OrganizationId!.Value,
            UserId = result.UserId!.Value,
            Slug = result.Slug!,
            Message = result.Message!,
            RequiresEmailVerification = result.RequiresEmailVerification,
            EmailSent = result.EmailSent
        });
    }

    /// <summary>
    /// Verify email address and activate account
    /// </summary>
    [HttpPost("verify")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(VerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request)
    {
        var command = new VerifyEmailCommand
        {
            OrganizationId = request.OrganizationId,
            Token = request.Token
        };

        var result = await _mediator.Send(command);

        if (!result.Success)
        {
            var statusCode = result.ErrorCode switch
            {
                "ORG_NOT_FOUND" => StatusCodes.Status404NotFound,
                "ALREADY_VERIFIED" => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status400BadRequest
            };

            return StatusCode(statusCode, new ErrorResponse
            {
                Error = result.ErrorCode ?? "VERIFICATION_FAILED",
                Message = result.ErrorMessage ?? "Verification failed."
            });
        }

        return Ok(new VerifyResponse
        {
            Success = true,
            Slug = result.Slug!,
            Message = result.Message!,
            RedirectUrl = result.RedirectUrl!
        });
    }

    /// <summary>
    /// Resend verification email
    /// </summary>
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationRequest request)
    {
        var command = new ResendVerificationCommand
        {
            Email = request.Email
        };

        var result = await _mediator.Send(command);

        // Always return success for security (don't reveal if email exists)
        return Ok(new MessageResponse
        {
            Message = result.Message ?? "If an account exists with this email, a verification link has been sent."
        });
    }

    /// <summary>
    /// Check if a slug is available
    /// </summary>
    [HttpGet("check-slug/{slug}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SlugAvailabilityResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckSlugAvailability(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Ok(new SlugAvailabilityResponse
            {
                Slug = slug ?? "",
                IsAvailable = false,
                Message = "Slug cannot be empty."
            });
        }

        var isAvailable = await _provisioningService.ValidateSlugAvailabilityAsync(slug);

        return Ok(new SlugAvailabilityResponse
        {
            Slug = slug,
            IsAvailable = isAvailable,
            Message = isAvailable
                ? "This slug is available."
                : "This slug is already in use or reserved."
        });
    }

    /// <summary>
    /// Generate a suggested slug from organization name
    /// </summary>
    [HttpGet("suggest-slug")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SlugSuggestionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SuggestSlug([FromQuery] string organizationName)
    {
        if (string.IsNullOrWhiteSpace(organizationName))
        {
            return BadRequest(new ErrorResponse
            {
                Error = "INVALID_NAME",
                Message = "Organization name is required."
            });
        }

        var suggestedSlug = await _provisioningService.GenerateUniqueSlugAsync(organizationName);

        return Ok(new SlugSuggestionResponse
        {
            OrganizationName = organizationName,
            SuggestedSlug = suggestedSlug
        });
    }
}

#region Request/Response Models

/// <summary>
/// Registration request model
/// </summary>
public record SendPhoneCodeRequest
{
    public string? Phone { get; init; }
}

public record VerifyPhoneCodeRequest
{
    public string? Phone { get; init; }
    public string? Code { get; init; }
}

public record RegisterRequest
{
    /// <summary>
    /// Proof token from the SMS verification step. Without it the phone is treated as unverified,
    /// no matter what the client claims.
    /// </summary>
    public string? PhoneVerificationToken { get; init; }

    /// <summary>
    /// Honeypot. The field is hidden from people by CSS, so anything in it means a script filled
    /// the form. Named to look worth completing to a bot scanning for plausible inputs.
    /// </summary>
    public string? ContactReference { get; init; }

    /// <summary>
    /// When the form was rendered, so an implausibly fast submission can be spotted. Advisory only:
    /// a client can lie about it, which is why it contributes to a score rather than blocking.
    /// </summary>
    public DateTime? FormRenderedAt { get; init; }

    /// <summary>
    /// The form told them an organization at their email domain already uses Q-Mgr — by name,
    /// because that school published the domain itself — and they chose to register a new one.
    /// Raises the risk score; never blocks. A second campus is a real thing (2026-09-20).
    /// </summary>
    public bool AcknowledgedExistingOrganization { get; init; }

    /// <summary>The name they were shown, for the reviewer. Never trusted as a fact about the caller.</summary>
    public string? AcknowledgedOrganizationName { get; init; }

    /// <summary>Organization/Company name</summary>
    public string OrganizationName { get; init; } = string.Empty;

    /// <summary>
    /// Optional: what the school calls its app ("MARYHILL Dashboard", "Dashboard", anything). Shown once
    /// white-labelling is on; until then the app is ours. Validated by ProductBrand.ValidateBrandName.
    /// </summary>
    public string? BrandName { get; init; }

    /// <summary>Desired URL slug (optional)</summary>
    public string? Slug { get; init; }

    /// <summary>Admin email address</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>Password</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Confirm password</summary>
    public string ConfirmPassword { get; init; } = string.Empty;

    /// <summary>Admin first name</summary>
    public string FirstName { get; init; } = string.Empty;

    /// <summary>Admin last name</summary>
    public string LastName { get; init; } = string.Empty;

    /// <summary>Admin phone number</summary>
    public string? Phone { get; init; }

    /// <summary>Organization contact phone</summary>
    public string? ContactPhone { get; init; }

    /// <summary>Industry type</summary>
    public string? IndustryType { get; init; }

    /// <summary>Preferred currency (USD, UGX)</summary>
    public string? PreferredCurrency { get; init; }

    /// <summary>Accept terms and conditions</summary>
    public bool AcceptTerms { get; init; }

    /// <summary>Referral code if applicable</summary>
    public string? ReferralCode { get; init; }

    /// <summary>Modules picked in the module-picker step — at least one required.</summary>
    public List<string>? SelectedModuleCodes { get; init; }
}

/// <summary>
/// Registration response model
/// </summary>
public record RegisterResponse
{
    public bool Success { get; init; }
    public Guid OrganizationId { get; init; }
    public Guid UserId { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool RequiresEmailVerification { get; init; }
    public bool EmailSent { get; init; }
}

/// <summary>
/// Email verification request model
/// </summary>
public record VerifyEmailRequest
{
    public Guid OrganizationId { get; init; }
    public string Token { get; init; } = string.Empty;
}

/// <summary>
/// Verification response model
/// </summary>
public record VerifyResponse
{
    public bool Success { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string RedirectUrl { get; init; } = string.Empty;
}

/// <summary>
/// Resend verification request model
/// </summary>
public record ResendVerificationRequest
{
    public string Email { get; init; } = string.Empty;
}

/// <summary>
/// Slug availability response
/// </summary>
public record SlugAvailabilityResponse
{
    public string Slug { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Slug suggestion response
/// </summary>
public record SlugSuggestionResponse
{
    public string OrganizationName { get; init; } = string.Empty;

    /// <summary>
    /// Optional: what the school calls its app ("MARYHILL Dashboard", "Dashboard", anything). Shown once
    /// white-labelling is on; until then the app is ours. Validated by ProductBrand.ValidateBrandName.
    /// </summary>
    public string? BrandName { get; init; }
    public string SuggestedSlug { get; init; } = string.Empty;
}

/// <summary>
/// Simple message response
/// </summary>
public record MessageResponse
{
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Error response model
/// </summary>
public record ErrorResponse
{
    public string Error { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

#endregion
