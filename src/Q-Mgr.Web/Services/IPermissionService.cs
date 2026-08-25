namespace QMgr.Web.Services;

/// <summary>
/// Service for checking user permissions in the frontend
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Checks if the current user has a specific permission
    /// </summary>
    Task<bool> HasPermissionAsync(string permission);

    /// <summary>
    /// Checks if the current user has any of the specified permissions
    /// </summary>
    Task<bool> HasAnyPermissionAsync(params string[] permissions);

    /// <summary>
    /// Checks if the current user has all of the specified permissions
    /// </summary>
    Task<bool> HasAllPermissionsAsync(params string[] permissions);

    /// <summary>
    /// Gets all permissions for the current user
    /// </summary>
    Task<HashSet<string>> GetPermissionsAsync();

    /// <summary>
    /// Checks if the current user has the SuperAdmin role (bypass all checks)
    /// </summary>
    Task<bool> IsSuperAdminAsync();

    /// <summary>
    /// Clears the cached permissions (call on logout)
    /// </summary>
    void ClearCache();
}

public class PermissionService : IPermissionService
{
    private readonly IAuthService _authService;
    private HashSet<string>? _cachedPermissions;
    private string? _cachedRoleCode;
    private Guid? _cachedUserId;

    public PermissionService(IAuthService authService)
    {
        _authService = authService;
    }

    public async Task<bool> HasPermissionAsync(string permission)
    {
        if (string.IsNullOrWhiteSpace(permission))
            return false;

        // SuperAdmin bypasses all permission checks
        if (await IsSuperAdminAsync())
            return true;

        var permissions = await GetPermissionsAsync();
        return permissions.Contains(permission);
    }

    public async Task<bool> HasAnyPermissionAsync(params string[] permissions)
    {
        if (permissions == null || permissions.Length == 0)
            return false;

        // SuperAdmin bypasses all permission checks
        if (await IsSuperAdminAsync())
            return true;

        var userPermissions = await GetPermissionsAsync();
        return permissions.Any(p => userPermissions.Contains(p));
    }

    public async Task<bool> HasAllPermissionsAsync(params string[] permissions)
    {
        if (permissions == null || permissions.Length == 0)
            return true;

        // SuperAdmin bypasses all permission checks
        if (await IsSuperAdminAsync())
            return true;

        var userPermissions = await GetPermissionsAsync();
        return permissions.All(p => userPermissions.Contains(p));
    }

    public async Task<HashSet<string>> GetPermissionsAsync()
    {
        var user = await _authService.GetCurrentUserAsync();

        // If no user, return empty permissions
        if (user == null)
        {
            ClearCache();
            return new HashSet<string>();
        }

        // If user changed (different login), clear cache
        if (_cachedUserId != null && _cachedUserId != user.Id)
        {
            ClearCache();
        }

        // Return cached permissions if available
        if (_cachedPermissions != null)
            return _cachedPermissions;

        _cachedUserId = user.Id;
        _cachedRoleCode = user.RoleCode;
        _cachedPermissions = user.Permissions?.ToHashSet() ?? new HashSet<string>();
        return _cachedPermissions;
    }

    public async Task<bool> IsSuperAdminAsync()
    {
        var user = await _authService.GetCurrentUserAsync();
        if (user == null)
            return false;

        // If user changed, clear cache
        if (_cachedUserId != null && _cachedUserId != user.Id)
        {
            ClearCache();
        }

        if (_cachedRoleCode == null)
        {
            _cachedUserId = user.Id;
            _cachedRoleCode = user.RoleCode;
        }

        return _cachedRoleCode.Equals(RoleCodes.SuperAdmin, StringComparison.OrdinalIgnoreCase);
    }

    public void ClearCache()
    {
        _cachedPermissions = null;
        _cachedRoleCode = null;
        _cachedUserId = null;
    }
}

/// <summary>
/// Permission constants matching the backend Permissions class
/// </summary>
public static class Permissions
{
    // Dashboard
    public const string DashboardView = "dashboard.view";

    // Users
    public const string UsersView = "users.view";
    public const string UsersCreate = "users.create";
    public const string UsersEdit = "users.edit";
    public const string UsersDelete = "users.delete";

