using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.API.Authorization;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/roles")]
[Produces("application/json")]
[Authorize] // SECURITY: Require authentication for all role management endpoints
public class RolesController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<RolesController> _logger;

    public RolesController(
        QMgrDbContext dbContext,
        IMemoryCache cache,
        ITenantContextAccessor tenantAccessor,
        ILogger<RolesController> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// SECURITY: true if the caller (not SuperAdmin) may not touch this role - it belongs
    /// to a different organization. System roles (OrganizationId == null) are excluded
    /// since they're shared/read-only for everyone; the mutating endpoints below already
    /// separately reject any write against a system role via their own IsSystem check.
    /// </summary>
    private bool RoleOutOfScope(Role role)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (RoleCodes.IsSuperAdmin(tenantContext?.UserRole)) return false;
        if (role.OrganizationId == null) return false;
        return role.OrganizationId != tenantContext?.OrganizationId;
    }

    /// <summary>
    /// Gets all roles (system + organization-specific)
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.RolesView)]
    [ProducesResponseType(typeof(List<RoleListDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRoles([FromQuery] bool includeInactive = false)
    {
        var query = _dbContext.Roles.AsQueryable();

        // SECURITY: Roles are organization-scoped (system roles have OrganizationId ==
        // null and are visible to everyone) - this previously had no filter at all, so
        // any tenant Admin viewing Users & Roles saw every other tenant's custom roles
        // (name, permission/user counts) mixed in with their own. Same bug shape as the
        // cross-tenant IDORs found in BranchesController/ContentController/etc.
        var tenantContext = _tenantAccessor.TenantContext;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(tenantContext?.UserRole);
        if (!isSuperAdmin)
        {
            var callerOrgId = tenantContext?.OrganizationId;
            query = query.Where(r => r.OrganizationId == null || r.OrganizationId == callerOrgId);
        }

        if (!includeInactive)
            query = query.Where(r => r.IsActive);

        var roles = await query
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .Select(r => new RoleListDto
            {
                Id = r.Id,
                Code = r.Code,
                Name = r.Name,
                Description = r.Description,
                Color = r.Color,
                Icon = r.Icon,
                IsSystem = r.IsSystem,
                IsActive = r.IsActive,
                UserCount = r.Users.Count(u => u.IsActive),
                PermissionCount = r.RolePermissions.Count
            })
            .ToListAsync();

        return Ok(roles);
    }

    /// <summary>
    /// Gets a specific role with its permissions
    /// </summary>
    [HttpGet("{roleId:guid}")]
    [RequirePermission(Permissions.RolesView)]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRole(Guid roleId)
    {
        // SECURITY: same org-scoping as GetRoles - without this, any tenant Admin could
        // read another tenant's custom role (including its full permission list) just by
        // knowing/guessing its GUID, e.g. one surfaced by the also-fixed GetRoles list.
        var tenantContext = _tenantAccessor.TenantContext;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(tenantContext?.UserRole);
        var callerOrgId = tenantContext?.OrganizationId;

        var role = await _dbContext.Roles
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .Where(r => r.Id == roleId && (isSuperAdmin || r.OrganizationId == null || r.OrganizationId == callerOrgId))
            .Select(r => new RoleDetailDto
            {
                Id = r.Id,
                OrganizationId = r.OrganizationId,
                Code = r.Code,
                Name = r.Name,
                Description = r.Description,
                Color = r.Color,
                Icon = r.Icon,
                IsSystem = r.IsSystem,
                IsActive = r.IsActive,
                SortOrder = r.SortOrder,
                CreatedAt = r.CreatedAt,
                Permissions = r.RolePermissions
                    .Select(rp => rp.Permission.Code)
                    .ToList()
            })
            .FirstOrDefaultAsync();

        if (role == null)
            return NotFound(new ProblemDetails
            {
                Title = "Role not found",
                Detail = $"Role with ID '{roleId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        return Ok(role);
    }

    /// <summary>
    /// Creates a new custom role for the organization
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.RolesCreate)]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request)
    {
        // Validation
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Role name is required.",
                Status = StatusCodes.Status400BadRequest
            });

        if (string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Role code is required.",
                Status = StatusCodes.Status400BadRequest
            });

        // SECURITY: Non-SuperAdmin callers can only create roles scoped to their own
        // organization; a global (OrganizationId = null) role is visible/assignable to
        // every tenant, so only SuperAdmin may create one. Client-supplied OrganizationId
        // is otherwise ignored — always derive it from the caller's own tenant context.
        var tenantContext = _tenantAccessor.TenantContext;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(tenantContext?.UserRole);
        Guid? organizationId;
        if (isSuperAdmin)
        {
            organizationId = request.OrganizationId;
        }
        else
        {
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            organizationId = tenantContext.OrganizationId;
        }

        // Check for duplicate code
        var existingCode = await _dbContext.Roles.AnyAsync(r =>
            r.Code == request.Code.ToLowerInvariant() &&
            r.OrganizationId == organizationId);

        if (existingCode)
            return BadRequest(new ProblemDetails
            {
                Title = "Duplicate code",
                Detail = $"A role with code '{request.Code}' already exists.",
                Status = StatusCodes.Status400BadRequest
            });

        // Get valid permission IDs. SECURITY: non-SuperAdmin callers may only grant
        // visible (tenant-facing) permissions — platform-tier permissions (platform.admin,
        // tenants.*, system.*) are IsVisible=false specifically so tenants can never hold
        // them; that intent was previously only enforced at role-seed time, not here.
        var validPermissionIds = new HashSet<Guid>();
        if (request.PermissionIds?.Any() == true)
        {
            var permissionQuery = _dbContext.Permissions
                .Where(p => request.PermissionIds.Contains(p.Id));
            if (!isSuperAdmin)
                permissionQuery = permissionQuery.Where(p => p.IsVisible);

            validPermissionIds = await permissionQuery
                .Select(p => p.Id)
                .ToHashSetAsync();
        }

        var role = new Role
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Code = request.Code.ToLowerInvariant(),
            Name = request.Name,
            Description = request.Description,
            Color = request.Color,
            Icon = request.Icon,
            SortOrder = request.SortOrder ?? 100,
            IsSystem = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Roles.Add(role);

        // Add permissions
        foreach (var permissionId in validPermissionIds)
        {
            _dbContext.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                PermissionId = permissionId,
                GrantedAt = DateTime.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created role: {RoleId} - {RoleCode}", role.Id, role.Code);

        var permissions = await _dbContext.RolePermissions
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.Permission.Code)
            .ToListAsync();

        return CreatedAtAction(nameof(GetRole), new { roleId = role.Id }, new RoleDetailDto
        {
            Id = role.Id,
            OrganizationId = role.OrganizationId,
            Code = role.Code,
            Name = role.Name,
            Description = role.Description,
            Color = role.Color,
            Icon = role.Icon,
            IsSystem = role.IsSystem,
            IsActive = role.IsActive,
            SortOrder = role.SortOrder,
            CreatedAt = role.CreatedAt,
            Permissions = permissions
        });
    }

    /// <summary>
    /// Updates a role (cannot update system roles)
    /// </summary>
    [HttpPut("{roleId:guid}")]
    [RequirePermission(Permissions.RolesEdit)]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateRole(Guid roleId, [FromBody] UpdateRoleRequest request)
    {
        var role = await _dbContext.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == roleId);

        if (role == null || RoleOutOfScope(role))
            return NotFound(new ProblemDetails
            {
                Title = "Role not found",
                Detail = $"Role with ID '{roleId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        if (role.IsSystem)
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot modify system role",
                Detail = "System roles cannot be modified. Create a custom role instead.",
                Status = StatusCodes.Status400BadRequest
            });

        // Update fields
        if (!string.IsNullOrWhiteSpace(request.Name))
            role.Name = request.Name;
        if (!string.IsNullOrWhiteSpace(request.Description))
            role.Description = request.Description;
        if (!string.IsNullOrWhiteSpace(request.Color))
            role.Color = request.Color;
        if (!string.IsNullOrWhiteSpace(request.Icon))
            role.Icon = request.Icon;
        if (request.SortOrder.HasValue)
            role.SortOrder = request.SortOrder.Value;

        role.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // Invalidate permission cache for users with this role
        await _cache.InvalidateRolePermissionsAsync(_dbContext, roleId);

        _logger.LogInformation("Updated role: {RoleId} - {RoleCode}", role.Id, role.Code);

        var permissions = await _dbContext.RolePermissions
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.Permission.Code)
            .ToListAsync();

        return Ok(new RoleDetailDto
        {
            Id = role.Id,
            OrganizationId = role.OrganizationId,
            Code = role.Code,
            Name = role.Name,
            Description = role.Description,
            Color = role.Color,
            Icon = role.Icon,
            IsSystem = role.IsSystem,
            IsActive = role.IsActive,
            SortOrder = role.SortOrder,
            CreatedAt = role.CreatedAt,
            Permissions = permissions
        });
    }

    /// <summary>
    /// Updates the permissions for a role
    /// </summary>
    [HttpPut("{roleId:guid}/permissions")]
    [RequirePermission(Permissions.RolesEdit)]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateRolePermissions(Guid roleId, [FromBody] UpdateRolePermissionsRequest request)
    {
        var role = await _dbContext.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == roleId);

        if (role == null || RoleOutOfScope(role))
            return NotFound(new ProblemDetails
            {
                Title = "Role not found",
                Detail = $"Role with ID '{roleId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        if (role.IsSystem)
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot modify system role permissions",
                Detail = "System role permissions cannot be modified. Create a custom role instead.",
                Status = StatusCodes.Status400BadRequest
            });

        // Get valid permission IDs. SECURITY: non-SuperAdmin callers may only grant
        // visible (tenant-facing) permissions — see CreateRole for the same rationale.
        var isSuperAdmin = RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole);
        var permissionQuery = _dbContext.Permissions
            .Where(p => request.PermissionIds.Contains(p.Id));
        if (!isSuperAdmin)
            permissionQuery = permissionQuery.Where(p => p.IsVisible);

        var validPermissionIds = await permissionQuery
            .Select(p => p.Id)
            .ToHashSetAsync();

        // Remove existing permissions
        _dbContext.RolePermissions.RemoveRange(role.RolePermissions);

        // Add new permissions
        foreach (var permissionId in validPermissionIds)
        {
            _dbContext.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                PermissionId = permissionId,
                GrantedAt = DateTime.UtcNow
            });
        }

        role.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // Invalidate permission cache for users with this role
        await _cache.InvalidateRolePermissionsAsync(_dbContext, roleId);

        _logger.LogInformation("Updated permissions for role: {RoleId} - {RoleCode}", role.Id, role.Code);

        var permissions = await _dbContext.RolePermissions
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.Permission.Code)
            .ToListAsync();

        return Ok(new RoleDetailDto
        {
            Id = role.Id,
            OrganizationId = role.OrganizationId,
            Code = role.Code,
            Name = role.Name,
            Description = role.Description,
            Color = role.Color,
            Icon = role.Icon,
            IsSystem = role.IsSystem,
            IsActive = role.IsActive,
            SortOrder = role.SortOrder,
            CreatedAt = role.CreatedAt,
            Permissions = permissions
        });
    }

    /// <summary>
    /// Toggles a role's active status
    /// </summary>
    [HttpPatch("{roleId:guid}/toggle")]
    [RequirePermission(Permissions.RolesEdit)]
    [ProducesResponseType(typeof(RoleListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ToggleRole(Guid roleId)
    {
        var role = await _dbContext.Roles
            .Include(r => r.Users)
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == roleId);

        if (role == null || RoleOutOfScope(role))
            return NotFound(new ProblemDetails
            {
                Title = "Role not found",
                Detail = $"Role with ID '{roleId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        if (role.IsSystem)
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot disable system role",
                Detail = "System roles cannot be disabled.",
                Status = StatusCodes.Status400BadRequest
            });

        role.IsActive = !role.IsActive;
        role.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Toggled role {RoleId} active status to {IsActive}", roleId, role.IsActive);

        return Ok(new RoleListDto
        {
            Id = role.Id,
            Code = role.Code,
            Name = role.Name,
            Description = role.Description,
            Color = role.Color,
            Icon = role.Icon,
            IsSystem = role.IsSystem,
            IsActive = role.IsActive,
            UserCount = role.Users.Count(u => u.IsActive),
            PermissionCount = role.RolePermissions.Count
        });
    }

    /// <summary>
    /// Deletes a custom role (cannot delete system roles)
    /// </summary>
    [HttpDelete("{roleId:guid}")]
    [RequirePermission(Permissions.RolesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteRole(Guid roleId)
    {
        var role = await _dbContext.Roles
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == roleId);

        if (role == null || RoleOutOfScope(role))
            return NotFound(new ProblemDetails
            {
                Title = "Role not found",
                Detail = $"Role with ID '{roleId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        if (role.IsSystem)
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot delete system role",
                Detail = "System roles cannot be deleted.",
                Status = StatusCodes.Status400BadRequest
            });

        if (role.Users.Any())
            return BadRequest(new ProblemDetails
            {
                Title = "Role in use",
                Detail = $"Cannot delete role '{role.Name}' because it has {role.Users.Count} user(s) assigned. Remove users first.",
                Status = StatusCodes.Status400BadRequest
            });

        _dbContext.Roles.Remove(role);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted role: {RoleId} - {RoleCode}", roleId, role.Code);

        return NoContent();
    }

    /// <summary>
    /// Gets all permissions grouped by category
    /// </summary>
    [HttpGet("permissions")]
    [RequirePermission(Permissions.RolesView)]
    [ProducesResponseType(typeof(List<PermissionCategoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPermissions()
    {
        var permissions = await _dbContext.Permissions
            .Where(p => p.IsActive && p.IsVisible)
            .OrderBy(p => p.Category)
            .ThenBy(p => p.SortOrder)
            .Select(p => new PermissionDto
            {
                Id = p.Id,
                Code = p.Code,
                Name = p.Name,
                Description = p.Description,
                Category = p.Category
            })
            .ToListAsync();

        var grouped = permissions
            .GroupBy(p => p.Category)
            .Select(g => new PermissionCategoryDto
            {
                Category = g.Key,
                Permissions = g.ToList()
            })
            .ToList();

        return Ok(grouped);
    }
}

#region DTOs

public record RoleListDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Color { get; init; }
    public string? Icon { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; }
    public int UserCount { get; init; }
    public int PermissionCount { get; init; }
}

public record RoleDetailDto
{
    public Guid Id { get; init; }
    public Guid? OrganizationId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Color { get; init; }
    public string? Icon { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; }
    public int SortOrder { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<string> Permissions { get; init; } = new();
}

public record CreateRoleRequest
{
    public Guid? OrganizationId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Color { get; init; }
    public string? Icon { get; init; }
    public int? SortOrder { get; init; }
    public List<Guid>? PermissionIds { get; init; }
}

public record UpdateRoleRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Color { get; init; }
    public string? Icon { get; init; }
    public int? SortOrder { get; init; }
}

public record UpdateRolePermissionsRequest
{
    public List<Guid> PermissionIds { get; init; } = new();
}

public record PermissionDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Category { get; init; } = string.Empty;
}

public record PermissionCategoryDto
{
    public string Category { get; init; } = string.Empty;
    public List<PermissionDto> Permissions { get; init; } = new();
}

#endregion
