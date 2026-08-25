using Microsoft.AspNetCore.Authorization;

namespace QMgr.API.Authorization;

/// <summary>
/// Attribute that requires a specific permission to access the endpoint.
/// Use permission codes from QMgr.Domain.Constants.Permissions.
/// </summary>
/// <example>
/// [RequirePermission(Permissions.UsersCreate)]
/// public async Task&lt;IActionResult&gt; CreateUser([FromBody] CreateUserRequest request)
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequirePermissionAttribute : AuthorizeAttribute
{
    /// <summary>
    /// Policy prefix used for permission-based authorization.
    /// </summary>
    public const string PolicyPrefix = "Permission:";

    /// <summary>
    /// Creates an authorization attribute that requires the specified permission.
    /// </summary>
    /// <param name="permission">The permission code (e.g., "users.create")</param>
    public RequirePermissionAttribute(string permission)
        : base($"{PolicyPrefix}{permission}")
    {
    }
}
