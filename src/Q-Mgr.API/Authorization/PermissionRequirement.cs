using Microsoft.AspNetCore.Authorization;

namespace QMgr.API.Authorization;

/// <summary>
/// Authorization requirement that checks if a user has a specific permission.
/// </summary>
public class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// The permission code required (e.g., "users.create")
    /// </summary>
    public string Permission { get; }

    public PermissionRequirement(string permission)
    {
        Permission = permission ?? throw new ArgumentNullException(nameof(permission));
    }
}
