using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;
using QMgr.Infrastructure.Services.Storage;
using System.Security.Claims;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/profile")]
[Authorize]
[Produces("application/json")]
public class ProfileController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly IPasswordValidationService _passwordValidation;
    private readonly IActivityLogger _activity;
    private readonly IPhoneVerificationService _phoneVerification;
    private readonly IMediaStorageService _mediaStorage;
    private readonly ILogger<ProfileController> _logger;

    private const long MaxPhotoSizeBytes = 5 * 1024 * 1024; // a headshot, not a document

    public ProfileController(
        QMgrDbContext dbContext,
        IPasswordValidationService passwordValidation,
        IActivityLogger activity,
        IPhoneVerificationService phoneVerification,
        IMediaStorageService mediaStorage,
        ILogger<ProfileController> logger)
    {
        _dbContext = dbContext;
        _passwordValidation = passwordValidation;
        _activity = activity;
        _phoneVerification = phoneVerification;
        _mediaStorage = mediaStorage;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current user's profile
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetProfile()
    {
        // SECURITY: Always get user ID from JWT claims, never from query parameters
        var currentUserId = GetCurrentUserId();

        if (currentUserId == null)
            return Unauthorized(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "User ID not found in authentication token.",
                Status = StatusCodes.Status401Unauthorized
            });

        var user = await _dbContext.Users
            .Include(u => u.AssignedBranch)
            .Where(u => u.Id == currentUserId)
            .Select(u => new ProfileDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                FullName = (u.FirstName ?? "") + " " + (u.LastName ?? ""),
                Phone = u.Phone,
                PhoneVerifiedAt = u.PhoneVerifiedAt,
                PhotoUrl = u.PhotoUrl,
                EmployeeNumber = u.EmployeeNumber,
                Role = u.Role.Name,
                AssignedBranchId = u.AssignedBranchId,
                AssignedBranchName = u.AssignedBranch != null ? u.AssignedBranch.Name : null,
                LastLogin = u.LastLogin,
                CreatedAt = u.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = "Profile not found.",
                Status = StatusCodes.Status404NotFound
            });

        return Ok(user with { PhotoUrl = UploadLinks.Sign(user.PhotoUrl) });
    }

    /// <summary>
    /// Updates the current user's profile
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        // SECURITY: Always get user ID from JWT claims, never from query parameters
        var currentUserId = GetCurrentUserId();

        if (currentUserId == null)
            return Unauthorized(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "User ID not found in authentication token.",
                Status = StatusCodes.Status401Unauthorized
            });

        var user = await _dbContext.Users
            .Include(u => u.AssignedBranch)
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == currentUserId);

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = "Profile not found.",
                Status = StatusCodes.Status404NotFound
            });

        // Validate email uniqueness if changed
        if (!string.IsNullOrWhiteSpace(request.Email) && request.Email.ToLowerInvariant() != user.Email)
        {
            var emailExists = await _dbContext.Users.AnyAsync(u => u.Email == request.Email.ToLowerInvariant() && u.Id != currentUserId);
            if (emailExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Email already in use",
                    Detail = $"The email '{request.Email}' is already associated with another account.",
                    Status = StatusCodes.Status400BadRequest
                });
            user.Email = request.Email.ToLowerInvariant();
        }

        // Update allowed fields
        if (!string.IsNullOrWhiteSpace(request.FirstName))
            user.FirstName = request.FirstName;
        if (!string.IsNullOrWhiteSpace(request.LastName))
            user.LastName = request.LastName;
        if (request.Phone != null) // Allow empty to clear
        {
            // A confirmation belongs to a number, not to a person: a changed number is unconfirmed
            // again (the onboarding checklist, plan §12.3, asks for it once more).
            if (RegistrationIdentity.NormalizePhone(request.Phone) != RegistrationIdentity.NormalizePhone(user.Phone))
                user.PhoneVerifiedAt = null;
            user.Phone = request.Phone;
        }

        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} updated their profile", currentUserId);

        return Ok(new ProfileDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            FullName = user.FullName,
            Phone = user.Phone,
            PhoneVerifiedAt = user.PhoneVerifiedAt,
            PhotoUrl = UploadLinks.Sign(user.PhotoUrl),
            EmployeeNumber = user.EmployeeNumber,
            Role = user.Role.Name,
            AssignedBranchId = user.AssignedBranchId,
            AssignedBranchName = user.AssignedBranch?.Name,
            LastLogin = user.LastLogin,
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Changes the current user's password.
    /// </summary>
    /// <remarks>
    /// Two callers. An ordinary one proves the current password. One holding a password-change-only
    /// token (signed in with a temporary password, plan §12.3) has just proved it at sign-in, so the
    /// token is the proof and only the new password is asked for. Either way the new password passes
    /// the policy and the blocklist — including the organization's name and, for a temporary password,
    /// the temporary password itself — and the change revokes every other session.
    /// </remarks>
    [HttpPut("password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        // SECURITY: Always get user ID from JWT claims, never from query parameters
        var currentUserId = GetCurrentUserId();

        if (currentUserId == null)
            return Unauthorized(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "User ID not found in authentication token.",
                Status = StatusCodes.Status401Unauthorized
            });

        var changeOnlyToken = User.HasClaim(TemporaryPasswords.ChangeOnlyClaim, "true");

        if (!changeOnlyToken && string.IsNullOrWhiteSpace(request.CurrentPassword))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Current password is required.",
                Status = StatusCodes.Status400BadRequest
            });

        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "New password is required.",
                Status = StatusCodes.Status400BadRequest
            });

        if (request.NewPassword != request.ConfirmPassword)
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "New password and confirmation do not match.",
                Status = StatusCodes.Status400BadRequest
            });

        var user = await _dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == currentUserId && u.IsActive);

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = "Profile not found.",
                Status = StatusCodes.Status404NotFound
            });

        // A change-only token is only honoured while the temporary password is still live: once it has
        // been changed (or has expired) the token is spent.
        if (changeOnlyToken && (!user.MustChangePassword || user.TemporaryPasswordExpiresAt is null || user.TemporaryPasswordExpiresAt <= DateTime.UtcNow))
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign in again",
                Detail = "This sign-in can no longer change the password. Sign in again.",
                Status = StatusCodes.Status401Unauthorized
            });

        var organizationName = await _dbContext.Organizations.IgnoreQueryFilters()
            .Where(o => o.Id == user.OrganizationId).Select(o => o.Name).FirstOrDefaultAsync();

        // Validate new password against security policy
        var passwordValidation = await _passwordValidation.ValidatePasswordAsync(
            request.NewPassword,
            user.Username,
            user.Email,
            organizationName);

        if (!passwordValidation.IsValid)
            return BadRequest(new ProblemDetails
            {
                Title = "Password validation failed",
                Detail = passwordValidation.ErrorMessage,
                Status = StatusCodes.Status400BadRequest
            });

        if (!changeOnlyToken && !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid password",
                Detail = "Current password is incorrect.",
                Status = StatusCodes.Status400BadRequest
            });

        // The new password must not be the old one — for a temporary password that is the NIST rule
        // ("the temporary password itself"), checked against the hash because the value is never stored.
        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, user.PasswordHash))
            return BadRequest(new ProblemDetails
            {
                Title = "Password validation failed",
                Detail = changeOnlyToken
                    ? "Choose a password different from the temporary one you were given."
                    : "Choose a password different from your current one.",
                Status = StatusCodes.Status400BadRequest
            });

        var wasTemporary = user.MustChangePassword;

        // Update password
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.RefreshToken = null; // Invalidate refresh tokens — every other session ends
        user.RefreshTokenExpiry = null;
        user.MustChangePassword = false;
        user.TemporaryPasswordExpiresAt = null;
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} changed their password{Temporary}", currentUserId, wasTemporary ? " (replacing a temporary password)" : "");

        await _activity.RecordAsync(ActivityActions.PasswordChanged, "User", user.Id, user.Id,
            wasTemporary ? $"{DisplayName(user)} set their own password (first sign-in)" : $"{DisplayName(user)} changed their password",
            organizationId: user.OrganizationId, branchId: user.AssignedBranchId, actorUserId: user.Id);

        return Ok(new { message = "Password changed successfully", firstSignIn = wasTemporary });
    }

    /// <summary>
    /// Sends a one-time code to the number on the caller's profile (the onboarding checklist's "confirm
    /// your phone", plan §12.3). Through the organization's own SMS configuration.
    /// </summary>
    [HttpPost("phone/send-code")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendPhoneCode()
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null) return Unauthorized();

        var user = await _dbContext.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId && u.IsActive);
        if (user == null) return NotFound();
        if (string.IsNullOrWhiteSpace(user.Phone))
            return BadRequest(new ProblemDetails { Title = "Add a phone number to your profile first", Status = StatusCodes.Status400BadRequest });

        var result = await _phoneVerification.SendCodeAsync(user.OrganizationId, user.Phone);
        return result.Sent
            ? Ok(new { sent = true, expiresInSeconds = result.ExpiresInSeconds })
            : BadRequest(new ProblemDetails { Title = "The code could not be sent", Detail = result.Message, Status = StatusCodes.Status400BadRequest });
    }

    /// <summary>Checks the code and marks the profile's number confirmed.</summary>
    [HttpPost("phone/verify-code")]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyPhoneCode([FromBody] VerifyProfilePhoneRequest request)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null) return Unauthorized();

        var user = await _dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == currentUserId && u.IsActive);
        if (user == null) return NotFound();
        if (string.IsNullOrWhiteSpace(user.Phone))
            return BadRequest(new ProblemDetails { Title = "Add a phone number to your profile first", Status = StatusCodes.Status400BadRequest });

        var result = await _phoneVerification.VerifyCodeAsync(user.Phone, request.Code ?? string.Empty);
        if (!result.Verified)
            return BadRequest(new ProblemDetails { Title = "That code did not work", Detail = result.Message, Status = StatusCodes.Status400BadRequest });

        await _phoneVerification.ConsumeProofAsync(result.ProofToken);
        user.PhoneVerifiedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.PhoneConfirmed, "User", user.Id, user.Id,
            $"{DisplayName(user)} confirmed their phone number",
            organizationId: user.OrganizationId, branchId: user.AssignedBranchId, actorUserId: user.Id);

        return Ok(new { verified = true, phoneVerifiedAt = user.PhoneVerifiedAt });
    }

    /// <summary>
    /// Uploads the caller's own profile photograph. Stored in the gated upload store and classified as
    /// UploadOwnerKind.StaffPhoto: readable by signed-in staff of the same organization, never public.
    /// </summary>
    [HttpPost("photo")]
    [RequestSizeLimit(MaxPhotoSizeBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadPhoto(IFormFile file)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null) return Unauthorized();

        if (file == null || file.Length == 0)
            return BadRequest(new ProblemDetails { Title = "No photo was provided", Status = StatusCodes.Status400BadRequest });
        if (file.Length > MaxPhotoSizeBytes)
            return BadRequest(new ProblemDetails { Title = $"A photo can be at most {MaxPhotoSizeBytes / 1024 / 1024}MB", Status = StatusCodes.Status400BadRequest });
        if (!(file.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ProblemDetails { Title = "Only image files are accepted", Status = StatusCodes.Status400BadRequest });

        var user = await _dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == currentUserId && u.IsActive);
        if (user == null) return NotFound();

        await using var stream = file.OpenReadStream();
        var upload = await _mediaStorage.UploadAsync(stream, file.FileName, file.ContentType!);
        if (!upload.Success)
        {
            _logger.LogError("Profile photo upload failed for {UserId}: {Error}", currentUserId, upload.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "The photo could not be stored" });
        }

        user.PhotoUrl = UploadLinks.Strip(upload.FileUrl);
        user.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.ProfilePhotoChanged, "User", user.Id, user.Id,
            $"{DisplayName(user)} changed their profile photo",
            organizationId: user.OrganizationId, branchId: user.AssignedBranchId, actorUserId: user.Id);

        return Ok(new { photoUrl = UploadLinks.Sign(user.PhotoUrl) });
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(userIdClaim, out var userId))
            return userId;
        return null;
    }

    private static string DisplayName(QMgr.Domain.Entities.Identity.User user)
        => string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
}

#region DTOs

public record ProfileDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public DateTime? PhoneVerifiedAt { get; init; }
    public string? PhotoUrl { get; init; }
    public string? EmployeeNumber { get; init; }
    public string Role { get; init; } = string.Empty;
    public Guid? AssignedBranchId { get; init; }
    public string? AssignedBranchName { get; init; }
    public DateTime? LastLogin { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record UpdateProfileRequest
{
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Phone { get; init; }
}

public record ChangePasswordRequest
{
    public string CurrentPassword { get; init; } = string.Empty;
    public string NewPassword { get; init; } = string.Empty;
    public string ConfirmPassword { get; init; } = string.Empty;
}

public record VerifyProfilePhoneRequest
{
    public string? Code { get; init; }
}

#endregion
