using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// THE ONE HOME FOR "WHO SHOULD HEAR ABOUT THIS" (2026-09-23).
///
/// <para>A newly onboarded teacher at Maryhill opened the bell and read the school's failed payments.
/// Payment and trial notices were written with no recipient, and a recipient-less row was read by
/// everybody in the tenant — and pushed live, through <c>Clients.All</c>, to every tenant on the
/// platform. The rule since: <b>every notification names one person</b>. A message several people need
/// is one row each, and the list of who they are is decided here, on the server, from what each
/// person may already open.</para>
///
/// <para><b>Role AND post.</b> The helper this replaces read <c>Role.RolePermissions</c> only, so a
/// head of department — who holds <c>staff.reports.view</c> through the post, not the role — never
/// received a report notice meant for them. That is the same "six readers of what may this person
/// do" bug <see cref="PostPermissionService"/> records; this is the audience-side copy of the fix.</para>
///
/// <para><b>Excluded:</b> inactive accounts, the platform account (it belongs to no school's
/// conversation), and anybody whose employment has ended. A leaver keeps a login until somebody
/// disables it; they must not keep hearing about the school's money meanwhile.</para>
/// </summary>
public static class NotificationAudience
{
    /// <param name="branchId">
    /// Narrows to people who work at that branch — assigned to it, or to no branch, which is how an
    /// organization-wide account is stored (the <c>StaffLookups.BranchStaff</c> rule). Null is the whole
    /// organization, which is right for billing and wrong for anything about one branch's timetable.
    /// </param>
    public static async Task<List<Guid>> HoldersAsync(QMgrDbContext db, Guid organizationId, string permissionCode, CancellationToken ct = default, Guid? branchId = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var people = db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive && u.Role.Code != RoleCodes.SuperAdmin)
            .Where(u => u.EmploymentEndDate == null || u.EmploymentEndDate >= today);
        if (branchId is { } b)
            people = people.Where(u => u.AssignedBranchId == b || u.AssignedBranchId == null);

        var ids = await people
            .Where(u => u.Role.RolePermissions.Any(rp => rp.Permission.Code == permissionCode))
            .Select(u => u.Id)
            .ToListAsync(ct);

        // What a POST grants. Read from PostPermissionService's own lists, never re-typed here, so a
        // code added to a post reaches its holders' notices with no second edit.
        if (PostPermissionService.PastoralClassPost.Contains(permissionCode, StringComparer.OrdinalIgnoreCase))
        {
            ids.AddRange(await people
                .Where(u => db.ClassTeacherAssignments.IgnoreQueryFilters().Any(a => a.UserId == u.Id && a.EndedAt == null
                            && (a.Role == ClassTeacherRole.ClassTeacher || a.Role == ClassTeacherRole.Assistant)))
                .Select(u => u.Id)
                .ToListAsync(ct));
        }

        if (PostPermissionService.DepartmentHeadPost.Contains(permissionCode, StringComparer.OrdinalIgnoreCase))
        {
            ids.AddRange(await people
                .Where(u => db.Departments.IgnoreQueryFilters().Any(d => d.IsActive && (d.HeadUserId == u.Id || d.DeputyHeadUserId == u.Id)))
                .Select(u => u.Id)
                .ToListAsync(ct));
        }

        // The leadership posts (2026-09-24): safeguarding lead and deputies, an acting head in period, house and
        // dormitory posts. Filtered through the same people query, so an inactive or departed holder hears nothing.
        var leadership = await LeadershipPosts.ReadAsync(db, organizationId, ct);
        var candidates = new List<Guid>();
        if (LeadershipPosts.SafeguardingLeadPost.Contains(permissionCode, StringComparer.OrdinalIgnoreCase))
        {
            if (leadership.SafeguardingLeadUserId is { } lead) candidates.Add(lead);
            candidates.AddRange(leadership.DeputySafeguardingLeadUserIds);
        }
        if (leadership.ActingHead is { } acting && acting.IsActiveOn(LeadershipPosts.Today())
            && LeadershipPosts.ActingHeadPost().Contains(permissionCode, StringComparer.OrdinalIgnoreCase))
            candidates.Add(acting.UserId);
        if (PostPermissionService.PastoralClassPost.Contains(permissionCode, StringComparer.OrdinalIgnoreCase))
            candidates.AddRange(leadership.PastoralUnitPosts.Where(p => branchId == null || p.BranchId == branchId).Select(p => p.UserId));
        if (candidates.Count > 0)
            ids.AddRange(await people.Where(u => candidates.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct));

        return ids.Distinct().ToList();
    }
}
