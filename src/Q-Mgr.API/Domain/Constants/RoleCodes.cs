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
    public const string ClassTeacher = "class-teacher";

    /// <summary>
    /// Viewer - Read-only access and customer self-service.
    /// Can view dashboards, queue status, and submit feedback.
    /// </summary>
    public const string Viewer = "viewer";

    /// <summary>
    /// All role codes for validation purposes.
    ///
    /// ORDER IS LOAD-BEARING: <see cref="Rank"/> indexes into this array, so it is declared
    /// most-privileged first and <see cref="IsManagerOrAbove"/> is computed from array position
    /// rather than from any stored level. ClassTeacher sits between Staff and Viewer deliberately —
    /// placing it above Manager would silently hand every class teacher the manager-tier override
    /// checks, including the visiting-day repeat check-in bypass in VisitorsController.CheckIn.
    /// </summary>
    public static readonly string[] All = { SuperAdmin, Admin, Manager, Staff, ClassTeacher, Viewer };

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
}
