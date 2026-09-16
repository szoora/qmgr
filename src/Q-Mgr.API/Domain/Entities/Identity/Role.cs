using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Identity;

/// <summary>
/// Database-backed role for RBAC. Roles are organization-scoped,
/// except for system roles which are shared across all organizations.
/// </summary>
public class Role : BaseAuditableEntity
{
    /// <summary>
    /// Organization this role belongs to. Null for system-wide roles.
    /// </summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>
    /// Role name (e.g., "Admin", "Manager", "Staff")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL-safe identifier (e.g., "admin", "manager", "staff")
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Description of the role's purpose
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Display color (hex code, e.g., "#FF5733")
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// Icon name for UI display
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// System roles cannot be deleted or have their core permissions modified
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// How widely this role sees rows INSIDE a branch its permissions already reach — a second
    /// axis to the permission table, not a replacement for it. Defaults to
    /// <see cref="RoleDataScope.Organization"/>, which is what every role did before this column
    /// existed, so an unmigrated row keeps behaving exactly as it did.
    ///
    /// Set to <see cref="RoleDataScope.AssignedClasses"/> on the seeded class-teacher role, and
    /// settable on a tenant's own custom roles (a head of year, a house parent) through the role
    /// editor. Enforced by StudentScopeService, which is the only place that reads it.
    /// </summary>
    public RoleDataScope DataScope { get; set; } = RoleDataScope.Organization;

    /// <summary>
    /// The same idea for the second subject: how widely this role sees OTHER STAFF. A separate
    /// column because <see cref="DataScope"/> is about students and one enum cannot say "all
    /// students, my department's staff". Defaults to Organization so every pre-existing role keeps
    /// behaving as it did; the seeded teacher / support-staff roles are SelfOnly and head-of-
    /// department is AssignedDepartments. Enforced by StaffScopeService, the only reader.
    /// </summary>
    public StaffDataScope StaffScope { get; set; } = StaffDataScope.Organization;

    /// <summary>
    /// Display order in lists
    /// </summary>
    public int SortOrder { get; set; }

    #region Navigation Properties

    /// <summary>
    /// Organization this role belongs to
    /// </summary>
    public virtual Organization.Organization? Organization { get; set; }

    /// <summary>
    /// Permissions assigned to this role
    /// </summary>
    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();

    /// <summary>
    /// Users assigned to this role
    /// </summary>
    public virtual ICollection<User> Users { get; set; } = new List<User>();

    #endregion
}
