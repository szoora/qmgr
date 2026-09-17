using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// "Nobody chooses their own privilege" (duty rota plan §13.17): the one rule for whether a caller may
/// put somebody else into a role — approving a join request, re-issuing access, importing staff.
/// <list type="bullet">
/// <item>Never the platform SuperAdmin.</item>
/// <item>Tenant Admin only for a caller who holds the role-assignment permission (<c>roles.edit</c>).</item>
/// <item>A system role at or below the caller's own rank (<see cref="RoleCodes.IsAtOrBelow"/>).</item>
/// <item>A role whose permissions are all held by the caller — which is what closes a custom role
///   carrying permissions the caller lacks. Tenant Admin and SuperAdmin hold everything a tenant role can.</item>
/// </list>
/// </summary>
public static class RoleAssignmentGuard
{
    /// <summary>Null when allowed; otherwise a sentence saying why, safe to show the caller.</summary>
    public static async Task<string?> RefusalAsync(QMgrDbContext db, Guid actorUserId, Role target, CancellationToken ct = default)
    {
        if (RoleCodes.IsSuperAdmin(target.Code))
            return "The platform administrator role cannot be assigned here.";

        var actor = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == actorUserId)
            .Select(u => new { RoleCode = u.Role.Code, Permissions = u.Role.RolePermissions.Select(rp => rp.Permission.Code).ToList() })
            .FirstOrDefaultAsync(ct);
        if (actor == null)
            return "Your account could not be found.";

        if (RoleCodes.IsSuperAdmin(actor.RoleCode))
            return null;

        if (RoleCodes.IsAdmin(target.Code) && !actor.Permissions.Contains(Permissions.RolesEdit))
            return "Assigning the Tenant Admin role needs the role-assignment permission, which your role does not hold.";

        if (!RoleCodes.IsAtOrBelow(target.Code, actor.RoleCode))
            return $"You can assign only roles at or below your own. {target.Name} ranks above your role.";

        if (RoleCodes.IsAdmin(actor.RoleCode))
            return null;

        var targetPermissions = await db.RolePermissions.IgnoreQueryFilters().AsNoTracking()
            .Where(rp => rp.RoleId == target.Id)
            .Select(rp => rp.Permission.Code)
            .ToListAsync(ct);
        var missing = targetPermissions.Except(actor.Permissions).ToList();
        if (missing.Count > 0)
            return $"{target.Name} carries {missing.Count} permission(s) your own role does not hold, so you cannot assign it.";

        return null;
    }
}
