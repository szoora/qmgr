using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// The handful of queries every Staff Performance controller and job needs and none should own:
/// display names for a set of user ids, department names, "who is staff in this branch", and
/// "who holds this permission in this organization". Every query here uses IgnoreQueryFilters
/// plus an explicit OrganizationId, so it answers the same in a request (tenant set) and in a
/// Hangfire worker (no tenant). Not a mapper — StaffPerformanceMapping is the only mapper.
/// </summary>
public static class StaffLookups
{
    /// <summary>userId → the person's name in their organisation's order (falls back to the username) for whichever ids are given. Nulls and Guid.Empty are ignored.</summary>
    public static async Task<StaffPerformanceMapping.NameLookup> LoadNamesAsync(QMgrDbContext db, IEnumerable<Guid?> ids, CancellationToken ct = default)
    {
        var wanted = ids.Where(id => id.HasValue && id.Value != Guid.Empty).Select(id => id!.Value).Distinct().ToList();
        if (wanted.Count == 0) return StaffPerformanceMapping.NameLookup.Empty;

        var rows = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => wanted.Contains(u.Id))
            .Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName, u.Username })
            .ToListAsync(ct);

        return new StaffPerformanceMapping.NameLookup(rows.ToDictionary(
            r => r.Id,
            r => PersonNames.Display(r.OrganizationId, r.FirstName, r.LastName, r.Username)));
    }

    /// <summary>departmentId → name for every department of the organization (active or not — a retired department still names a person's history).</summary>
    public static async Task<Dictionary<Guid, string>> LoadDepartmentNamesAsync(QMgrDbContext db, Guid organizationId, CancellationToken ct = default)
        => await db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId)
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

    /// <summary>
    /// The members of staff of a branch: active users of the organization assigned to the branch or
    /// to no branch, never the platform SuperAdmin. Role included, because the staff group and the
    /// notice audience both read it. The same definition StaffScoringService.ComputeBranchAsync
    /// uses, so a report's denominator and a register's "everyone" agree.
    ///
    /// <para><b>Excludes people whose employment has ended or not yet begun (2026-09-18).</b> This is
    /// the forward-looking set — registers, scoring denominators, notice audiences, rota generation
    /// — and somebody who left in June has no business in September's. Their RECORDS are untouched:
    /// see StaffEmployment for why a leaver is dated rather than deactivated. Pass
    /// <paramref name="includeFormer"/> to get everyone, which the directory does so it can show a
    /// Left badge rather than silently losing the person.</para>
    /// </summary>
    public static IQueryable<User> BranchStaff(QMgrDbContext db, Guid organizationId, Guid branchId, bool includeFormer = false)
    {
        var q = db.Users.IgnoreQueryFilters().AsNoTracking()
            .Include(u => u.Role)
            .Where(u => u.OrganizationId == organizationId && u.IsActive
                        && (u.AssignedBranchId == branchId || u.AssignedBranchId == null)
                        && u.Role.Code != RoleCodes.SuperAdmin);

        return includeFormer ? q : q.Where(StaffEmployment.CurrentOn(DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    /// <summary>
    /// Active users of the organization who hold <paramref name="permissionCode"/> — through their
    /// role OR their post. Delegates to <see cref="NotificationAudience.HoldersAsync"/>, the one home;
    /// until 2026-09-23 this read the role alone, so a head of department never heard about the
    /// reports their post lets them read.
    /// </summary>
    public static Task<List<Guid>> UsersWithPermissionAsync(QMgrDbContext db, Guid organizationId, string permissionCode, CancellationToken ct = default, Guid? branchId = null)
        => NotificationAudience.HoldersAsync(db, organizationId, permissionCode, ct, branchId);

    /// <summary>"Maths, Physics" for a user's DepartmentIds, or null when they are in none.</summary>
    public static string? DepartmentNames(Guid[]? departmentIds, IReadOnlyDictionary<Guid, string> names)
    {
        if (departmentIds == null || departmentIds.Length == 0) return null;
        var list = departmentIds.Select(id => names.TryGetValue(id, out var n) ? n : null).Where(n => n != null).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }
}
