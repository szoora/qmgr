namespace QMgr.Domain.Constants;

/// <summary>
/// Role code constants for consistent role identification across the application.
/// These codes match the Role.Code values in the database and JWT claims.
///
/// IMPORTANT: Always use these constants instead of string literals to prevent
/// case sensitivity and typo issues (e.g., "SuperAdmin" vs "super-admin").
/// </summary>
public static class RoleCodes
{
    /// <summary>
    /// Platform Administrator - Full access across all organizations.
    /// This role bypasses all permission checks.
    /// </summary>
    public const string SuperAdmin = "super-admin";

    /// <summary>
    /// Tenant Administrator - Full access within their organization.
    /// Cannot access platform-level features or other organizations.
    /// </summary>
    public const string Admin = "admin";

    /// <summary>
    /// Manager - Branch management and staff supervision.
    /// Can manage counters, service types, and view reports.
    /// </summary>
    public const string Manager = "manager";

    /// <summary>
    /// Staff - Counter operations and queue management.
    /// Can call tokens, serve customers, and view basic reports.
    /// </summary>
    public const string Staff = "staff";

    /// <summary>
    /// Class Teacher - Pastoral responsibility for one or more classes.
    ///
    /// Unlike every other role here, this one carries a DATA SCOPE as well as a permission set: its
    /// seeded role row has <see cref="QMgr.Domain.Enums.RoleDataScope.AssignedClasses"/>, so its
    /// holder sees only students whose ClassName matches a live ClassTeacherAssignment of theirs.
    /// See StudentScopeService — the scope is enforced there, never by comparing against this code.
    /// </summary>
    /// <summary>
    /// <b>RETIRED 2026-09-22 — no role row carries this code any more.</b> Kept only so the
    /// migration that removed it, and anything reading historical data, still compiles.
    ///
    /// <para>It was deleted because it never did anything on its own: the role granted the
    /// <i>scope</i> (AssignedClasses) and every welfare <i>permission</i> came from somewhere else,
    /// so a holder with no ClassTeacherAssignment saw nothing and a holder with one was refused by
    /// every welfare endpoint anyway. The POST grants the permissions now
    /// (<c>PostPermissionService</c>), which is what the role was pretending to do.</para>
    ///
    /// <para><i>"What is the point in having them if they cannot be assigned? Redundant features
    /// create more confusion"</i> — user decision, plan §5 decision 3.</para>
    /// </summary>
    public const string RetiredClassTeacher = "class-teacher";

    /// <summary>
    /// Viewer - Read-only access and customer self-service.
    /// Can view dashboards, queue status, and submit feedback.
    /// </summary>
    public const string Viewer = "viewer";

    // ---- Staff Performance Monitor hierarchy (2026-09-16). All five sit BELOW Manager in All, so
    // none of them satisfies IsManagerOrAbove: that tier also unlocks the visiting-day repeat
    // check-in bypass and similar manager-tier overrides in the visitor module, and a head of
    // department has no business with those. "Administrator" in the requested hierarchy is the
    // existing Tenant Admin. The staff-axis narrowing lives on Role.StaffScope, enforced by
    // StaffScopeService — never by comparing against these codes.

    /// <summary>Director of Studies — academic head. Staff scope Organization; conducts and approves appraisals.</summary>
    public const string DirectorOfStudies = "director-of-studies";

    /// <summary>Academic Assistant — timetabling and duties. Staff scope Organization; no appraisal sign-off, no confidential rung.</summary>
    public const string AcademicAssistant = "academic-assistant";

    /// <summary>
    /// <b>RETIRED 2026-09-22</b>, for the same reason as <see cref="RetiredClassTeacher"/>: heading a
    /// department is a POST, recorded on <c>Department.HeadUserId</c>, and that post now grants the
    /// permissions and the staff scope together. The role granted the scope and no permission.
    /// </summary>
    public const string RetiredHeadOfDepartment = "head-of-department";

    /// <summary>Teacher — the portal and recognition; records only through named-recorder delegation on a duty.</summary>
    public const string Teacher = "teacher";

    /// <summary>Support Staff — the portal and recognition; appraised by their line manager.</summary>
    public const string SupportStaff = "support-staff";

    /// <summary>
    /// All role codes for validation purposes.
    ///
    /// ORDER IS LOAD-BEARING: <see cref="Rank"/> indexes into this array, so it is declared
    /// most-privileged first and <see cref="IsManagerOrAbove"/> is computed from array position
    /// rather than from any stored level. The Staff Performance roles sit below Manager deliberately:
    /// placing one above would silently hand its holders the manager-tier override checks, including
    /// the visiting-day repeat check-in bypass in VisitorsController.CheckIn (user decision
    /// 2026-09-16, plan §13 decision 1).
    ///
    /// <para><b>Removing class-teacher and head-of-department on 2026-09-22 shifted nothing</b>, and
    /// that was checked rather than assumed: Rank is RELATIVE — IsManagerOrAbove is
    /// <c>Rank(x) &lt;= Rank(Manager)</c> and RoleAssignmentGuard.IsAtOrBelow compares two ranks — and
    /// both removed entries sat BELOW Manager, so no surviving role's answer changed. A future
    /// removal needs the same check; a removal from above Manager would not be free.</para>
    /// </summary>
    public static readonly string[] All =
    {
        SuperAdmin, Admin, Manager,
        DirectorOfStudies, AcademicAssistant,
        Staff, Teacher, SupportStaff, Viewer
    };

