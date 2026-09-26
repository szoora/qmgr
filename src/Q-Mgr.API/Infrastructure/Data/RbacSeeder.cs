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
        new("users.approve", "Approve Join Requests", "Approve or reject staff who registered through the join link, and assign their role", "User Management", 5, true),

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
        // DOCUMENT LIBRARY & SECURE SHARING
        // Publishing is the boundary: nothing is shared in place. Being able to read a report is
        // not the same as being able to turn it into a document that leaves the building, and an
        // access log is a record of people's behaviour, so reading it is narrower than issuing a
        // link. See docs/plans/SECURE_DOCUMENT_SHARING.md §7.
        // ============================================
        new("library.publish", "Publish to Document Library", "Mark a document as shareable, and publish a generated report into the Library", "Document Library", 1, true),
        new("documents.share.create", "Create Share Links", "Issue a secure link to a Library document, with its expiry, passcode and download rules", "Document Library", 2, true),
        new("documents.share.manage", "Manage Share Links", "Edit or revoke any share link in the organization, and set the sharing policy", "Document Library", 3, true),
        new("documents.share.audit", "View Share Activity", "See who opened a shared document, when, from where, and which pages they read", "Document Library", 4, true),

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
        new("classes.teachers.manage", "Manage Class Teachers", "Assign and end class-teacher assignments, and edit their contact details", "Student Rosters", 3, true),

        // ============================================
        // STUDENT WELFARE LEDGER
        // ============================================
        new("welfare.view", "View Welfare Records", "View non-confidential achievement, behavior, and welfare records", "Student Welfare", 1, true),
        new("welfare.create", "Log Welfare Records", "Log an achievement, behavior incident, or welfare concern", "Student Welfare", 2, true),
        new("welfare.edit", "Add Welfare Follow-Up Notes", "Add a follow-up note to an existing record (records are never rewritten)", "Student Welfare", 3, true),
        new("welfare.notify", "Notify Guardians", "Review and send a guardian notification for a welfare record", "Student Welfare", 4, true),
        new("welfare.confidential.view", "View Confidential Welfare Records", "View Welfare-tier (safeguarding) records — a smaller audience than general behavior records by design", "Student Welfare", 5, true),
        new("welfare.restricted.view", "View Restricted Welfare Information", "View and set administrator-only restricted records, flags, and student notes", "Student Welfare", 6, true),
        new("welfare.categories.manage", "Manage Welfare Categories", "Define the achievement/behavior/welfare categories staff can log against", "Student Welfare", 7, true),
        new("welfare.reports.view", "View Welfare Reports", "View trend and process-consistency reports across welfare records", "Student Welfare", 8, true),
        new("welfare.reports.own", "View Own Class Reports", "The same welfare reports, narrowed to the classes this user holds — revoke it to withhold the page from class teachers", "Student Welfare", 9, true),
        new("welfare.reports.aggregate", "View Welfare Figures", "Welfare counts by category and cohort, with no child and no member of staff named — for governors", "Student Welfare", 10, true),

        // ============================================
        // STAFF PERFORMANCE MONITOR (2026-09-16) — mirrored in Permissions.All and the Web copy
        // ============================================
        new("staff.records.view", "View Staff Records", "Read performance records about other staff, within the role's staff scope", "Staff Performance", 1, true),
        new("staff.records.create", "Log Staff Records", "Log attendance, duties, observations, contributions and conduct about staff within scope", "Staff Performance", 2, true),
        new("staff.records.edit", "Follow Up Staff Records", "Add notes, annul a record with a reason, correct points — records are never rewritten", "Staff Performance", 3, true),
        new("staff.confidential.view", "View Confidential Staff Records", "Observations, welfare-of-staff records and appraisals — a smaller audience by design", "Staff Performance", 4, true),
        new("staff.restricted.view", "View Restricted Staff Records", "Administrator-only records: an investigation or a grievance", "Staff Performance", 5, true),
        new("staff.duties.manage", "Manage Staff Duties", "Create meetings, exam and prep supervision slots and lessons, and name who takes the register", "Staff Performance", 6, true),
        new("staff.parameters.manage", "Manage Performance Parameters", "Define what is measured, its points, weight and rubric, and the scoring policy", "Staff Performance", 7, true),
        new("staff.appraisals.conduct", "Conduct Appraisals", "Act as appraiser: set targets, review a self-assessment, rate", "Staff Performance", 8, true),
        new("staff.appraisals.approve", "Approve Appraisals", "Open a period's appraisals, moderate ratings and sign them off", "Staff Performance", 9, true),
        new("staff.reports.view", "View Staff Performance Reports", "Bands, attendance, observer dispersion and who-logs-what, scoped to the caller's departments", "Staff Performance", 10, true),
        new("staff.notices.manage", "Publish Staff Notices", "Publish notices to a branch, departments, roles or a staff group", "Staff Performance", 11, true),
        new("staff.structure.manage", "Manage Staff Structure", "Departments, heads of department, line managers and the staff import", "Staff Performance", 12, true),
        new("staff.recognition.give", "Give Recognition", "Recognise a colleague, within the monthly budget", "Staff Performance", 13, true),
        // Duty rota plan (2026-09-17) — mirrored in Permissions.All and the Web copy
        new("staff.dutyreports.view", "View Duty Reports", "Read the reports written by staff and administrators on duty, within the role's staff scope", "Staff Performance", 14, true),
        new("staff.dutyreports.review", "Review Duty Reports", "Comment on duty reports, return them for changes and mark them reviewed", "Staff Performance", 15, true),
        new("timetable.manage", "Manage the Timetable", "Build, check and publish the timetable, bell schedule and rooms — the timetable master", "Staff Performance", 16, true),
        new("timetable.lessons.flag", "Flag Lessons", "Confirm or override lessons taught, missed and recovered for the staff in scope", "Staff Performance", 17, true),
        // Lesson plans (2026-09-26) — mirrored in Permissions.All and the Web copy
        new("teaching.plans.review", "Review Lesson Plans", "Forward or return the lesson plans and schemes of work of a department (the head of department's post grants it)", "Staff Performance", 18, true),
        new("teaching.plans.approve", "Approve Lesson Plans", "Approve or return lesson plans and schemes of work forwarded by heads of department", "Staff Performance", 19, true),
        new("teaching.plans.view", "View Lesson Plans", "Read approved lesson plans and schemes of work across the school, and the planning reports", "Staff Performance", 20, true),
        // School calendar (2026-09-23) — mirrored in Permissions.All and the Web copy
        new("calendar.manage", "Manage the School Calendar", "Create and edit school events and import the term programme, meetings and duty rotas", "Calendar", 1, true),

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
        new("platform.docs.view", "View Docs Articles", "View onboarding/docs articles (platform admin)", "Platform Admin", 7, false),
        new("platform.docs.manage", "Manage Docs Articles", "Create, edit, publish, and delete onboarding/docs articles", "Platform Admin", 8, false),
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
            Description: "Full platform administration access across all organizations. For the platform's own operators.",
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
            Name: "Administrator",
            Code: RoleCodes.Admin,
            Description: "Everything in the organisation, including billing, settings and what each role may do. For the owner or IT administrator; a school's head holds the Head Teacher role.",
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
            // Display name and description changed 2026-09-24: "Manager" read as a school's management, and a school
            // may not use this role at all — it is the front office. The CODE is a wire format and does not move.
            Name: "Front Office Manager",
            Code: RoleCodes.Manager,
            Description: "Runs the front office: visitors, the queue and counters, signage and broadcasts, feedback, and the student roll. Outside a school's teaching hierarchy — it reads no staff records and appraises nobody.",
            Color: "#2196F3",
            Icon: "person-badge",
            SortOrder: 2,
            IsSystemRole: true,
            // Staff axis: a manager holds the portal only (plan §5.1) — they can be appraised, not appraise.
            StaffScope: StaffDataScope.SelfOnly,
            Permissions: new[]
            {
                // Dashboard
                "dashboard.view",
                // Users (limited). users.approve: a manager can admit a join request, at or below their own rank.
                "users.view", "users.create", "users.edit", "users.approve",
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
                // Document Library: a manager publishes and issues links. The ACTIVITY log is
                // deliberately not here — it is a record of named people's reading and stays
                // with the Tenant Admin unless a custom role is granted it.
                "library.publish", "documents.share.create", "documents.share.manage",
                // Feedback (full)
                "feedback.view", "feedback.respond", "feedback.analytics",
                // Visitor Management (full)
                "visitors.view", "visitors.checkin", "visitors.checkout", "visitors.manage",
                // School calendar
                "calendar.manage",
                // Student Rosters (full)
                "students.view", "students.manage", "classes.teachers.manage",
                // Student Welfare Ledger (full, except confidential-tier — see the welfare-plan's
                // "configurable, off by default" note: an Admin can grant welfare.confidential.view
                // to a custom role, e.g. a "Counselor" role, via the existing custom-roles feature)
                "welfare.view", "welfare.create", "welfare.edit", "welfare.notify", "welfare.reports.view",
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
            // "Staff" beside Teacher and Support Staff in a school's role list read as "the staff". It is the front desk.
            Name: "Front Desk Staff",
            Code: RoleCodes.Staff,
            Description: "Front-desk work: the queue, tickets and visitors, and logging a welfare concern. Not teaching staff — a teacher holds the Teacher role.",
            Color: "#4CAF50",
            Icon: "person",
            SortOrder: 3,
            IsSystemRole: true,
            StaffScope: StaffDataScope.SelfOnly,
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
                // Student Welfare Ledger (log and notify, front-line staff — no confidential-tier
                // view, no editing categories, no reports)
                "welfare.view", "welfare.create", "welfare.notify",
            }
        ),

        // ============================================
        // STAFF PERFORMANCE MONITOR HIERARCHY (2026-09-16)
        // Director of Studies · Academic Assistant · Head of Department · Teacher · Support Staff.
        // All five rank below Manager in RoleCodes.All (plan §13 decision 1). What defines a head of
        // department is not the permission set but StaffScope.AssignedDepartments: a head with no
        // department sees nobody, as a class teacher with no class sees no students.
        // ============================================
        // ============================================
        // THE SCHOOL CHAIN (2026-09-24): Head Teacher, then Deputy, then Director of Studies.
        // Their permission sets live in Permissions.cs, ONE definition read by both seeders.
        // ============================================
        [RoleCodes.HeadTeacher] = new RoleDefinition(
            Name: "Head Teacher",
            Code: RoleCodes.HeadTeacher,
            Description: "The school's accountable head. Everything the school runs, including restricted welfare and staff records; payments, system settings, API keys and what a role grants stay with the Administrator.",
            Color: "#5B1E36",
            Icon: "award",
            SortOrder: 2,
            IsSystemRole: true,
            Permissions: QMgr.Domain.Constants.Permissions.HeadTeacherPermissions(AllPermissions.Select(p => (p.Code, p.IsVisible))),
            DataScope: RoleDataScope.Organization,
            StaffScope: StaffDataScope.Organization
        ),

        [RoleCodes.DeputyHeadTeacher] = new RoleDefinition(
            Name: "Deputy Head Teacher",
            Code: RoleCodes.DeputyHeadTeacher,
            Description: "Runs the school day. Everything the Director of Studies holds, plus student welfare to the confidential tier, the roll, class teachers, visitors and staff onboarding.",
            Color: "#6C2A45",
            Icon: "person-up",
            SortOrder: 3,
            IsSystemRole: true,
            Permissions: QMgr.Domain.Constants.Permissions.DeputyHeadTeacherPermissions,
            DataScope: RoleDataScope.Organization,
            StaffScope: StaffDataScope.Organization
        ),

        [RoleCodes.DirectorOfStudies] = new RoleDefinition(
            Name: "Director of Studies",
            Code: RoleCodes.DirectorOfStudies,
            Description: "Academic head. Sees every member of staff; conducts and approves appraisals; owns the parameters and notices.",
            Color: "#7A2847",
            Icon: "mortarboard",
            SortOrder: 3,
            IsSystemRole: true,
            Permissions: new[]
            {
                "dashboard.view", "notifications.view",
                "users.view",
                "students.view", "welfare.view", "welfare.reports.view",
                "staff.records.view", "staff.records.create", "staff.records.edit", "staff.confidential.view",
                "staff.duties.manage", "staff.parameters.manage",
                "staff.appraisals.conduct", "staff.appraisals.approve",
                "staff.reports.view", "staff.notices.manage", "staff.structure.manage", "staff.recognition.give",
                // Duty rota plan §15 decisions 4 and 10: the DoS reads and reviews duty reports, is a timetable
                // master, and supervises lessons school-wide.
                "staff.dutyreports.view", "staff.dutyreports.review", "timetable.manage", "timetable.lessons.flag",
                "teaching.plans.approve", "teaching.plans.view",
                "calendar.manage",
            },
            DataScope: RoleDataScope.Organization,
            StaffScope: StaffDataScope.Organization
        ),

        [RoleCodes.AcademicAssistant] = new RoleDefinition(
            Name: "Academic Assistant",
            Code: RoleCodes.AcademicAssistant,
            Description: "Timetables duties and takes registers across the school. No appraisal sign-off and no confidential rung.",
            Color: "#5A9C92",
            Icon: "calendar-check",
            SortOrder: 3,
            IsSystemRole: true,
            Permissions: new[]
            {
                "dashboard.view", "notifications.view",
                "users.view",
                "staff.records.view", "staff.records.create",
                "staff.duties.manage",
                "staff.reports.view", "staff.notices.manage", "staff.recognition.give",
                // Duty rota plan §15 decision 10: the academic assistant is a timetable master and reads duty reports.
                "staff.dutyreports.view", "timetable.manage", "timetable.lessons.flag",
                "teaching.plans.approve", "teaching.plans.view",
                "calendar.manage",
            },
            DataScope: RoleDataScope.Organization,
            StaffScope: StaffDataScope.Organization
        ),


        [RoleCodes.Teacher] = new RoleDefinition(
            Name: "Teacher",
            Code: RoleCodes.Teacher,
            Description: "The staff portal and recognition. Sees the students of the classes they teach, at the teaching tier. Logs records only as the named recorder on a duty.",
            Color: "#3F8A80",
            Icon: "person-workspace",
            SortOrder: 4,
            IsSystemRole: true,
            Permissions: new[]
            {
                "dashboard.view", "notifications.view",
                "staff.recognition.give",
                // Duty rota plan §5.3: students.view ONLY — never a welfare permission — and only together with
                // AssignedClasses below, which moved FIRST: granting students.view on an Organization scope
                // would have shown every teacher the whole school.
                "students.view",
            },
            DataScope: RoleDataScope.AssignedClasses,
            StaffScope: StaffDataScope.SelfOnly
        ),

        [RoleCodes.SupportStaff] = new RoleDefinition(
            Name: "Support Staff",
            Code: RoleCodes.SupportStaff,
            Description: "The staff portal and recognition; appraised by their line manager.",
            Color: "#8A7A81",
            Icon: "person-gear",
            SortOrder: 4,
            IsSystemRole: true,
            Permissions: new[]
            {
                "dashboard.view", "notifications.view",
                "staff.recognition.give",
            },
            DataScope: RoleDataScope.Organization,
            StaffScope: StaffDataScope.SelfOnly,
            StaffGroup: StaffGroups.Support
        ),

        // ============================================
        // VIEWER / CUSTOMER
        // For read-only access and customer self-service
        // Note: Customer role is merged with Viewer as per requirements
        // ============================================
        // Board Member (2026-09-24): the welfare FIGURES, never a named child. Set in Permissions.cs, read here.
        [RoleCodes.BoardMember] = new RoleDefinition(
            Name: "Board Member",
            Code: RoleCodes.BoardMember,
            Description: "A governor or proprietor's representative: the dashboard and the school's welfare figures, with no child and no member of staff named.",
            Color: "#475569",
            Icon: "bank",
            SortOrder: 4,
            IsSystemRole: true,
            Permissions: QMgr.Domain.Constants.Permissions.BoardMemberPermissions,
            DataScope: RoleDataScope.Organization,
            StaffScope: StaffDataScope.SelfOnly
        ),

        [RoleCodes.Viewer] = new RoleDefinition(
            Name: "Viewer",
            Code: RoleCodes.Viewer,
            Description: "Read-only access to dashboards, queue status, and reports. Also used for customer self-service portals.",
            Color: "#607D8B",
            Icon: "eye",
            SortOrder: 4,
            IsSystemRole: true,
            StaffScope: StaffDataScope.SelfOnly,
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
        var existing = await _context.Roles
            .Where(r => r.OrganizationId == null) // System roles only
            .ToDictionaryAsync(r => r.Code);

        var rolesToAdd = new List<Role>();

        foreach (var (code, roleDef) in SystemRoles)
        {
            if (!existing.ContainsKey(code))
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
                    DataScope = roleDef.DataScope,
                    StaffScope = roleDef.StaffScope,
                    StaffGroup = roleDef.StaffGroup,
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

        // A SYSTEM role's data scope is not a tenant's to change, so unlike name/colour it is
        // repaired rather than left alone. Without this, a class-teacher row created before the
        // column existed (or edited by hand) would keep DataScope=Organization and silently see
        // the whole school — the exact fail-open this feature exists to prevent. Permissions are
        // already repaired the same way by SeedRolePermissionsAsync.
        var repaired = 0;
        foreach (var (code, roleDef) in SystemRoles)
        {
            if (!existing.TryGetValue(code, out var role)) continue;
            if (role.DataScope != roleDef.DataScope)
            {
                role.DataScope = roleDef.DataScope;
                repaired++;
            }
            // The staff axis is repaired the same way and for the same reason: a teacher row created
            // before the column existed would read StaffScope=Organization and see every colleague.
            if (role.StaffScope != roleDef.StaffScope)
            {
                role.StaffScope = roleDef.StaffScope;
                repaired++;
            }
            // And the staff GROUP, for the same reason: a role row created before 2026-09-22 has
            // none, and a null there resolves to the tenant's first group rather than the one the
            // role actually belongs to — which is how support staff came to be scored on Lesson
            // Attendance in the first place.
            if (!string.Equals(role.StaffGroup, roleDef.StaffGroup, StringComparison.Ordinal))
            {
                role.StaffGroup = roleDef.StaffGroup;
                repaired++;
            }
            // The DISPLAY NAME and description are repaired too, since 2026-09-18. They were
            // deliberately left alone before, on the reasoning that a tenant might have customised
            // them — but a SYSTEM role cannot be customised: RolesController.UpdateRole refuses one
            // outright ("System roles cannot be modified"), and the UI offers View Permissions
            // rather than Edit. So the only thing a stale name can be is a seeder value nobody
            // reconciled, and without this a rename here reaches a FRESH install only and silently
            // leaves every existing one — including production — reading the old label.
            // This is what made "Tenant Admin" -> "Administrator" land everywhere rather than
            // nowhere (user: "using the word Tenant... does not make much sense to an ordinary
            // user").
            if (role.Name != roleDef.Name)
            {
                role.Name = roleDef.Name;
                repaired++;
            }
            if (role.Description != roleDef.Description)
            {
                role.Description = roleDef.Description;
                repaired++;
            }
        }

        if (repaired > 0)
        {
            await _context.SaveChangesAsync();
            _logger.LogWarning("Repaired {Count} system role field(s) (scope, name, description)", repaired);
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

        await RetireReplacedGrantsAsync(permissionLookup, roleLookup);
    }

    /// <summary>
    /// This seeder only ever ADDS a mapping, which is right — a tenant's own extra grant on a system role
    /// must survive a restart. But a code that has been REPLACED has to go, or the replacement is pointless:
    /// the old grant still opens the endpoint and revoking the new one changes nothing.
    ///
    /// One entry so far. <c>welfare.reports.view</c> was split on 2026-09-18 so a school can withhold the
    /// Welfare Reports page from a class-teacher role without touching what a manager reads; the class
    /// teacher now holds <c>welfare.reports.own</c>, which opens the same three reports and nothing wider.
    /// Existing tenants seeded before that date hold both, and the broad one is what has to be retired.
    ///
    /// **Only add a pair here when one code genuinely replaces another for that role.** It removes a real
    /// grant from a real tenant, so it is not the place for "this role probably should not have that".
    /// </summary>
    private static readonly (string RoleCode, string Retire, string ReplacedBy)[] ReplacedGrants =
    {
        ("class-teacher", "welfare.reports.view", "welfare.reports.own"),
    };

    private async Task RetireReplacedGrantsAsync(
        Dictionary<string, Guid> permissionLookup, Dictionary<string, Guid> roleLookup)
    {
        foreach (var (roleCode, retire, replacedBy) in ReplacedGrants)
        {
            if (!roleLookup.TryGetValue(roleCode, out var roleId)) continue;
            if (!permissionLookup.TryGetValue(retire, out var retireId)) continue;
            // Never strip the old grant until the replacement is actually in place for that role,
            // or a half-applied startup leaves the role unable to read its own reports.
            if (!permissionLookup.TryGetValue(replacedBy, out var replacementId)) continue;

            var hasReplacement = await _context.RolePermissions
                .AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == replacementId);
            if (!hasReplacement) continue;

            var stale = await _context.RolePermissions
                .Where(rp => rp.RoleId == roleId && rp.PermissionId == retireId)
                .ToListAsync();
            if (stale.Count == 0) continue;

            _context.RolePermissions.RemoveRange(stale);
            await _context.SaveChangesAsync();
            _logger.LogWarning("Retired '{Retire}' from system role '{RoleCode}' — replaced by '{ReplacedBy}'",
                retire, roleCode, replacedBy);
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
                BrandName = null,
                ContactEmail = "admin@qmgr.platform",
                Slug = "platform",
                Status = TenantStatus.Active,
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
        string[] Permissions,
        RoleDataScope DataScope = RoleDataScope.Organization,
        StaffDataScope StaffScope = StaffDataScope.Organization,
        // Which staff group a role's holders are in. Only support-staff differs, which is exactly
        // what the old roleCode == "support-staff" test resolved to — so seeding these moves no score.
        string StaffGroup = StaffGroups.Teaching
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
