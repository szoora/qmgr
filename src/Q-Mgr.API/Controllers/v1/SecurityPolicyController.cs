using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.API.Domain.Entities;
using QMgr.Domain.Constants;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/security-policy")]
[Authorize]
public class SecurityPolicyController : ControllerBase
{
    private readonly IPasswordValidationService _passwordValidationService;
    private readonly ILogger<SecurityPolicyController> _logger;

    public SecurityPolicyController(
        IPasswordValidationService passwordValidationService,
        ILogger<SecurityPolicyController> logger)
    {
        _passwordValidationService = passwordValidationService;
        _logger = logger;
    }

    /// <summary>
    /// Get current security settings including password policy (Platform Admin only)
    /// </summary>
    [HttpGet("security-settings")]
    [RequirePermission(Permissions.PlatformAdmin)]
    [ProducesResponseType(typeof(SecuritySettings), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSecuritySettings()
    {
        var settings = await _passwordValidationService.GetSecuritySettingsAsync();
        return Ok(settings);
    }

    /// <summary>
    /// Get current password policy (Platform Admin only)
    /// </summary>
    [HttpGet("password-policy")]
    [RequirePermission(Permissions.PlatformAdmin)]
    [ProducesResponseType(typeof(PasswordPolicySettings), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPasswordPolicy()
    {
        var policy = await _passwordValidationService.GetPasswordPolicyAsync();
        return Ok(policy);
    }

    /// <summary>
    /// Update password policy (Platform Admin only)
    /// </summary>
    [HttpPut("password-policy")]
    [RequirePermission(Permissions.PlatformAdmin)]
    [ProducesResponseType(typeof(PasswordPolicySettings), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePasswordPolicy([FromBody] PasswordPolicySettings policy)
    {
        // Validate policy settings
        if (policy.MinimumLength < 6)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid policy",
                Detail = "Minimum password length cannot be less than 6 characters.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (policy.MaximumLength < policy.MinimumLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid policy",
                Detail = "Maximum password length cannot be less than minimum length.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (policy.MaximumLength > 128)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid policy",
                Detail = "Maximum password length cannot exceed 128 characters (BCrypt limitation).",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (policy.EnableAccountLockout && policy.MaxFailedAttempts < 3)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid policy",
                Detail = "Maximum failed attempts must be at least 3 if account lockout is enabled.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (policy.EnablePasswordHistory && policy.PasswordHistoryCount < 1)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid policy",
                Detail = "Password history count must be at least 1 if password history is enabled.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            var currentUserId = GetCurrentUserId() ?? throw new UnauthorizedAccessException();
            await _passwordValidationService.UpdatePasswordPolicyAsync(policy, currentUserId);

            _logger.LogInformation("Password policy updated by user {UserId}", currentUserId);

            return Ok(policy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating password policy");
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Error updating password policy",
                Detail = "An unexpected error occurred while updating the password policy.",
                Status = StatusCodes.Status500InternalServerError
            });
        }
    }

    /// <summary>
    /// Validate a password against current policy (for client-side validation)
    /// </summary>
    [HttpPost("validate-password")]
    [AllowAnonymous] // Allow for registration form validation
    [ProducesResponseType(typeof(PasswordValidationResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidatePassword([FromBody] PasswordValidationRequest request)
    {
        var result = await _passwordValidationService.ValidatePasswordAsync(
            request.Password,
            request.Username,
            request.Email);

        return Ok(new PasswordValidationResponse
        {
            IsValid = result.IsValid,
            Errors = result.Errors
        });
    }

    private Guid? GetCurrentUserId()
    {
        // BUG FIX: same root cause as NotificationsController's GetCurrentUserId (see Phase
        // 13b in docs/TASK_TRACKER.md) — the default JWT inbound-claim mapping renames "sub"
        // to ClaimTypes.NameIdentifier, so a literal "sub" lookup never matches a real token.
        // This made UpdatePasswordPolicy always throw UnauthorizedAccessException -> 500 for
        // every caller, including a legitimate Platform Admin.
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst("userId")?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

public class PasswordValidationRequest
{
    public string Password { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Email { get; set; }
}

public class PasswordValidationResponse
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
}