    /// <summary>The roles whose holders are support (non-teaching) staff, for PerformanceParameter.AppliesTo.</summary>
    public static bool IsSupportStaff(string? roleCode)
        => string.Equals(roleCode, SupportStaff, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the given role code is a valid system role
    /// </summary>
    public static bool IsValidRole(string? roleCode)
    {
        if (string.IsNullOrWhiteSpace(roleCode))
            return false;
        return All.Contains(roleCode, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the role code represents a platform administrator
    /// </summary>
    public static bool IsSuperAdmin(string? roleCode)
    {
        return string.Equals(roleCode, SuperAdmin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the role code represents an organization administrator
    /// </summary>
    public static bool IsAdmin(string? roleCode)
    {
        return string.Equals(roleCode, Admin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the role has administrative privileges (SuperAdmin or Admin)
    /// </summary>
    public static bool IsAdministrator(string? roleCode)
    {
        return IsSuperAdmin(roleCode) || IsAdmin(roleCode);
    }

    /// <summary>
    /// Ordinal rank for tier comparisons (lower = more privileged) — index into <see cref="All"/>,
    /// which is already declared most-to-least privileged. An unrecognized role ranks below
    /// Viewer so it never accidentally satisfies an "at least X" check.
    /// </summary>
    private static int Rank(string? roleCode)
    {
        var index = Array.FindIndex(All, r => string.Equals(r, roleCode, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? All.Length : index;
    }

    /// <summary>
    /// Checks if the role is Manager, Admin, or SuperAdmin — the "can approve an exception a
    /// front-desk Staff/Viewer user can't" tier, e.g. overriding the visiting-day repeat
    /// check-in gate in VisitorsController.CheckIn without first flagging the card.
    /// </summary>
    public static bool IsManagerOrAbove(string? roleCode)
    {
        return Rank(roleCode) <= Rank(Manager);
    }

    /// <summary>
    /// True when <paramref name="targetRoleCode"/> is at or below <paramref name="actorRoleCode"/> in
    /// <see cref="All"/> — "an approver can assign only roles at or below their own rank" (duty rota plan
    /// §12.4). A custom role ranks below Viewer, so it always passes this test; RoleAssignmentGuard then
    /// requires its permissions to be a subset of the actor's, which is the check that matters for it.
    /// </summary>
    public static bool IsAtOrBelow(string? targetRoleCode, string? actorRoleCode)
    {
        return Rank(targetRoleCode) >= Rank(actorRoleCode);
    }

    // ---- Who may SEE a seeded role (2026-09-18) ------------------------------------------------
    // Assignment was already guarded: IsAtOrBelow plus RoleAssignmentGuard refuse a tenant admin
    // handing out super-admin. What had no guard at all was VISIBILITY — RolesController admitted
    // every row with OrganizationId == null, and every seeded role has that, so a school's Users &
    // Roles page listed Platform Admin, and a bank's listed Class Teacher and the five school
    // staff roles. This is the one home for that rule; do not re-derive it per endpoint.

    /// <summary>
    /// Roles that exist only at platform level. A tenant must never see one in its own role list:
    /// Platform Admin is not a role a school can hold, assign, edit or be shown, and listing it
    /// invites exactly the question "why can I see this?".
    /// </summary>
    public static readonly string[] PlatformOnly = { SuperAdmin };

    /// <summary>True when the role belongs to the platform rather than to any tenant.</summary>
    public static bool IsPlatformOnly(string? roleCode)
        => PlatformOnly.Contains(roleCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The module a seeded role belongs to, or null when it is part of the core product and
    /// therefore always available.
    ///
    /// Class Teacher and the five Staff Performance roles all map to <see cref="ModuleCodes.StudentWelfare"/>
    /// because Staff Performance is PART of that module — display name "Welfare &amp; Performance"
    /// (user decision 2026-09-17), not a module of its own. A custom role's code is arbitrary and
    /// returns null here, so a tenant's own roles are never hidden by this.
    /// </summary>
    public static string? ModuleFor(string? roleCode) => roleCode?.Trim().ToLowerInvariant() switch
    {
        DirectorOfStudies or AcademicAssistant or Teacher or SupportStaff => ModuleCodes.StudentWelfare,
        _ => null,
    };

    /// <summary>
    /// Whether a tenant may see this role, given the modules that tenant has active. Platform roles
    /// never; a module's roles only while that module is on; everything else always.
    ///
    /// <para>Deliberately a VISIBILITY rule only. A tenant that cancels Welfare &amp; Performance
    /// keeps any user already holding Class Teacher — the role row and the assignment both survive,
    /// because revoking a module must not silently strip someone's access on the way past. It stops
    /// being offered, it is not retrospectively unassigned.</para>
    /// </summary>
    public static bool IsVisibleToTenant(string? roleCode, IReadOnlyCollection<string>? activeModuleCodes)
    {
        if (IsPlatformOnly(roleCode)) return false;

        var module = ModuleFor(roleCode);
        if (module is null) return true;

        return activeModuleCodes is not null
            && activeModuleCodes.Contains(module, StringComparer.OrdinalIgnoreCase);
    }
}
