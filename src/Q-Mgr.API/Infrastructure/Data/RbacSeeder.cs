using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Enums;

namespace QMgr.Infrastructure.Data;

/// <summary>
/// Comprehensive RBAC seeder for Q-Mgr platform.
/// Seeds permissions, roles, and role-permission mappings for:
/// - Platform Admin (SuperAdmin)
/// - Tenant Admin (Organization Admin)
/// - Staff (Counter operators)
/// - Customer (End users with limited access)
/// </summary>
public class RbacSeeder
{
    private readonly QMgrDbContext _context;
    private readonly ILogger<RbacSeeder> _logger;

    public RbacSeeder(QMgrDbContext context, ILogger<RbacSeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Seeds all RBAC data. This method is idempotent and can be run multiple times.
    /// </summary>
    public async Task SeedAsync()
    {
        await SeedPermissionsAsync();
        await SeedSystemRolesAsync();
        await SeedRolePermissionsAsync();
        await SeedPlatformAdminUserAsync();
    }

    #region Permission Definitions

    /// <summary>
    /// All permission definitions organized by category
    /// </summary>
    private static readonly List<PermissionDefinition> AllPermissions = new()
    {
        // ============================================
        // DASHBOARD
        // ============================================
        new("dashboard.view", "View Dashboard", "Access the main dashboard", "Dashboard", 1, true),

        // ============================================
        // USER MANAGEMENT
        // ============================================
        new("users.view", "View Users", "View user list and details", "User Management", 1, true),
        new("users.create", "Create Users", "Create new users in the organization", "User Management", 2, true),
        new("users.edit", "Edit Users", "Edit user details and assignments", "User Management", 3, true),
        new("users.delete", "Delete Users", "Deactivate or delete users", "User Management", 4, true),

        // ============================================
        // ROLE MANAGEMENT
        // ============================================
        new("roles.view", "View Roles", "View roles and their permissions", "Role Management", 1, true),
        new("roles.create", "Create Roles", "Create custom roles", "Role Management", 2, true),
        new("roles.edit", "Edit Roles", "Edit roles and assign permissions", "Role Management", 3, true),
        new("roles.delete", "Delete Roles", "Delete custom roles", "Role Management", 4, true),

        // ============================================
        // BRANCH MANAGEMENT
        // ============================================
        new("branches.view", "View Branches", "View branch list and details", "Branch Management", 1, true),
        new("branches.create", "Create Branches", "Create new branches", "Branch Management", 2, true),
        new("branches.edit", "Edit Branches", "Edit branch details and settings", "Branch Management", 3, true),
        new("branches.delete", "Delete Branches", "Delete or deactivate branches", "Branch Management", 4, true),

        // ============================================
        // COUNTER MANAGEMENT
        // ============================================
        new("counters.view", "View Counters", "View counter list and status", "Counter Management", 1, true),
        new("counters.create", "Create Counters", "Create new service counters", "Counter Management", 2, true),
        new("counters.edit", "Edit Counters", "Edit counter details and assignments", "Counter Management", 3, true),
        new("counters.delete", "Delete Counters", "Delete or deactivate counters", "Counter Management", 4, true),

        // ============================================
        // SERVICE TYPE MANAGEMENT
        // ============================================
        new("service-types.view", "View Service Types", "View available service types", "Service Types", 1, true),
        new("service-types.create", "Create Service Types", "Create new service types", "Service Types", 2, true),
        new("service-types.edit", "Edit Service Types", "Edit service type details", "Service Types", 3, true),
        new("service-types.delete", "Delete Service Types", "Delete service types", "Service Types", 4, true),

        // ============================================
        // QUEUE OPERATIONS
        // ============================================
        new("queue.view", "View Queue", "View queue status and waiting tokens", "Queue Operations", 1, true),
        new("queue.manage", "Manage Queue", "Call next, complete, transfer, hold tokens", "Queue Operations", 2, true),

        // ============================================
        // TOKEN MANAGEMENT
        // ============================================
        new("tokens.view", "View Tokens", "View token list and details", "Token Management", 1, true),
        new("tokens.create", "Create Tokens", "Issue new queue tokens", "Token Management", 2, true),
        new("tokens.cancel", "Cancel Tokens", "Cancel or void tokens", "Token Management", 3, true),

        // ============================================
        // REPORTS & ANALYTICS
        // ============================================
        new("reports.view", "View Reports", "View reports and analytics dashboards", "Reports", 1, true),
        new("reports.export", "Export Reports", "Export reports to PDF/Excel", "Reports", 2, true),

        // ============================================
        // CONTENT MANAGEMENT (Digital Signage)
        // ============================================
        new("content.view", "View Content", "View media library and playlists", "Content Management", 1, true),
        new("content.create", "Create Content", "Upload media and create playlists", "Content Management", 2, true),
        new("content.edit", "Edit Content", "Edit media metadata and playlists", "Content Management", 3, true),
        new("content.delete", "Delete Content", "Delete media and playlists", "Content Management", 4, true),

        // ============================================
        // FEEDBACK
        // ============================================
        new("feedback.view", "View Feedback", "View customer feedback submissions", "Feedback", 1, true),
        new("feedback.respond", "Respond to Feedback", "Reply to customer feedback", "Feedback", 2, true),
        new("feedback.analytics", "Feedback Analytics", "View feedback statistics and trends", "Feedback", 3, true),

        // ============================================
        // ORGANIZATION SETTINGS
        // ============================================
        new("settings.view", "View Settings", "View organization settings", "Settings", 1, true),
        new("settings.edit", "Edit Settings", "Modify organization settings", "Settings", 2, true),

        // ============================================
        // NOTIFICATIONS
        // ============================================
        new("notifications.view", "View Notifications", "View notification history", "Notifications", 1, true),
        new("notifications.manage", "Manage Notifications", "Configure notification settings and templates", "Notifications", 2, true),

        // ============================================
        // API CLIENTS (Integrations)
        // ============================================
        // ============================================
        // VISITOR MANAGEMENT
        // ============================================
        new("visitors.view", "View Visitors", "View visitor log and details", "Visitor Management", 1, true),
        new("visitors.checkin", "Check In Visitors", "Pre-register and check in visitors", "Visitor Management", 2, true),
        new("visitors.checkout", "Check Out Visitors", "Check out visitors", "Visitor Management", 3, true),
        new("visitors.manage", "Manage Visitors", "Edit visitor details, delete records, manage watchlist", "Visitor Management", 4, true),

        // ============================================
        // STUDENT ROSTERS
        // ============================================
        new("students.view", "View Student Rosters", "View students and their authorized guardians", "Student Rosters", 1, true),
        new("students.manage", "Manage Student Rosters", "Create/edit/delete students and guardians, bulk import a roster", "Student Rosters", 2, true),

        // ============================================
        // MARKETING (contacts + broadcast campaigns)
        // ============================================
        new("marketing.view", "View Marketing", "View contacts and broadcast campaigns", "Marketing", 1, true),
        new("marketing.manage", "Manage Marketing", "Manage contacts and create broadcast drafts", "Marketing", 2, true),
        new("marketing.send", "Send Broadcasts", "Schedule or send broadcast campaigns", "Marketing", 3, true),

        new("api-clients.view", "View API Clients", "View API client configurations", "API Clients", 1, true),
        new("api-clients.create", "Create API Clients", "Create new API client credentials", "API Clients", 2, true),
        new("api-clients.edit", "Edit API Clients", "Edit API client settings", "API Clients", 3, true),
        new("api-clients.delete", "Delete API Clients", "Revoke API client credentials", "API Clients", 4, true),

        // ============================================
        // BILLING (SaaS)
        // ============================================
        new("billing.view", "View Billing", "View subscription and invoices", "Billing", 1, true),
        new("billing.manage", "Manage Billing", "Manage subscription and payment methods", "Billing", 2, true),

        // ============================================
        // CUSTOMER-FACING (Limited permissions for end-users)
        // ============================================
        new("customer.queue-status", "View Queue Status", "View current queue position and wait times", "Customer", 1, true),
        new("customer.feedback-submit", "Submit Feedback", "Submit feedback after service", "Customer", 2, true),
        new("customer.token-history", "View Token History", "View own token history", "Customer", 3, true),
        new("customer.profile", "Manage Profile", "View and edit own profile", "Customer", 4, true),

        // ============================================
        // PLATFORM ADMINISTRATION (SuperAdmin only - not visible to tenants)
        // ============================================
        new("platform.admin", "Platform Administrator", "Full platform administration access", "Platform Admin", 0, false),
        new("tenants.view", "View Tenants", "View all tenant organizations", "Platform Admin", 1, false),
        new("tenants.manage", "Manage Tenants", "Create, edit, suspend tenant organizations", "Platform Admin", 2, false),
        new("system.settings", "System Settings", "Configure platform-wide settings", "Platform Admin", 3, false),
        new("platform.settings.view", "View Platform Settings", "View platform configuration", "Platform Admin", 4, false),
        new("platform.settings.edit", "Edit Platform Settings", "Modify platform configuration", "Platform Admin", 5, false),
        new("platform.analytics", "Platform Analytics", "View cross-tenant analytics", "Platform Admin", 6, false),
    };

    #endregion

    #region Role Definitions

    /// <summary>
    /// System role definitions with their permission assignments.
    /// These are global roles (OrganizationId = null) available to all tenants.
    /// </summary>
    private static readonly Dictionary<string, RoleDefinition> SystemRoles = new()
    {
        // ============================================
        // PLATFORM ADMIN (SuperAdmin)
        // For Q-Mgr platform operators managing all tenants
        // ============================================
        [RoleCodes.SuperAdmin] = new RoleDefinition(
            Name: "Platform Admin",
            Code: RoleCodes.SuperAdmin,
            Description: "Full platform administration access across all organizations. For Q-Mgr platform operators.",
            Color: "#FF0000",
            Icon: "shield-check",
            SortOrder: 0,
            IsSystemRole: true,
            Permissions: AllPermissions.Select(p => p.Code).ToArray() // ALL permissions
        ),

        // ============================================
        // TENANT ADMIN (Organization Admin)
        // For organization owners/administrators
        // ============================================
        [RoleCodes.Admin] = new RoleDefinition(
            Name: "Tenant Admin",
            Code: RoleCodes.Admin,
            Description: "Full administrative access within the organization. For business owners and IT administrators.",
            Color: "#9C27B0",
            Icon: "person-gear",
            SortOrder: 1,
            IsSystemRole: true,
            Permissions: AllPermissions
                .Where(p => p.IsVisible) // Exclude platform admin permissions
                .Where(p => !p.Code.StartsWith("tenants.") && !p.Code.StartsWith("system.") && !p.Code.StartsWith("platform."))
                .Select(p => p.Code)
                .ToArray()
        ),

        // ============================================
        // MANAGER
        // For branch managers and supervisors
        // ============================================
        [RoleCodes.Manager] = new RoleDefinition(
            Name: "Manager",
            Code: RoleCodes.Manager,
            Description: "Branch management and staff supervision. Can manage counters, service types, and view reports.",
            Color: "#2196F3",
            Icon: "person-badge",
            SortOrder: 2,
            IsSystemRole: true,
            Permissions: new[]
            {
                // Dashboard
                "dashboard.view",
                // Users (limited)
                "users.view", "users.create", "users.edit",
                // Branches (limited)
                "branches.view", "branches.edit",
                // Counters (full)
                "counters.view", "counters.create", "counters.edit", "counters.delete",
                // Service Types (full)
                "service-types.view", "service-types.create", "service-types.edit", "service-types.delete",
                // Queue (full)
                "queue.view", "queue.manage",
                // Tokens (full)
                "tokens.view", "tokens.create", "tokens.cancel",
                // Reports (full)
                "reports.view", "reports.export",
                // Content (full)
                "content.view", "content.create", "content.edit", "content.delete",
                // Feedback (full)
                "feedback.view", "feedback.respond", "feedback.analytics",
                // Visitor Management (full)
                "visitors.view", "visitors.checkin", "visitors.checkout", "visitors.manage",
                // Student Rosters (full)
                "students.view", "students.manage",
                // Marketing (full)
                "marketing.view", "marketing.manage", "marketing.send",
                // Settings (view only)
                "settings.view",
                // Notifications (view only)
                "notifications.view",
            }
        ),

        // ============================================
        // STAFF
        // For counter operators and service agents
        // ============================================
        [RoleCodes.Staff] = new RoleDefinition(
            Name: "Staff",
            Code: RoleCodes.Staff,
            Description: "Counter operations and queue management. Can call tokens and serve customers.",
            Color: "#4CAF50",
            Icon: "person",
            SortOrder: 3,
            IsSystemRole: true,
            Permissions: new[]
            {
                // Dashboard
                "dashboard.view",
                // Queue (full operational)
                "queue.view", "queue.manage",
                // Tokens (issue and view)
                "tokens.view", "tokens.create",
                // Reports (view only)
                "reports.view",
                // Feedback (view only)
                "feedback.view",
                // Visitor Management (front-desk operations)
                "visitors.view", "visitors.checkin", "visitors.checkout",
                // Student Rosters (search/lookup only — bulk import stays a manager+ action)
                "students.view",
            }
        ),

        // ============================================
        // VIEWER / CUSTOMER
        // For read-only access and customer self-service
        // Note: Customer role is merged with Viewer as per requirements
        // ============================================
        [RoleCodes.Viewer] = new RoleDefinition(
            Name: "Viewer",
            Code: RoleCodes.Viewer,
            Description: "Read-only access to dashboards, queue status, and reports. Also used for customer self-service portals.",
            Color: "#607D8B",
            Icon: "eye",
            SortOrder: 4,
            IsSystemRole: true,
            Permissions: new[]
            {
                // Standard viewer permissions
                "dashboard.view",
                "queue.view",
                "tokens.view",
                "reports.view",
                "feedback.view",
                // Customer-specific permissions (self-service)
                "customer.queue-status",
                "customer.feedback-submit",
                "customer.token-history",
                "customer.profile",
            }
        ),
    };

    #endregion

    #region Seeding Methods

    private async Task SeedPermissionsAsync()
    {
        var existingCodes = await _context.Permissions
            .Select(p => p.Code)
            .ToHashSetAsync();

        var permissionsToAdd = new List<Permission>();

        foreach (var permDef in AllPermissions)
        {
            if (!existingCodes.Contains(permDef.Code))
            {
                permissionsToAdd.Add(new Permission
                {
                    Id = Guid.NewGuid(),
                    Code = permDef.Code,
                    Name = permDef.Name,
                    Description = permDef.Description,
                    Category = permDef.Category,
                    SortOrder = permDef.SortOrder,
                    IsVisible = permDef.IsVisible,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        if (permissionsToAdd.Any())
        {
            _context.Permissions.AddRange(permissionsToAdd);
            await _context.SaveChangesAsync();
            _logger.LogInformation("Seeded {Count} new permissions", permissionsToAdd.Count);
        }
    }

    private async Task SeedSystemRolesAsync()
    {
        var existingCodes = await _context.Roles
            .Where(r => r.OrganizationId == null) // System roles only
            .Select(r => r.Code)
            .ToHashSetAsync();

        var rolesToAdd = new List<Role>();

        foreach (var (code, roleDef) in SystemRoles)
        {
            if (!existingCodes.Contains(code))
            {
                rolesToAdd.Add(new Role
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = null, // System role (global)
                    Code = roleDef.Code,
                    Name = roleDef.Name,
                    Description = roleDef.Description,
                    Color = roleDef.Color,
                    Icon = roleDef.Icon,
                    SortOrder = roleDef.SortOrder,
                    IsSystem = roleDef.IsSystemRole,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        if (rolesToAdd.Any())
        {
            _context.Roles.AddRange(rolesToAdd);
            await _context.SaveChangesAsync();
            _logger.LogInformation("Seeded {Count} new system roles", rolesToAdd.Count);
        }
    }

    private async Task SeedRolePermissionsAsync()
    {
        // Get all permissions and roles
        var permissionLookup = await _context.Permissions
            .ToDictionaryAsync(p => p.Code, p => p.Id);

        var roleLookup = await _context.Roles
            .Where(r => r.OrganizationId == null)
            .ToDictionaryAsync(r => r.Code, r => r.Id);

        // Get existing role-permission mappings
        var existingMappings = await _context.RolePermissions
            .Select(rp => new { rp.RoleId, rp.PermissionId })
            .ToListAsync();

        var existingSet = existingMappings
            .Select(rp => (rp.RoleId, rp.PermissionId))
            .ToHashSet();

        var mappingsToAdd = new List<RolePermission>();

        foreach (var (roleCode, roleDef) in SystemRoles)
        {
            if (!roleLookup.TryGetValue(roleCode, out var roleId))
                continue;

            foreach (var permCode in roleDef.Permissions)
            {
                if (!permissionLookup.TryGetValue(permCode, out var permId))
                {
                    _logger.LogWarning("Permission '{PermCode}' not found for role '{RoleCode}'", permCode, roleCode);
                    continue;
                }

                if (!existingSet.Contains((roleId, permId)))
                {
                    mappingsToAdd.Add(new RolePermission
                    {
                        RoleId = roleId,
                        PermissionId = permId,
                        GrantedAt = DateTime.UtcNow,
                        GrantedBy = null // System seeded
                    });
                }
            }
        }

        if (mappingsToAdd.Any())
        {
            _context.RolePermissions.AddRange(mappingsToAdd);
            await _context.SaveChangesAsync();
            _logger.LogInformation("Seeded {Count} new role-permission mappings", mappingsToAdd.Count);
        }
    }

    private async Task SeedPlatformAdminUserAsync()
    {
        // Check if platform admin user already exists. Runs before DbSeeder's own (separate,
        // idempotent) SuperAdmin seeding, so this is the one that actually wins the race on a
        // fresh install — its credentials must match DbSeeder's, or a fresh install would end up
        // with these stale ones instead.
        var platformAdminExists = await _context.Users
            .AnyAsync(u => u.Username == "superadmin" || u.Email == "support@getsacc.com");

        if (platformAdminExists)
        {
            _logger.LogDebug("Platform admin user already exists");
            return;
        }

        // Get super-admin role
        var superAdminRole = await _context.Roles
            .FirstOrDefaultAsync(r => r.Code == RoleCodes.SuperAdmin && r.OrganizationId == null);

        if (superAdminRole == null)
        {
            _logger.LogError("super-admin role not found - cannot create platform admin user");
            return;
        }

        // Ensure platform organization exists
        var platformOrgId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var platformOrg = await _context.Organizations.FindAsync(platformOrgId);

        if (platformOrg == null)
        {
            platformOrg = new Domain.Entities.Organization.Organization
            {
                Id = platformOrgId,
                Name = "Platform Administration",
                BrandName = "Q-Mgr Platform",
                ContactEmail = "admin@qmgr.platform",
                Slug = "platform",
                Status = TenantStatus.Active,
                Tier = TenantTier.Enterprise,
                OnboardingCompleted = true,
                VerifiedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            _context.Organizations.Add(platformOrg);
            await _context.SaveChangesAsync();
            _logger.LogInformation("Created platform organization");
        }

        // Create platform admin user
        var platformAdmin = new User
        {
            Id = Guid.NewGuid(),
            OrganizationId = platformOrgId,
            Username = "superadmin",
            Email = "support@getsacc.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
            FirstName = "Platform",
            LastName = "Administrator",
            RoleId = superAdminRole.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(platformAdmin);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Platform admin user seeded. Username: superadmin, Email: support@getsacc.com");
    }

    #endregion

    #region Helper Records

    private record PermissionDefinition(
        string Code,
        string Name,
        string Description,
        string Category,
        int SortOrder,
        bool IsVisible = true
    );

    private record RoleDefinition(
        string Name,
        string Code,
        string Description,
        string Color,
        string Icon,
        int SortOrder,
        bool IsSystemRole,
        string[] Permissions
    );

    #endregion
}

/// <summary>
/// Extension methods for RbacSeeder registration
/// </summary>
public static class RbacSeederExtensions
{
    /// <summary>
    /// Seeds RBAC data (permissions, roles, and role-permission mappings)
    /// </summary>
    public static async Task SeedRbacAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<QMgrDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RbacSeeder>>();

        var seeder = new RbacSeeder(context, logger);
        await seeder.SeedAsync();
    }
}
