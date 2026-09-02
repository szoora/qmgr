using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Interfaces;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly QMgrDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IPasswordValidationService _passwordValidationService;
    private readonly ILogger<AuthController> _logger;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IEmailSender _emailSender;
    private readonly IPlatformSettingsService _platformSettingsService;

    public AuthController(
        IUnitOfWork unitOfWork,
        QMgrDbContext dbContext,
        IConfiguration configuration,
        IPasswordValidationService passwordValidationService,
        ILogger<AuthController> logger,
        ITenantContextAccessor tenantAccessor,
        IEmailSender emailSender,
        IPlatformSettingsService platformSettingsService)
    {
        _unitOfWork = unitOfWork;
        _dbContext = dbContext;
        _configuration = configuration;
        _passwordValidationService = passwordValidationService;
        _logger = logger;
        _tenantAccessor = tenantAccessor;
        _emailSender = emailSender;
        _platformSettingsService = platformSettingsService;
    }

    /// <summary>
    /// Step 1: Identify user by email OR username and return tenant info.
    /// Supports both subdomain-scoped and global lookup.
    /// </summary>
    [HttpPost("identify")]
    [ProducesResponseType(typeof(IdentifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> IdentifyUser([FromBody] IdentifyRequest request)
    {
        var identifier = request.Email.Trim().ToLowerInvariant();
        var tenantContext = _tenantAccessor.TenantContext;

        // Build query — the "Email" field on the request is really a generic identifier: it
        // accepts either the user's email or their username.
        var query = _dbContext.Users
            .Include(u => u.Organization)
            .Where(u => (u.Email.ToLower() == identifier || u.Username.ToLower() == identifier) && u.IsActive);

        // If subdomain is resolved, scope to that organization only
        if (tenantContext.IsResolved)
        {
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
            _logger.LogDebug("Tenant-scoped identify: {Identifier} in org {OrgId}", identifier, tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
        {
            var message = tenantContext.IsResolved
                ? $"No account found with this email/username in {tenantContext.TenantSlug} organization"
                : "No account found with this email or username";

            _logger.LogWarning("User identification failed for identifier: {Identifier}, Tenant: {Tenant}",
                identifier, tenantContext.TenantSlug ?? "none");
            return NotFound(new { message });
        }

        _logger.LogInformation("User identified: {Email}, Organization: {OrgName}", user.Email, user.Organization?.Name);

        return Ok(new IdentifyResponse
        {
            Email = user.Email,
            OrganizationId = user.OrganizationId,
            OrganizationName = user.Organization?.Name ?? "Unknown",
            OrganizationSlug = user.Organization?.Slug ?? "",
            HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
            SsoEnabled = false // TODO: Check if organization has SSO configured
        });
    }

    /// <summary>
    /// Step 2: User login with email or username, plus password.
    /// Supports both subdomain-scoped and organization-targeted login.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var identifier = request.Email.Trim().ToLowerInvariant();
        var tenantContext = _tenantAccessor.TenantContext;

        // Build query — "Email" on the request is a generic identifier: email or username.
        var query = _dbContext.Users
            .Include(u => u.Role)
            .ThenInclude(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .Include(u => u.Organization)
            .Where(u => (u.Email.ToLower() == identifier || u.Username.ToLower() == identifier) && u.IsActive);

        // Priority 1: Subdomain-scoped (tenant resolved from URL)
        if (tenantContext.IsResolved)
        {
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
            _logger.LogDebug("Subdomain-scoped login: {Identifier} in org {OrgId}", identifier, tenantContext.OrganizationId);
        }
        // Priority 2: Organization ID from frontend (two-step login)
        else if (request.OrganizationId.HasValue)
        {
            query = query.Where(u => u.OrganizationId == request.OrganizationId.Value);
            _logger.LogDebug("Organization-targeted login: {Identifier} in org {OrgId}", identifier, request.OrganizationId);
        }
        // Priority 3: Global lookup (not recommended in production, for dev only)
        else
        {
            _logger.LogWarning("Global login attempt (no tenant context): {Identifier}", identifier);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
        {
            _logger.LogWarning("Login failed for identifier: {Identifier}, Tenant: {Tenant}",
                identifier, tenantContext.TenantSlug ?? request.OrganizationId?.ToString() ?? "global");
            return Unauthorized(new { message = "Invalid email or password" });
        }

        var securitySettings = await _passwordValidationService.GetSecuritySettingsAsync();
        var lockoutPolicy = securitySettings.PasswordPolicy;

        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
        {
            _logger.LogWarning("Login blocked — account locked out for user: {Email} until {LockoutEnd}", user.Email, user.LockoutEnd);
            var minutesRemaining = (int)Math.Ceiling((user.LockoutEnd.Value - DateTime.UtcNow).TotalMinutes);
            return Unauthorized(new { message = $"Account locked due to too many failed login attempts. Try again in {minutesRemaining} minute(s)." });
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Invalid password for user: {Email}", user.Email);

            if (lockoutPolicy.EnableAccountLockout)
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= lockoutPolicy.MaxFailedAttempts)
                {
                    user.LockoutEnd = DateTime.UtcNow.AddMinutes(lockoutPolicy.LockoutDurationMinutes);
                    _logger.LogWarning("Account locked out for user: {Email} after {Attempts} failed attempts", user.Email, user.FailedLoginAttempts);
                }
                await _dbContext.SaveChangesAsync();
            }

            return Unauthorized(new { message = "Invalid email or password" });
        }

        var token = GenerateJwtToken(user);
        var refreshToken = GenerateRefreshToken();

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        user.LastLogin = DateTime.UtcNow;
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {Username} logged in successfully", user.Username);

        // Get user's permissions
        var permissions = user.Role.RolePermissions
            .Select(rp => rp.Permission.Code)
            .ToList();

        return Ok(new LoginResponse
        {
            AccessToken = token,
            RefreshToken = refreshToken,
            ExpiresIn = int.Parse(_configuration["JWT:ExpiryMinutes"] ?? "60") * 60,
            User = new UserInfo
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                RoleId = user.RoleId,
                RoleCode = user.Role.Code,
                RoleName = user.Role.Name,
                RoleColor = user.Role.Color,
                OrganizationId = user.OrganizationId,
                OrganizationName = user.Organization?.Name,
                BranchId = user.AssignedBranchId,
                Permissions = permissions
            }
        });
    }

    /// <summary>
    /// API client token endpoint (OAuth2 client credentials)
    /// </summary>
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetToken([FromBody] ClientCredentialsRequest request)
    {
        var client = await _unitOfWork.ApiClients.FirstOrDefaultAsync(
            c => c.ClientId == request.ClientId && c.IsActive);

        if (client == null)
        {
            return Unauthorized(new { error = "invalid_client", error_description = "Client not found" });
        }

        if (!BCrypt.Net.BCrypt.Verify(request.ClientSecret, client.ClientSecretHash))
        {
            return Unauthorized(new { error = "invalid_client", error_description = "Invalid client credentials" });
        }

        var token = GenerateApiClientToken(client);

        client.LastUsedAt = DateTime.UtcNow;
        await _unitOfWork.ApiClients.UpdateAsync(client);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("API client {ClientId} obtained token", client.ClientId);

        return Ok(new TokenResponse
        {
            AccessToken = token,
            TokenType = "Bearer",
            ExpiresIn = 3600
        });
    }

    /// <summary>
    /// Refresh access token
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        var user = await _dbContext.Users
            .Include(u => u.Role)
            .ThenInclude(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u =>
                u.RefreshToken == request.RefreshToken &&
                u.RefreshTokenExpiry > DateTime.UtcNow &&
                u.IsActive);

        if (user == null)
        {
            return Unauthorized(new { message = "Invalid or expired refresh token" });
        }

        var token = GenerateJwtToken(user);
        var newRefreshToken = GenerateRefreshToken();

        user.RefreshToken = newRefreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);

        await _dbContext.SaveChangesAsync();

        // Get user's permissions
        var permissions = user.Role.RolePermissions
            .Select(rp => rp.Permission.Code)
            .ToList();

        return Ok(new LoginResponse
        {
            AccessToken = token,
            RefreshToken = newRefreshToken,
            ExpiresIn = int.Parse(_configuration["JWT:ExpiryMinutes"] ?? "60") * 60,
            User = new UserInfo
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                RoleId = user.RoleId,
                RoleCode = user.Role.Code,
                RoleName = user.Role.Name,
                RoleColor = user.Role.Color,
                OrganizationId = user.OrganizationId,
                OrganizationName = user.Organization?.Name,
                BranchId = user.AssignedBranchId,
                Permissions = permissions
            }
        });
    }

    /// <summary>
    /// Get current user info (requires authentication)
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)
            ?? User.FindFirst("sub");

        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return Unauthorized(new { message = "Invalid token" });
        }

        var user = await _dbContext.Users
            .Include(u => u.Role)
            .ThenInclude(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);

        if (user == null)
        {
            return Unauthorized(new { message = "User not found" });
        }

        var permissions = user.Role.RolePermissions
            .Select(rp => rp.Permission.Code)
            .ToList();

        return Ok(new UserInfo
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            FullName = user.FullName,
            RoleId = user.RoleId,
            RoleCode = user.Role.Code,
            RoleName = user.Role.Name,
            RoleColor = user.Role.Color,
            OrganizationId = user.OrganizationId,
            OrganizationName = user.Organization?.Name,
            BranchId = user.AssignedBranchId,
            Permissions = permissions
        });
    }

    private string GenerateJwtToken(QMgr.Domain.Entities.Identity.User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Secret"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.Code), // Use role code from database
            new Claim("role_id", user.RoleId.ToString()),
            new Claim("org_id", user.OrganizationId.ToString()),
            new Claim("branch_id", user.AssignedBranchId?.ToString() ?? "")
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["JWT:Issuer"],
            audience: _configuration["JWT:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(int.Parse(_configuration["JWT:ExpiryMinutes"] ?? "60")),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GenerateApiClientToken(QMgr.Domain.Entities.Integration.ApiClient client)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Secret"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, client.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("client_id", client.ClientId),
            new("org_id", client.OrganizationId.ToString()),
            new("system_type", client.SystemType ?? "custom"),
            // Same marker ApiKeyAuthenticationMiddleware sets for the X-API-Key path — without it,
            // PermissionAuthorizationHandler falls through to its user-permission lookup, treating
            // `sub` (this ApiClient's Id) as a User.Id that never matches, silently denying every
            // [RequirePermission] endpoint regardless of the client's configured scopes. Found live
            // 2026-08-31 testing the ERP Bridge: token issuance succeeded but every scoped call 403'd.
            new("auth_method", "api_key")
        };

        // Add scopes
        if (client.Scopes != null)
        {
            foreach (var scope in client.Scopes)
            {
                claims.Add(new Claim("scope", scope));
            }
        }

        var token = new JwtSecurityToken(
            issuer: _configuration["JWT:Issuer"],
            audience: _configuration["JWT:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// Step 1 of self-service password reset: requests a reset link by email. Always returns a
    /// generic success response whether or not the email matches an account (and whether or not
    /// sending the email actually succeeds) - same "don't leak which emails are registered"
    /// convention as ResendVerificationCommandHandler.
    /// </summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        const string genericMessage = "If an account exists for that email address, we've sent a link to reset the password.";

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Ok(new { message = genericMessage });
        }

        try
        {
            var email = request.Email.Trim().ToLowerInvariant();
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email && u.IsActive);

            if (user != null)
            {
                var token = GenerateRefreshToken();
                user.PasswordResetToken = token;
                user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
                await _dbContext.SaveChangesAsync();

                await SendPasswordResetEmailAsync(user.Email, user.FirstName, token);
                _logger.LogInformation("Password reset requested for {Email}", user.Email);
            }
        }
        catch (Exception ex)
        {
            // Still return the generic success message - a delivery failure shouldn't tell an
            // attacker anything different than "no such account" would.
            _logger.LogError(ex, "Error processing forgot-password request for {Email}", request.Email);
        }

        return Ok(new { message = genericMessage });
    }

    /// <summary>
    /// Step 2 of self-service password reset: exchanges a valid, unexpired token for a new
    /// password. Also clears the token (single-use) and revokes the existing refresh token, so a
    /// reset forces re-login everywhere rather than leaving an old session silently valid.
    /// </summary>
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] SelfServiceResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Email, token, and new password are all required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (request.NewPassword != request.ConfirmPassword)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Passwords do not match.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email && u.IsActive);

        // Deliberately the same "invalid or expired" message whether the email doesn't exist,
        // the token doesn't match, or it's simply expired - never confirms which case applies.
        const string invalidTokenMessage = "This reset link is invalid or has expired. Please request a new one.";

        if (user == null ||
            string.IsNullOrEmpty(user.PasswordResetToken) ||
            user.PasswordResetToken != request.Token ||
            user.PasswordResetTokenExpiry == null ||
            user.PasswordResetTokenExpiry < DateTime.UtcNow)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid or expired link",
                Detail = invalidTokenMessage,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var passwordValidation = await _passwordValidationService.ValidatePasswordAsync(
            request.NewPassword, user.Username, user.Email);

        if (!passwordValidation.IsValid)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Password validation failed",
                Detail = passwordValidation.ErrorMessage,
                Status = StatusCodes.Status400BadRequest
            });
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        // Force re-login everywhere - a leaked reset link shouldn't also inherit whatever
        // refresh token an attacker's own prior session might already hold.
        user.RefreshToken = null;
        user.RefreshTokenExpiry = null;
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Password reset completed for {Email}", user.Email);

        return Ok(new { message = "Your password has been reset. You can now sign in with your new password." });
    }

    private async Task SendPasswordResetEmailAsync(string toEmail, string? firstName, string token)
    {
        var saas = await _platformSettingsService.GetSettingsAsync<SaasSettings>("SaaS");
        var baseUrl = (saas?.BaseUrl ?? "https://qmgr.app").TrimEnd('/');
        var resetUrl = $"{baseUrl}/reset-password?email={Uri.EscapeDataString(toEmail)}&token={Uri.EscapeDataString(token)}";

        var subject = "Reset your Q-Mgr password";
        var htmlBody = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Reset your password</title>
</head>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #7a2847;'>Reset your password</h1>
        <p>Hi {firstName},</p>
        <p>We received a request to reset the password on your Q-Mgr account. Click the button below to choose a new one:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='{resetUrl}' style='background-color: #7a2847; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px; display: inline-block;'>Reset Password</a>
        </div>
        <p>Or copy and paste this link into your browser:</p>
        <p style='word-break: break-all; color: #666;'>{resetUrl}</p>
        <p>This link will expire in 1 hour.</p>
        <hr style='border: none; border-top: 1px solid #eee; margin: 30px 0;'>
        <p style='color: #666; font-size: 14px;'>
            If you didn't request this, you can safely ignore this email - your password will not be changed.
        </p>
    </div>
</body>
</html>";

        await _emailSender.SendAsync(toEmail, subject, htmlBody);
    }
}

public record IdentifyRequest
{
    public string Email { get; init; } = string.Empty;
}

public record IdentifyResponse
{
    public string Email { get; init; } = string.Empty;
    public Guid OrganizationId { get; init; }
    public string OrganizationName { get; init; } = string.Empty;
    public string OrganizationSlug { get; init; } = string.Empty;
    public bool HasPassword { get; init; }
    public bool SsoEnabled { get; init; }
    public string? SsoUrl { get; init; }
}

public record LoginRequest
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public Guid? OrganizationId { get; init; }
}

public record LoginResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
    public UserInfo? User { get; init; }
}

public record ClientCredentialsRequest
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
}

public record TokenResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string TokenType { get; init; } = "Bearer";
    public int ExpiresIn { get; init; }
}

public record RefreshTokenRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}

public record ForgotPasswordRequest
{
    public string Email { get; init; } = string.Empty;
}

public record SelfServiceResetPasswordRequest
{
    public string Email { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string NewPassword { get; init; } = string.Empty;
    public string ConfirmPassword { get; init; } = string.Empty;
}
