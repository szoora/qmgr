namespace QMgr.Domain.Entities.Identity;

/// <summary>
/// Junction table linking roles to permissions.
/// </summary>
public class RolePermission
{
    /// <summary>
    /// Role ID
    /// </summary>
    public Guid RoleId { get; set; }

    /// <summary>
    /// Permission ID
    /// </summary>
    public Guid PermissionId { get; set; }

    /// <summary>
    /// When this permission was granted to the role
    /// </summary>
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who granted this permission (null for seed data)
    /// </summary>
    public Guid? GrantedBy { get; set; }

    #region Navigation Properties

    /// <summary>
    /// The role
    /// </summary>
    public virtual Role Role { get; set; } = null!;

    /// <summary>
    /// The permission
    /// </summary>
    public virtual Permission Permission { get; set; } = null!;

    #endregion
}
