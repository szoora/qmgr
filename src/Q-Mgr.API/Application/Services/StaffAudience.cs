using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Calendar;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// WHO IS IN A STAFF AUDIENCE — the server's one home for it (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING §5.1, 2026-09-26).
/// The membership TEST is <see cref="StaffAudienceRule"/> in Shared, which the Web runs too; this class supplies the
/// facts that test needs, the same way every time:
/// <list type="bullet">
/// <item>the person's staff group through <c>GroupFor</c> WITH its fallback — Staff Notices read the raw
/// <c>Role.StaffGroup</c>, so a person whose role had no group could see a group notice and was never told of it;</item>
/// <item>active accounts only, never the platform account, never a leaver (<see cref="StaffEmployment.CurrentOn"/>);</item>
/// <item>the branch's people — assigned to it, or to no branch, which is how an organization-wide account is stored.</item>
/// </list>
/// Used by the calendar (who sees an event, who is told), the event reminders, the feed, the programme import and Staff
/// Notices. A second "is this person in this audience" anywhere else is this codebase's most repeated bug.
/// </summary>
public static class StaffAudience
{
    /// <summary>The audience an event carries, as the shared shape.</summary>
    public static StaffAudienceDto Of(SchoolEvent e) => new()
    {
        AllStaff = e.AllStaff,
        StaffGroups = e.AudienceStaffGroups.ToList(),
        RoleCodes = e.AudienceRoleCodes.ToList(),
        DepartmentIds = e.AudienceDepartmentIds.ToList(),
        UserIds = e.AudienceUserIds.ToList()
    };

    /// <summary>Writes an audience onto an event, tidied: no blanks, no duplicates.</summary>
    public static void Apply(SchoolEvent e, StaffAudienceDto a)
    {
        e.AllStaff = a.AllStaff;
        e.AudienceStaffGroups = a.AllStaff ? Array.Empty<string>() : Clean(a.StaffGroups);
        e.AudienceRoleCodes = a.AllStaff ? Array.Empty<string>() : Clean(a.RoleCodes);
        e.AudienceDepartmentIds = a.AllStaff ? Array.Empty<Guid>() : a.DepartmentIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        e.AudienceUserIds = a.AllStaff ? Array.Empty<Guid>() : a.UserIds.Where(x => x != Guid.Empty).Distinct().ToArray();
    }

    private static string[] Clean(IEnumerable<string> values)
        => values.Select(v => (v ?? string.Empty).Trim()).Where(v => v.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>One person's facts. Null for somebody inactive, departed, in another organization, or the platform account.</summary>
    public static async Task<AudienceMemberDto?> MemberAsync(QMgrDbContext db, IStaffPerformancePolicyService policyService,
        Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        var list = await LoadAsync(db, policyService, organizationId, null, userId, ct);
        return list.FirstOrDefault();
    }

    /// <summary>Everybody who could be in an audience at <paramref name="branchId"/> (null = the whole organization).</summary>
    public static Task<List<AudienceMemberDto>> MembersAsync(QMgrDbContext db, IStaffPerformancePolicyService policyService,
        Guid organizationId, Guid? branchId, CancellationToken ct = default)
        => LoadAsync(db, policyService, organizationId, branchId, null, ct);

    private static async Task<List<AudienceMemberDto>> LoadAsync(QMgrDbContext db, IStaffPerformancePolicyService policyService,
        Guid organizationId, Guid? branchId, Guid? onlyUserId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var people = db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive && u.PendingApprovalAt == null && u.Role.Code != RoleCodes.SuperAdmin)
            .Where(StaffEmployment.CurrentOn(today));
        if (branchId is { } b) people = people.Where(u => u.AssignedBranchId == b || u.AssignedBranchId == null);
        if (onlyUserId is { } one) people = people.Where(u => u.Id == one);

        var rows = await people
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username, u.AssignedBranchId, RoleCode = u.Role.Code, RoleName = u.Role.Name, RoleGroup = u.Role.StaffGroup, u.DepartmentIds })
            .ToListAsync(ct);
        if (rows.Count == 0) return new();

        var policy = await policyService.GetAsync(organizationId, ct);
        return rows.Select(u => new AudienceMemberDto
        {
            UserId = u.Id,
            FullName = PersonNames.Display(organizationId, u.FirstName, u.LastName, u.Username),
            SortName = PersonNames.SortKey(organizationId, u.FirstName, u.LastName),
            RoleCode = u.RoleCode,
            RoleName = u.RoleName,
            StaffGroup = policyService.GroupFor(u.RoleGroup, policy),
            DepartmentIds = (u.DepartmentIds ?? Array.Empty<Guid>()).ToList(),
            BranchId = u.AssignedBranchId
        }).ToList();
    }

    /// <summary>
    /// Is the event <paramref name="viewer"/>'s own — for them, or theirs to run? What "My events", My School Day, the
    /// portal, the feed and the reminders read. The staff audience counts only while the event is FOR staff.
    /// </summary>
    public static bool IsMine(SchoolEvent e, AudienceMemberDto? viewer, Guid viewerId)
    {
        if (viewerId != Guid.Empty && e.ResponsibleUserIds.Contains(viewerId)) return true;
        if (viewer == null) return false;
        if (viewer.DepartmentIds.Count > 0 && e.ResponsibleDepartmentIds.Any(viewer.DepartmentIds.Contains)) return true;
        return (e.Audience & EventAudience.Staff) == EventAudience.Staff && StaffAudienceRule.Includes(Of(e), viewer);
    }

    /// <summary>Everybody an event is for: its staff audience plus the people and departments responsible for it.</summary>
    public static List<Guid> RecipientsOf(SchoolEvent e, IEnumerable<AudienceMemberDto> members)
        => members.Where(m => IsMine(e, m, m.UserId)).Select(m => m.UserId).Distinct().ToList();

    /// <summary>Who an audience reaches at a branch — the import resolves a meeting's attendance this way.</summary>
    public static List<Guid> Resolve(StaffAudienceDto audience, IEnumerable<AudienceMemberDto> members)
        => StaffAudienceRule.Members(audience, members).Select(m => m.UserId).Distinct().ToList();
}