    // Roles
    public const string RolesView = "roles.view";
    public const string RolesCreate = "roles.create";
    public const string RolesEdit = "roles.edit";
    public const string RolesDelete = "roles.delete";

    // Branches
    public const string BranchesView = "branches.view";
    public const string BranchesCreate = "branches.create";
    public const string BranchesEdit = "branches.edit";
    public const string BranchesDelete = "branches.delete";

    // Counters
    public const string CountersView = "counters.view";
    public const string CountersCreate = "counters.create";
    public const string CountersEdit = "counters.edit";
    public const string CountersDelete = "counters.delete";

    // Service Types
    public const string ServiceTypesView = "service-types.view";
    public const string ServiceTypesCreate = "service-types.create";
    public const string ServiceTypesEdit = "service-types.edit";
    public const string ServiceTypesDelete = "service-types.delete";

    // Queue
    public const string QueueView = "queue.view";
    public const string QueueManage = "queue.manage";

    // Tokens
    public const string TokensView = "tokens.view";
    public const string TokensCreate = "tokens.create";
    public const string TokensCancel = "tokens.cancel";

    // Reports
    public const string ReportsView = "reports.view";
    public const string ReportsExport = "reports.export";

    // Content
    public const string ContentView = "content.view";
    public const string ContentCreate = "content.create";
    public const string ContentEdit = "content.edit";
    public const string ContentDelete = "content.delete";

    // Feedback
    public const string FeedbackView = "feedback.view";
    public const string FeedbackRespond = "feedback.respond";
    public const string FeedbackAnalytics = "feedback.analytics";

    // Settings
    public const string SettingsView = "settings.view";
    public const string SettingsEdit = "settings.edit";

    // Notifications
    public const string NotificationsView = "notifications.view";
    public const string NotificationsManage = "notifications.manage";

    // API Clients
    public const string ApiClientsView = "api-clients.view";
    public const string ApiClientsCreate = "api-clients.create";
    public const string ApiClientsEdit = "api-clients.edit";
    public const string ApiClientsDelete = "api-clients.delete";

    // Billing
    public const string BillingView = "billing.view";
    public const string BillingManage = "billing.manage";

    // Customer-facing (self-service)
    public const string CustomerQueueStatus = "customer.queue-status";
    public const string CustomerFeedbackSubmit = "customer.feedback-submit";
    public const string CustomerTokenHistory = "customer.token-history";
    public const string CustomerProfile = "customer.profile";

    // Platform Admin (SuperAdmin only)
    public const string PlatformAdmin = "platform.admin";
    public const string TenantsView = "tenants.view";
    public const string TenantsManage = "tenants.manage";
    public const string SystemSettings = "system.settings";
    public const string PlatformSettingsView = "platform.settings.view";
    public const string PlatformSettingsEdit = "platform.settings.edit";
    public const string PlatformAnalytics = "platform.analytics";
}

/// <summary>
/// Role code constants matching the backend RoleCodes class.
/// Use these constants instead of string literals to prevent typos and case sensitivity issues.
/// </summary>
public static class RoleCodes
{
    /// <summary>
    /// Platform Administrator - Full access across all organizations.
    /// </summary>
    public const string SuperAdmin = "super-admin";

    /// <summary>
    /// Tenant Administrator - Full access within their organization.
    /// </summary>
    public const string Admin = "admin";

    /// <summary>
    /// Manager - Branch management and staff supervision.
    /// </summary>
    public const string Manager = "manager";

    /// <summary>
    /// Staff - Counter operations and queue management.
    /// </summary>
    public const string Staff = "staff";

    /// <summary>
    /// Viewer - Read-only access and customer self-service.
    /// </summary>
    public const string Viewer = "viewer";

    /// <summary>
    /// Checks if the role code represents a platform administrator
    /// </summary>
    public static bool IsSuperAdmin(string? roleCode)
    {
        return string.Equals(roleCode, SuperAdmin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the role has administrative privileges (SuperAdmin or Admin)
    /// </summary>
    public static bool IsAdministrator(string? roleCode)
    {
        return IsSuperAdmin(roleCode) || string.Equals(roleCode, Admin, StringComparison.OrdinalIgnoreCase);
    }
}
