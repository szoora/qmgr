using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// A leaver's sign-in ends on their last day (the RBAC review's R3, 2026-09-24).
///
/// <para>Until this, <c>User.EmploymentEndDate</c> was honoured by the notification audience and by nothing else,
/// so a teacher who left last term could still sign in and read their classes. NIST SP 800-53 AC-2 asks for an
/// account to be disabled when its holder is no longer associated with the organisation; this is that, daily.</para>
///
/// <para>What it does, per person whose end date has PASSED (the last day itself is still a working day): switches
/// the account off (reversible — nothing is deleted), ends their live class and subject assignments, takes them off
/// a department's head or deputy seat, removes them from every leadership post, drops their cached permissions, and
/// tells the people who manage accounts. <b>An organisation's last active Administrator is never switched off</b> —
/// they are named in the notice instead, because a school locked out of its own system is worse than a late leaver.</para>
/// </summary>
public class AccountLifecycleJobs
{
    private readonly QMgrDbContext _db;
    private readonly IStaffProfileChangeNotifier _accessChanged;
    private readonly INotificationService _notifications;
    private readonly IActivityLogger _activity;
    private readonly ILogger<AccountLifecycleJobs> _logger;

    public AccountLifecycleJobs(QMgrDbContext db, IStaffProfileChangeNotifier accessChanged, INotificationService notifications,
        IActivityLogger activity, ILogger<AccountLifecycleJobs> logger)
    {
        _db = db;
        _accessChanged = accessChanged;
        _notifications = notifications;
        _activity = activity;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 1)]
    public async Task DeactivateLeaversAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var leavers = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Role)
            .Where(u => u.IsActive && u.PendingApprovalAt == null
                && u.EmploymentEndDate != null && u.EmploymentEndDate < today
                && u.Role.Code != RoleCodes.SuperAdmin)
            .ToListAsync();
        if (leavers.Count == 0) return;

        foreach (var org in leavers.GroupBy(u => u.OrganizationId))
        {
            try
            {
                await DeactivateInOrganizationAsync(org.Key, org.ToList(), today);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Leaver sweep failed for organization {OrganizationId}", org.Key);
            }
        }
    }

    private async Task DeactivateInOrganizationAsync(Guid organizationId, List<QMgr.Domain.Entities.Identity.User> leavers, DateOnly today)
    {
        var activeAdmins = await _db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.OrganizationId == organizationId && u.IsActive && u.Role.Code == RoleCodes.Admin);

        var switchedOff = new List<QMgr.Domain.Entities.Identity.User>();
        var keptAdmins = new List<QMgr.Domain.Entities.Identity.User>();
        foreach (var u in leavers)
        {
            if (RoleCodes.IsAdmin(u.Role.Code) && activeAdmins <= 1) { keptAdmins.Add(u); continue; }
            if (RoleCodes.IsAdmin(u.Role.Code)) activeAdmins--;
            u.IsActive = false;
            u.UpdatedAt = DateTime.UtcNow;
            switchedOff.Add(u);
        }

        var ids = switchedOff.Select(u => u.Id).ToList();
        if (ids.Count > 0)
        {
            var now = DateTime.UtcNow;
            var assignments = await _db.ClassTeacherAssignments.IgnoreQueryFilters()
                .Where(a => ids.Contains(a.UserId) && a.EndedAt == null).ToListAsync();
            foreach (var a in assignments) { a.EndedAt = now; a.EndReason = "Employment ended"; }

            var departments = await _db.Departments.IgnoreQueryFilters()
                .Where(d => d.OrganizationId == organizationId
                    && ((d.HeadUserId != null && ids.Contains(d.HeadUserId.Value)) || (d.DeputyHeadUserId != null && ids.Contains(d.DeputyHeadUserId.Value))))
                .ToListAsync();
            foreach (var d in departments)
            {
                if (d.HeadUserId is { } h && ids.Contains(h)) d.HeadUserId = null;
                if (d.DeputyHeadUserId is { } dh && ids.Contains(dh)) d.DeputyHeadUserId = null;
            }

            await _db.SaveChangesAsync();

            await LeadershipPosts.WriteAsync(_db, organizationId, posts => ids.Aggregate(posts, LeadershipPosts.Without));

            foreach (var u in switchedOff)
            {
                await _accessChanged.PostChangedAsync(u.Id, "employment ended");
                await _activity.RecordAsync(ActivityActions.LeaverDeactivated, "User", u.Id, u.Id,
                    $"Account switched off: employment ended {u.EmploymentEndDate:dd MMM yyyy}",
                    organizationId: organizationId);
            }
        }

        var managers = await NotificationAudience.HoldersAsync(_db, organizationId, Permissions.UsersEdit);
        if (managers.Count == 0) return;

        var lines = new List<string>();
        if (switchedOff.Count > 0)
            lines.Add($"Switched off because their employment ended: {string.Join(", ", switchedOff.Select(PersonNames.Display))}. " +
                      "Their class and department posts ended with them. To bring somebody back, change their end date and switch the account on.");
        if (keptAdmins.Count > 0)
            lines.Add($"NOT switched off, because they are the only Administrator: {string.Join(", ", keptAdmins.Select(PersonNames.Display))}. " +
                      "Appoint another Administrator, or correct the end date.");

        await _notifications.NotifyManyAsync(managers, new CreateNotificationRequest
        {
            OrganizationId = organizationId,
            Title = switchedOff.Count > 0 ? $"{switchedOff.Count} leaver account(s) switched off" : "A leaver's account was kept on",
            Message = string.Join(" ", lines),
            Type = NotificationType.SystemAlert,
            Priority = keptAdmins.Count > 0 ? NotificationPriority.High : NotificationPriority.Normal,
            Channels = NotificationChannel.InApp | NotificationChannel.Email,
            EventKey = NotificationEventKeys.AccountsDeactivated,
            ActionUrl = "/admin/users",
            IconClass = "person-x"
        });
    }
}

public static class AccountLifecycleJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // 02:15 UTC — 05:15 in Kampala, before anybody signs in on the day after their last.
        RecurringJob.AddOrUpdate<AccountLifecycleJobs>("deactivate-leavers", job => job.DeactivateLeaversAsync(), "15 2 * * *");
    }
}
