using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.Application.Commands.Registration;
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
    private readonly ILogger<RegistrationController> _logger;

    public RegistrationController(
        IMediator mediator,
        ITenantProvisioningService provisioningService,
        IPlatformSettingsService platformSettingsService,
        ILogger<RegistrationController> logger)
    {
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
    [HttpGet("trial-info")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTrialInfo()
    {
        var saas = await _platformSettingsService.GetSettingsAsync<QMgr.Domain.Entities.Platform.SaasSettings>("SaaS");
        return Ok(new { trialDays = saas?.TrialDays ?? 14, baseDomain = saas?.BaseDomain ?? "" });
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
        var command = new RegisterOrganizationCommand
        {
            OrganizationName = request.OrganizationName,
            Slug = request.Slug,
            Email = request.Email,
            Password = request.Password,
            ConfirmPassword = request.ConfirmPassword,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Phone = request.Phone,
            ContactPhone = request.ContactPhone,
            IndustryType = request.IndustryType,
            PreferredCurrency = request.PreferredCurrency ?? "USD",
            AcceptTerms = request.AcceptTerms,
            Source = "web",
            ReferralCode = request.ReferralCode,
            SelectedModuleCodes = request.SelectedModuleCodes ?? new()
        };

        var result = await _mediator.Send(command);

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
public record RegisterRequest
{
    /// <summary>Organization/Company name</summary>
    public string OrganizationName { get; init; } = string.Empty;

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
