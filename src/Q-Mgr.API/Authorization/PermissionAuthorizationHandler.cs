using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Authorization;

/// <summary>
/// Authorization handler that checks if a user has the required permission
/// through their assigned role.
/// </summary>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PermissionAuthorizationHandler(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<PermissionAuthorizationHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // Get user ID from claims
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirst("sub");

        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            _logger.LogDebug("Authorization failed: No valid user ID in claims");
            return;
        }

        // Check if user is SuperAdmin (bypasses all permission checks)
        var roleClaim = context.User.FindFirst(ClaimTypes.Role)
            ?? context.User.FindFirst("role");

        if (RoleCodes.IsSuperAdmin(roleClaim?.Value))
        {
            _logger.LogDebug("SuperAdmin user {UserId} bypassing permission check for {Permission}",
                userId, requirement.Permission);
            context.Succeed(requirement);
            return;
        }

        // Get user's permissions (with caching)
        var permissions = await GetUserPermissionsAsync(userId);

        if (permissions.Contains(requirement.Permission))
        {
            _logger.LogDebug("User {UserId} has permission {Permission}", userId, requirement.Permission);
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogDebug("User {UserId} denied permission {Permission}", userId, requirement.Permission);
        }
    }

    private async Task<HashSet<string>> GetUserPermissionsAsync(Guid userId)
    {
        var cacheKey = $"user_permissions:{userId}";

        if (_cache.TryGetValue<HashSet<string>>(cacheKey, out var cachedPermissions) && cachedPermissions != null)
        {
            return cachedPermissions;
        }

        // Create a new scope to get the DbContext (since this handler is singleton)
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QMgrDbContext>();

        // Get user's role and its permissions
        var permissions = await dbContext.Users
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .ToHashSetAsync();

        // Cache the permissions
        _cache.Set(cacheKey, permissions, CacheDuration);

        _logger.LogDebug("Loaded {Count} permissions for user {UserId}", permissions.Count, userId);

        return permissions;
    }
}

/// <summary>
/// Extension methods for invalidating permission cache
/// </summary>
public static class PermissionCacheExtensions
{
    /// <summary>
    /// Invalidates the permission cache for a specific user.
    /// Call this when a user's role changes or when role permissions are modified.
    /// </summary>
    public static void InvalidateUserPermissions(this IMemoryCache cache, Guid userId)
    {
        cache.Remove($"user_permissions:{userId}");
    }

    /// <summary>
    /// Invalidates permission cache for all users with a specific role.
    /// Call this when role permissions are modified.
    /// </summary>
    public static async Task InvalidateRolePermissionsAsync(
        this IMemoryCache cache,
        QMgrDbContext dbContext,
        Guid roleId)
    {
        var userIds = await dbContext.Users
            .Where(u => u.RoleId == roleId)
            .Select(u => u.Id)
            .ToListAsync();

        foreach (var userId in userIds)
        {
            cache.Remove($"user_permissions:{userId}");
        }
    }
}
