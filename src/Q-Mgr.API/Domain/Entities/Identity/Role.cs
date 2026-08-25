using QMgr.Domain.Common;

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
