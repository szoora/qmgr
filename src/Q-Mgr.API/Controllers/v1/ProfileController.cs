using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Infrastructure.Data;
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
    private readonly ILogger<ProfileController> _logger;

    public ProfileController(
        QMgrDbContext dbContext,
        IPasswordValidationService passwordValidation,
        ILogger<ProfileController> logger)
    {
        _dbContext = dbContext;
        _passwordValidation = passwordValidation;
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

        return Ok(user);
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
            user.Phone = request.Phone;

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
            EmployeeNumber = user.EmployeeNumber,
            Role = user.Role.Name,
            AssignedBranchId = user.AssignedBranchId,
            AssignedBranchName = user.AssignedBranch?.Name,
            LastLogin = user.LastLogin,
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Changes the current user's password
    /// </summary>
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

        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
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

        var user = await _dbContext.Users.FindAsync(currentUserId);

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = "Profile not found.",
                Status = StatusCodes.Status404NotFound
            });

        // Validate new password against security policy
        var passwordValidation = await _passwordValidation.ValidatePasswordAsync(
            request.NewPassword,
            user.Username,
            user.Email);

        if (!passwordValidation.IsValid)
            return BadRequest(new ProblemDetails
            {
                Title = "Password validation failed",
                Detail = passwordValidation.ErrorMessage,
                Status = StatusCodes.Status400BadRequest
            });

        // Verify current password
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid password",
                Detail = "Current password is incorrect.",
                Status = StatusCodes.Status400BadRequest
            });

        // Update password
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.RefreshToken = null; // Invalidate refresh tokens
        user.RefreshTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} changed their password", currentUserId);

        return Ok(new { message = "Password changed successfully" });
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(userIdClaim, out var userId))
            return userId;
        return null;
    }
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

#endregion
