using Microsoft.AspNetCore.Authorization;

namespace QMgr.API.Authorization;

/// <summary>
/// Authorization requirement that checks if a user has a specific permission.
/// </summary>
/// <remarks>
/// Usually one code. <see cref="RequirePermissionAnyAttribute"/> builds one carrying several, and
/// holding ANY of them satisfies it — see <see cref="Accepts"/>. The single-code constructor is
/// unchanged, so every existing <c>[RequirePermission(...)]</c> behaves exactly as before.
/// </remarks>
public class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// The permission code required (e.g., "users.create"). When several are accepted this is the
    /// first of them, and it is what the log lines name.
    /// </summary>
    public string Permission { get; }

    /// <summary>Every code that satisfies this requirement. One entry in the ordinary case.</summary>
    public IReadOnlyList<string> Accepted { get; }

    public PermissionRequirement(string permission)
        : this(new[] { permission ?? throw new ArgumentNullException(nameof(permission)) })
    {
    }

    public PermissionRequirement(IReadOnlyList<string> permissions)
    {
        if (permissions is null || permissions.Count == 0)
            throw new ArgumentException("At least one permission code is required.", nameof(permissions));

        Accepted = permissions;
        Permission = permissions[0];
    }

    /// <summary>True when the caller's granted codes include any one of the accepted codes.</summary>
    public bool Accepts(ICollection<string> granted)
    {
        foreach (var code in Accepted)
            if (granted.Contains(code)) return true;
        return false;
    }

    /// <summary>The accepted codes as they read in a log line.</summary>
    public string Describe() => string.Join(" or ", Accepted);
}
