using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Identity;

/// <summary>
/// System-wide permission definition. Permissions are static and defined at the platform level.
/// They are not organization-scoped.
/// </summary>
public class Permission : BaseEntity
{
    /// <summary>
    /// Unique permission code (e.g., "users.create", "queue.manage")
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name (e.g., "Create Users", "Manage Queue")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of what this permission allows
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Category for grouping in UI (e.g., "User Management", "Queue Operations")
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Display order within category
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Whether this permission is visible in the UI
    /// (some permissions may be internal/system-only)
    /// </summary>
    public bool IsVisible { get; set; } = true;

    #region Navigation Properties

    /// <summary>
    /// Roles that have this permission
    /// </summary>
    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();

    #endregion
}
