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

    /// <summary>
    /// Maps ApiClient "scope" strings (ApiClientsSetup.razor's colon-separated vocabulary,
    /// e.g. "queue:write") to the permission codes this handler actually checks against
    /// (dot-separated, e.g. "queue.manage"). Without this mapping every API-key-authenticated
    /// request was silently denied by every [RequirePermission]-protected endpoint regardless of
    /// the client's configured scopes — ApiKeyAuthenticationMiddleware sets "scope" claims but
    /// this handler only ever looked for a user ID, which API-key auth never has. That made the
    /// entire external-integration story (hospital/banking/pharmacy system types already
    /// anticipated in ApiClientsSetup.razor's own icon mapping) non-functional end to end: a
    /// valid, active API key could authenticate but could never actually call anything.
    /// </summary>
    private static readonly Dictionary<string, string[]> ScopeToPermissions = new()
    {
        ["queue:read"] = new[] { Permissions.QueueView },
        ["queue:write"] = new[] { Permissions.QueueManage },
        ["token:read"] = new[] { Permissions.TokensView },
        ["token:create"] = new[] { Permissions.TokensCreate },
        ["token:manage"] = new[] { Permissions.QueueManage, Permissions.TokensCancel },
        ["counter:read"] = new[] { Permissions.CountersView },
        ["service:read"] = new[] { Permissions.ServiceTypesView },
        ["stats:read"] = new[] { Permissions.ReportsView },
        ["roster:read"] = new[] { Permissions.StudentsView },
        ["roster:write"] = new[] { Permissions.StudentsManage },
        // Added for the ERP Bridge's digital-signage sync (Hotel/Retail room-status and
        // promo/stock content) — content:read/write were never mapped, so no API-client
        // scope could ever satisfy a [RequirePermission(Permissions.Content*)] endpoint.
        ["content:read"] = new[] { Permissions.ContentView },
        ["content:write"] = new[] { Permissions.ContentCreate, Permissions.ContentEdit },
        // DisplayBannerController.UpdateDisplayBanner requires SettingsEdit — the Bridge uses the
        // branch ticker banner (not the full MediaContent/Playlist pipeline) as the fastest correct
        // signage proof: it's already public, already live-pushes via DisplayHub, zero new Q-Mgr code.
        ["settings:write"] = new[] { Permissions.SettingsEdit },
        // Added 2026-09-03 (API-key hardening): Visitor Management, Marketing and the Student
        // Welfare Ledger previously had no scopes at all, so they were JWT-only by accident of
        // omission rather than by decision.
        ["visitors:read"] = new[] { Permissions.VisitorsView },
        ["visitors:write"] = new[] { Permissions.VisitorsManage, Permissions.VisitorsCheckIn, Permissions.VisitorsCheckOut },
        ["marketing:read"] = new[] { Permissions.MarketingView },
        ["marketing:send"] = new[] { Permissions.MarketingManage, Permissions.MarketingSend },
        ["welfare:read"] = new[] { Permissions.WelfareView },
        ["welfare:write"] = new[] { Permissions.WelfareCreate, Permissions.WelfareEdit },
    };

    /// <summary>
    /// The canonical list of API-client scopes this handler understands — the single source of
    /// truth for any scope picker or validation. A scope not in this list is silently inert (it
    /// grants nothing), so UI/API surfaces that let admins assign scopes should be driven from here.
    /// </summary>
    public static readonly IReadOnlyList<string> AllScopes = ScopeToPermissions.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Returns the permission codes a given scope grants (empty for an unknown scope).
    /// </summary>
    public static IReadOnlyList<string> GetPermissionsForScope(string scope)
        => ScopeToPermissions.TryGetValue(scope, out var perms) ? perms : Array.Empty<string>();

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.FindFirst("auth_method")?.Value == "api_key")
        {
            var grantedByScopes = context.User.FindAll("scope")
                .SelectMany(c => ScopeToPermissions.TryGetValue(c.Value, out var perms) ? perms : Array.Empty<string>());

            if (grantedByScopes.Contains(requirement.Permission))
            {
                _logger.LogDebug("API key granted permission {Permission} via scope claim", requirement.Permission);
                context.Succeed(requirement);
            }
            else
            {
                _logger.LogDebug("API key denied permission {Permission} — no matching scope", requirement.Permission);
            }
            return;
        }

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
