using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Filters;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/users")]
[Authorize]
[Produces("application/json")]
public class UsersController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UsersController> _logger;
    private readonly IPasswordValidationService _passwordValidation;

    public UsersController(
        QMgrDbContext dbContext,
        ITenantContextAccessor tenantAccessor,
        IMemoryCache cache,
        ILogger<UsersController> logger,
        IPasswordValidationService passwordValidation)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _logger = logger;
        _passwordValidation = passwordValidation;
    }

    /// <summary>
    /// Gets all users (including inactive for admin)
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.UsersView)]
    [ProducesResponseType(typeof(List<UserDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsers([FromQuery] bool includeInactive = false, [FromQuery] Guid? branchId = null)
    {
        var query = _dbContext.Users.AsQueryable();

        // SECURITY: Filter by organization (except for SuperAdmin/PlatformAdmin)
        // Note: JWT role claim uses lowercase code with hyphen (e.g., "super-admin")
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only see users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }
        // SuperAdmin/PlatformAdmin: can see all users (no filter)

        if (!includeInactive)
            query = query.Where(u => u.IsActive);

        if (branchId.HasValue)
            query = query.Where(u => u.AssignedBranchId == branchId.Value);

        var users = await query
            .Include(u => u.Role)
            .Include(u => u.AssignedBranch)
            .Include(u => u.AssignedCounter)
            .OrderBy(u => u.FirstName)
            .ThenBy(u => u.LastName)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                FullName = (u.FirstName ?? "") + " " + (u.LastName ?? ""),
                Phone = u.Phone,
                EmployeeNumber = u.EmployeeNumber,
                RoleId = u.RoleId,
                RoleCode = u.Role.Code,
                RoleName = u.Role.Name,
                RoleColor = u.Role.Color,
                AssignedBranchId = u.AssignedBranchId,
                AssignedBranchName = u.AssignedBranch != null ? u.AssignedBranch.Name : null,
                AssignedCounterId = u.AssignedCounterId,
                AssignedCounterNumber = u.AssignedCounter != null ? u.AssignedCounter.CounterNumber : null,
                IsActive = u.IsActive,
                LastLogin = u.LastLogin,
                CreatedAt = u.CreatedAt
            })
            .ToListAsync();

        return Ok(users);
    }

    /// <summary>
    /// Gets a specific user by ID
    /// </summary>
    [HttpGet("{userId:guid}")]
    [RequirePermission(Permissions.UsersView)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUser(Guid userId)
    {
        // SECURITY: Build organization filter (except for SuperAdmin/PlatformAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users
            .Include(u => u.Role)
            .Include(u => u.AssignedBranch)
            .Include(u => u.AssignedCounter)
            .Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only see users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                FullName = (u.FirstName ?? "") + " " + (u.LastName ?? ""),
                Phone = u.Phone,
                EmployeeNumber = u.EmployeeNumber,
                RoleId = u.RoleId,
                RoleCode = u.Role.Code,
                RoleName = u.Role.Name,
                RoleColor = u.Role.Color,
                AssignedBranchId = u.AssignedBranchId,
                AssignedBranchName = u.AssignedBranch != null ? u.AssignedBranch.Name : null,
                AssignedCounterId = u.AssignedCounterId,
                AssignedCounterNumber = u.AssignedCounter != null ? u.AssignedCounter.CounterNumber : null,
                IsActive = u.IsActive,
                LastLogin = u.LastLogin,
                CreatedAt = u.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        return Ok(user);
    }

    /// <summary>
    /// Creates a new user
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.UsersCreate)]
    [CheckLimit("users")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        // Validation
        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Username is required.",
                Status = StatusCodes.Status400BadRequest
            });

        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Email is required.",
                Status = StatusCodes.Status400BadRequest
            });

        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Password is required.",
                Status = StatusCodes.Status400BadRequest
            });

        // Validate password against platform policy
        var passwordValidation = await _passwordValidation.ValidatePasswordAsync(
            request.Password,
            request.Username,
            request.Email);

        if (!passwordValidation.IsValid)
            return BadRequest(new ProblemDetails
            {
                Title = "Password validation failed",
                Detail = passwordValidation.ErrorMessage,
                Status = StatusCodes.Status400BadRequest
            });

        // SECURITY: Get organization from tenant context (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        Guid organizationId;

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });
            organizationId = tenantContext.OrganizationId;
        }
        else
        {
            // SuperAdmin can create users in specific organizations via request
            // If not specified, use default organization
            organizationId = request.OrganizationId ?? Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // Check for duplicate username within organization
        var existingUsername = await _dbContext.Users
            .AnyAsync(u => u.Username == request.Username && u.OrganizationId == organizationId);
        if (existingUsername)
            return BadRequest(new ProblemDetails
            {
                Title = "Duplicate username",
                Detail = $"A user with username '{request.Username}' already exists in this organization.",
                Status = StatusCodes.Status400BadRequest
            });

        // Check for duplicate email within organization
        var existingEmail = await _dbContext.Users
            .AnyAsync(u => u.Email == request.Email && u.OrganizationId == organizationId);
        if (existingEmail)
            return BadRequest(new ProblemDetails
            {
                Title = "Duplicate email",
                Detail = $"A user with email '{request.Email}' already exists in this organization.",
                Status = StatusCodes.Status400BadRequest
            });

        // Validate role exists
        Role? role = null;
        if (request.RoleId.HasValue)
        {
            role = await _dbContext.Roles.FindAsync(request.RoleId.Value);
            if (role == null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid role",
                    Detail = $"Role with ID '{request.RoleId}' was not found.",
                    Status = StatusCodes.Status400BadRequest
                });
        }
        else
        {
            // Default to Staff role
            role = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Code == RoleCodes.Staff && r.OrganizationId == null);
            if (role == null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Configuration error",
                    Detail = "Default 'staff' role not found. Please contact administrator.",
                    Status = StatusCodes.Status500InternalServerError
                });
        }

        // Validate branch exists within organization if specified
        if (request.AssignedBranchId.HasValue)
        {
            var branchExists = await _dbContext.Branches
                .AnyAsync(b => b.Id == request.AssignedBranchId.Value && b.OrganizationId == organizationId && b.IsActive);
            if (!branchExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid branch",
                    Detail = $"Branch with ID '{request.AssignedBranchId}' was not found, is inactive, or does not belong to this organization.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        // Validate counter exists within organization if specified
        if (request.AssignedCounterId.HasValue)
        {
            // Counter belongs to branch, so verify branch belongs to organization
            var counterExists = await _dbContext.Counters
                .AnyAsync(c => c.Id == request.AssignedCounterId.Value && c.IsActive
                    && c.Branch != null && c.Branch.OrganizationId == organizationId);
            if (!counterExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid counter",
                    Detail = $"Counter with ID '{request.AssignedCounterId}' was not found, is inactive, or does not belong to this organization.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId, // SECURITY: Always from tenant context or SuperAdmin decision
            Username = request.Username.ToLowerInvariant(),
            Email = request.Email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            Phone = request.Phone,
            EmployeeNumber = request.EmployeeNumber,
            RoleId = role.Id,
            AssignedBranchId = request.AssignedBranchId,
            AssignedCounterId = request.AssignedCounterId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created user: {UserId} - {Username}", user.Id, user.Username);

        var dto = new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            FullName = user.FullName,
            Phone = user.Phone,
            EmployeeNumber = user.EmployeeNumber,
            RoleId = role.Id,
            RoleCode = role.Code,
            RoleName = role.Name,
            RoleColor = role.Color,
            AssignedBranchId = user.AssignedBranchId,
            AssignedCounterId = user.AssignedCounterId,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt
        };

        return CreatedAtAction(nameof(GetUser), new { userId = user.Id }, dto);
    }

    /// <summary>
    /// Updates an existing user
    /// </summary>
    [HttpPut("{userId:guid}")]
    [RequirePermission(Permissions.UsersEdit)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateUser(Guid userId, [FromBody] UpdateUserRequest request)
    {
        // SECURITY: Build organization filter (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users
            .Include(u => u.Role)
            .Include(u => u.AssignedBranch)
            .Include(u => u.AssignedCounter)
            .Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only modify users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        // Check for duplicate email if changed within organization
        if (!string.IsNullOrWhiteSpace(request.Email) && request.Email.ToLowerInvariant() != user.Email)
        {
            var existingEmail = await _dbContext.Users
                .AnyAsync(u => u.Email == request.Email.ToLowerInvariant() && u.Id != userId && u.OrganizationId == user.OrganizationId);
            if (existingEmail)
                return BadRequest(new ProblemDetails
                {
                    Title = "Duplicate email",
                    Detail = $"A user with email '{request.Email}' already exists.",
                    Status = StatusCodes.Status400BadRequest
                });
            user.Email = request.Email.ToLowerInvariant();
        }

        // Validate role exists if changing
        var roleChanged = false;
        if (request.RoleId.HasValue)
        {
            var role = await _dbContext.Roles.FindAsync(request.RoleId.Value);
            if (role == null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid role",
                    Detail = $"Role with ID '{request.RoleId}' was not found.",
                    Status = StatusCodes.Status400BadRequest
                });
            roleChanged = user.RoleId != role.Id;
            user.RoleId = role.Id;
        }

        // Validate branch exists within organization if specified
        if (request.AssignedBranchId.HasValue)
        {
            var branchExists = await _dbContext.Branches
                .AnyAsync(b => b.Id == request.AssignedBranchId.Value && b.OrganizationId == user.OrganizationId && b.IsActive);
            if (!branchExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid branch",
                    Detail = $"Branch with ID '{request.AssignedBranchId}' was not found or is inactive.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        // Validate counter exists within organization if specified
        if (request.AssignedCounterId.HasValue)
        {
            var counterExists = await _dbContext.Counters
                .AnyAsync(c => c.Id == request.AssignedCounterId.Value && c.IsActive
                    && c.Branch != null && c.Branch.OrganizationId == user.OrganizationId);
            if (!counterExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid counter",
                    Detail = $"Counter with ID '{request.AssignedCounterId}' was not found, is inactive, or does not belong to this organization.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        // Update fields
        user.FirstName = request.FirstName ?? user.FirstName;
        user.LastName = request.LastName ?? user.LastName;
        user.Phone = request.Phone ?? user.Phone;
        user.EmployeeNumber = request.EmployeeNumber ?? user.EmployeeNumber;
        user.AssignedBranchId = request.AssignedBranchId;
        user.AssignedCounterId = request.AssignedCounterId;
        user.UpdatedAt = DateTime.UtcNow;

        // Update password if provided
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            // Validate password against platform policy
            var passwordValidation = await _passwordValidation.ValidatePasswordAsync(
                request.Password,
                user.Username,
                user.Email);

            if (!passwordValidation.IsValid)
                return BadRequest(new ProblemDetails
                {
                    Title = "Password validation failed",
                    Detail = passwordValidation.ErrorMessage,
                    Status = StatusCodes.Status400BadRequest
                });

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        }

        await _dbContext.SaveChangesAsync();

        // SECURITY: a stale cached permission set from the user's *old* role must not
        // outlive a role change by up to CacheDuration (5 min) — most important on
        // downgrade/revocation, where the old set may be more privileged than the new one.
        if (roleChanged)
            _cache.InvalidateUserPermissions(user.Id);

        _logger.LogInformation("Updated user: {UserId} - {Username}", user.Id, user.Username);

        // Reload navigation properties for response
        await _dbContext.Entry(user).Reference(u => u.Role).LoadAsync();
        await _dbContext.Entry(user).Reference(u => u.AssignedBranch).LoadAsync();
        await _dbContext.Entry(user).Reference(u => u.AssignedCounter).LoadAsync();

        return Ok(new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            FullName = user.FullName,
            Phone = user.Phone,
            EmployeeNumber = user.EmployeeNumber,
            RoleId = user.RoleId,
            RoleCode = user.Role.Code,
            RoleName = user.Role.Name,
            RoleColor = user.Role.Color,
            AssignedBranchId = user.AssignedBranchId,
            AssignedBranchName = user.AssignedBranch?.Name,
            AssignedCounterId = user.AssignedCounterId,
            AssignedCounterNumber = user.AssignedCounter?.CounterNumber,
            IsActive = user.IsActive,
            LastLogin = user.LastLogin,
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Toggles a user's active status
    /// </summary>
    [HttpPatch("{userId:guid}/toggle")]
    [RequirePermission(Permissions.UsersEdit)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleUser(Guid userId)
    {
        // SECURITY: Build organization filter (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users
            .Include(u => u.Role)
            .Include(u => u.AssignedBranch)
            .Include(u => u.AssignedCounter)
            .Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only modify users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        user.IsActive = !user.IsActive;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // SECURITY: deactivation must take effect immediately, not after the permission
        // cache's TTL — otherwise a just-deactivated user keeps working permission-gated
        // endpoints for up to 5 more minutes on their still-valid JWT.
        _cache.InvalidateUserPermissions(user.Id);

        _logger.LogInformation("Toggled user {UserId} active status to {IsActive}", userId, user.IsActive);

        return Ok(new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            FullName = user.FullName,
            Phone = user.Phone,
            EmployeeNumber = user.EmployeeNumber,
            RoleId = user.RoleId,
            RoleCode = user.Role.Code,
            RoleName = user.Role.Name,
            RoleColor = user.Role.Color,
            AssignedBranchId = user.AssignedBranchId,
            AssignedBranchName = user.AssignedBranch?.Name,
            AssignedCounterId = user.AssignedCounterId,
            AssignedCounterNumber = user.AssignedCounter?.CounterNumber,
            IsActive = user.IsActive,
            LastLogin = user.LastLogin,
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Deletes a user (soft delete)
    /// </summary>
    [HttpDelete("{userId:guid}")]
    [RequirePermission(Permissions.UsersDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteUser(Guid userId)
    {
        // SECURITY: Build organization filter (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users.Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only delete users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        // Soft delete
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // SECURITY: see ToggleUser — don't let a cached permission set outlive deletion.
        _cache.InvalidateUserPermissions(user.Id);

        _logger.LogInformation("Deleted (soft) user: {UserId} - {Username}", userId, user.Username);

        return NoContent();
    }

    /// <summary>
    /// Resets a user's password
    /// </summary>
    [HttpPost("{userId:guid}/reset-password")]
    [RequirePermission(Permissions.UsersEdit)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(Guid userId, [FromBody] ResetPasswordRequest request)
    {
        // SECURITY: Build organization filter (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users.Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only reset passwords for users in their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "New password is required.",
                Status = StatusCodes.Status400BadRequest
            });

        // Validate password against platform policy
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

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.RefreshToken = null; // Invalidate any existing refresh tokens
        user.RefreshTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Reset password for user: {UserId} - {Username}", userId, user.Username);

        return Ok(new { message = "Password reset successfully" });
    }

    /// <summary>
    /// Gets available roles from database
    /// </summary>
    [HttpGet("roles")]
    [RequirePermission(Permissions.RolesView)]
    [ProducesResponseType(typeof(List<RoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRoles()
    {
        var roles = await _dbContext.Roles
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .Select(r => new RoleDto
            {
                Id = r.Id,
                Code = r.Code,
                Name = r.Name,
                Description = r.Description,
                Color = r.Color,
                Icon = r.Icon,
                IsSystem = r.IsSystem
            })
            .ToListAsync();

        return Ok(roles);
    }
}

#region Request/Response DTOs

public record UserDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? EmployeeNumber { get; init; }
    public Guid RoleId { get; init; }
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public string? RoleColor { get; init; }
    public Guid? AssignedBranchId { get; init; }
    public string? AssignedBranchName { get; init; }
    public Guid? AssignedCounterId { get; init; }
    public string? AssignedCounterNumber { get; init; }
    public bool IsActive { get; init; }
    public DateTime? LastLogin { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record CreateUserRequest
{
    public Guid? OrganizationId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Phone { get; init; }
    public string? EmployeeNumber { get; init; }
    public Guid? RoleId { get; init; }
    public Guid? AssignedBranchId { get; init; }
    public Guid? AssignedCounterId { get; init; }
}

public record UpdateUserRequest
{
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Phone { get; init; }
    public string? EmployeeNumber { get; init; }
    public Guid? RoleId { get; init; }
    public Guid? AssignedBranchId { get; init; }
    public Guid? AssignedCounterId { get; init; }
}

public record ResetPasswordRequest
{
    public string NewPassword { get; init; } = string.Empty;
}

public record RoleDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Color { get; init; }
    public string? Icon { get; init; }
    public bool IsSystem { get; init; }
}

#endregion
