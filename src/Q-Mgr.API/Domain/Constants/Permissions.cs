namespace QMgr.Domain.Constants;

/// <summary>
/// Permission constants for RBAC. These are used throughout the application
/// for authorization checks and are seeded to the database.
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

    // Queue Operations
    public const string QueueView = "queue.view";
    public const string QueueManage = "queue.manage"; // Call next, complete, transfer, etc.

    // Tokens
    public const string TokensView = "tokens.view";
    public const string TokensCreate = "tokens.create";
    public const string TokensCancel = "tokens.cancel";

    // Reports
    public const string ReportsView = "reports.view";
    public const string ReportsExport = "reports.export";

    // Content Management
    public const string ContentView = "content.view";
    public const string ContentCreate = "content.create";
    public const string ContentEdit = "content.edit";
    public const string ContentDelete = "content.delete";

    // Feedback
    public const string FeedbackView = "feedback.view";
    public const string FeedbackRespond = "feedback.respond";
    public const string FeedbackAnalytics = "feedback.analytics";

    // Visitor Management
    public const string VisitorsView = "visitors.view";
    public const string VisitorsCheckIn = "visitors.checkin";
    public const string VisitorsCheckOut = "visitors.checkout";
    public const string VisitorsManage = "visitors.manage"; // Edit details, delete, watchlist flag

    // Marketing (contacts + broadcast campaigns)
    public const string MarketingView = "marketing.view";
    public const string MarketingManage = "marketing.manage"; // Manage contacts, create/edit broadcast drafts
    public const string MarketingSend = "marketing.send"; // Schedule/send a broadcast

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

    // Billing (SaaS)
    public const string BillingView = "billing.view";
    public const string BillingManage = "billing.manage";

    // Customer-facing (self-service)
    public const string CustomerQueueStatus = "customer.queue-status";
    public const string CustomerFeedbackSubmit = "customer.feedback-submit";
    public const string CustomerTokenHistory = "customer.token-history";
    public const string CustomerProfile = "customer.profile";

    // SuperAdmin only (Platform Admin)
    public const string PlatformAdmin = "platform.admin";
    public const string TenantsView = "tenants.view";
    public const string TenantsManage = "tenants.manage";
    public const string SystemSettings = "system.settings";
    public const string PlatformSettingsView = "platform.settings.view";
    public const string PlatformSettingsEdit = "platform.settings.edit";
    public const string PlatformAnalytics = "platform.analytics";

    /// <summary>
    /// All permissions grouped by category for seeding and UI display
    /// </summary>
    public static readonly List<PermissionDefinition> All = new()
    {
        // Dashboard
        new("dashboard.view", "View Dashboard", "Access the main dashboard", "Dashboard", 1),

        // User Management
        new("users.view", "View Users", "View user list and details", "User Management", 1),
        new("users.create", "Create Users", "Create new users", "User Management", 2),
        new("users.edit", "Edit Users", "Edit user details", "User Management", 3),
        new("users.delete", "Delete Users", "Delete users", "User Management", 4),

        // Role Management
        new("roles.view", "View Roles", "View roles and permissions", "Role Management", 1),
        new("roles.create", "Create Roles", "Create new roles", "Role Management", 2),
        new("roles.edit", "Edit Roles", "Edit roles and permissions", "Role Management", 3),
        new("roles.delete", "Delete Roles", "Delete roles", "Role Management", 4),

        // Branch Management
        new("branches.view", "View Branches", "View branch list and details", "Branch Management", 1),
        new("branches.create", "Create Branches", "Create new branches", "Branch Management", 2),
        new("branches.edit", "Edit Branches", "Edit branch details and settings", "Branch Management", 3),
        new("branches.delete", "Delete Branches", "Delete branches", "Branch Management", 4),

        // Counter Management
        new("counters.view", "View Counters", "View counter list and details", "Counter Management", 1),
        new("counters.create", "Create Counters", "Create new counters", "Counter Management", 2),
        new("counters.edit", "Edit Counters", "Edit counter details", "Counter Management", 3),
        new("counters.delete", "Delete Counters", "Delete counters", "Counter Management", 4),

        // Service Type Management
        new("service-types.view", "View Service Types", "View service type list", "Service Types", 1),
        new("service-types.create", "Create Service Types", "Create new service types", "Service Types", 2),
        new("service-types.edit", "Edit Service Types", "Edit service type details", "Service Types", 3),
        new("service-types.delete", "Delete Service Types", "Delete service types", "Service Types", 4),

        // Queue Operations
        new("queue.view", "View Queue", "View queue status and tokens", "Queue Operations", 1),
        new("queue.manage", "Manage Queue", "Call next, complete, transfer tokens", "Queue Operations", 2),

        // Token Management
        new("tokens.view", "View Tokens", "View token list and details", "Token Management", 1),
        new("tokens.create", "Create Tokens", "Create/issue new tokens", "Token Management", 2),
        new("tokens.cancel", "Cancel Tokens", "Cancel tokens", "Token Management", 3),

        // Reports
        new("reports.view", "View Reports", "View reports and analytics", "Reports", 1),
        new("reports.export", "Export Reports", "Export reports to files", "Reports", 2),

        // Content Management
        new("content.view", "View Content", "View media and playlists", "Content Management", 1),
        new("content.create", "Create Content", "Upload media and create playlists", "Content Management", 2),
        new("content.edit", "Edit Content", "Edit media and playlists", "Content Management", 3),
        new("content.delete", "Delete Content", "Delete media and playlists", "Content Management", 4),

        // Feedback
        new("feedback.view", "View Feedback", "View customer feedback", "Feedback", 1),
        new("feedback.respond", "Respond to Feedback", "Reply to customer feedback", "Feedback", 2),
        new("feedback.analytics", "Feedback Analytics", "View feedback analytics", "Feedback", 3),

        // Visitor Management
        new("visitors.view", "View Visitors", "View visitor log and details", "Visitor Management", 1),
        new("visitors.checkin", "Check In Visitors", "Pre-register and check in visitors", "Visitor Management", 2),
        new("visitors.checkout", "Check Out Visitors", "Check out visitors", "Visitor Management", 3),
        new("visitors.manage", "Manage Visitors", "Edit visitor details, delete records, manage watchlist", "Visitor Management", 4),

        // Marketing
        new("marketing.view", "View Marketing", "View contacts and broadcast campaigns", "Marketing", 1),
        new("marketing.manage", "Manage Marketing", "Manage contacts and create broadcast drafts", "Marketing", 2),
        new("marketing.send", "Send Broadcasts", "Schedule or send broadcast campaigns", "Marketing", 3),

        // Settings
        new("settings.view", "View Settings", "View organization settings", "Settings", 1),
        new("settings.edit", "Edit Settings", "Modify organization settings", "Settings", 2),

        // Notifications
        new("notifications.view", "View Notifications", "View notification settings", "Notifications", 1),
        new("notifications.manage", "Manage Notifications", "Configure notification settings", "Notifications", 2),

        // API Clients
        new("api-clients.view", "View API Clients", "View API client list", "API Clients", 1),
        new("api-clients.create", "Create API Clients", "Create new API clients", "API Clients", 2),
        new("api-clients.edit", "Edit API Clients", "Edit API client details", "API Clients", 3),
        new("api-clients.delete", "Delete API Clients", "Delete API clients", "API Clients", 4),

        // Billing
        new("billing.view", "View Billing", "View subscription and invoices", "Billing", 1),
        new("billing.manage", "Manage Billing", "Manage subscription and payment methods", "Billing", 2),

        // Customer-facing (self-service)
        new("customer.queue-status", "View Queue Status", "View current queue position and wait times", "Customer", 1),
        new("customer.feedback-submit", "Submit Feedback", "Submit feedback after service", "Customer", 2),
        new("customer.token-history", "View Token History", "View own token history", "Customer", 3),
        new("customer.profile", "Manage Profile", "View and edit own profile", "Customer", 4),

        // Platform Administration (SuperAdmin only)
        new("platform.admin", "Platform Administrator", "Full platform administration access", "Platform Admin", 0, false),
        new("tenants.view", "View Tenants", "View all organizations (platform admin)", "Platform Admin", 1, false),
        new("tenants.manage", "Manage Tenants", "Manage organizations (platform admin)", "Platform Admin", 2, false),
        new("system.settings", "System Settings", "Configure platform settings (platform admin)", "Platform Admin", 3, false),
        new("platform.settings.view", "View Platform Settings", "View platform-wide configuration", "Platform Admin", 4, false),
        new("platform.settings.edit", "Edit Platform Settings", "Modify platform-wide configuration", "Platform Admin", 5, false),
        new("platform.analytics", "Platform Analytics", "View cross-tenant analytics", "Platform Admin", 6, false),
    };

    /// <summary>
    /// Default role definitions with their permissions
    /// </summary>
    public static readonly Dictionary<string, RoleDefinition> DefaultRoles = new()
    {
        [RoleCodes.SuperAdmin] = new RoleDefinition(
            "SuperAdmin",
            RoleCodes.SuperAdmin,
            "Full system access across all organizations",
            "#FF0000",
            "shield-account",
            0,
            All.Select(p => p.Code).ToArray() // All permissions
        ),

        [RoleCodes.Admin] = new RoleDefinition(
            "Admin",
            RoleCodes.Admin,
            "Full access within organization",
            "#9C27B0",
            "account-cog",
            1,
            All.Where(p => p.IsVisible && !p.Code.StartsWith("tenants.") && !p.Code.StartsWith("system."))
                .Select(p => p.Code).ToArray()
        ),

        [RoleCodes.Manager] = new RoleDefinition(
            "Manager",
            RoleCodes.Manager,
            "Manage branches, counters, and staff",
            "#2196F3",
            "account-supervisor",
            2,
            new[]
            {
                DashboardView,
                UsersView, UsersCreate, UsersEdit,
                BranchesView, BranchesEdit,
                CountersView, CountersCreate, CountersEdit, CountersDelete,
                ServiceTypesView, ServiceTypesCreate, ServiceTypesEdit, ServiceTypesDelete,
                QueueView, QueueManage,
                TokensView, TokensCreate, TokensCancel,
                ReportsView, ReportsExport,
                ContentView, ContentCreate, ContentEdit, ContentDelete,
                FeedbackView, FeedbackRespond, FeedbackAnalytics,
                VisitorsView, VisitorsCheckIn, VisitorsCheckOut, VisitorsManage,
                MarketingView, MarketingManage, MarketingSend,
                SettingsView,
                NotificationsView,
            }
        ),

        [RoleCodes.Staff] = new RoleDefinition(
            "Staff",
            RoleCodes.Staff,
            "Counter operations and queue management",
            "#4CAF50",
            "account",
            3,
            new[]
            {
                DashboardView,
                QueueView, QueueManage,
                TokensView, TokensCreate,
                ReportsView,
                FeedbackView,
                VisitorsView, VisitorsCheckIn, VisitorsCheckOut,
            }
        ),

        [RoleCodes.Viewer] = new RoleDefinition(
            "Viewer",
            RoleCodes.Viewer,
            "View-only access to reports and dashboards. Also used for customer self-service.",
            "#607D8B",
            "eye",
            4,
            new[]
            {
                DashboardView,
                QueueView,
                TokensView,
                ReportsView,
                FeedbackView,
                // Customer-specific permissions (self-service)
                CustomerQueueStatus,
                CustomerFeedbackSubmit,
                CustomerTokenHistory,
                CustomerProfile,
            }
        ),
    };
}

/// <summary>
/// Permission definition for seeding
/// </summary>
public record PermissionDefinition(
    string Code,
    string Name,
    string Description,
    string Category,
    int SortOrder,
    bool IsVisible = true
);

/// <summary>
/// Role definition for seeding
/// </summary>
public record RoleDefinition(
    string Name,
    string Code,
    string Description,
    string Color,
    string Icon,
    int SortOrder,
    string[] Permissions
);
