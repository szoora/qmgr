using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// What follows a change to WHO somebody is in the organization — today, their role. Three things, in
/// one place, so the single-user editor, the bulk role change and its undo cannot drift apart again:
///
///  1. the cached permission set is dropped and the live PermissionsChanged push is sent. The bulk path
///     did neither before 2026-09-17, so a person demoted in a batch kept their old permissions for up
///     to the cache's five minutes, the exact window UsersController already closed for one user;
///  2. an ActivityEvent names who changed whose role, from what to what — structure changes are in the
///     activity log (plan §11) and a role change was the one that was not;
///  3. <c>staff.profile-changed</c> tells the person, when the organization has Staff Performance
///     (plan §8: "department, line manager, role"; only the first two were ever sent).
///
/// NEVER THROWS. Every caller has already committed the change; a notification that did not land is a
/// degraded success, not a failure.
/// </summary>
public interface IStaffProfileChangeNotifier
{
    Task RoleChangedAsync(Guid organizationId, Guid userId, Guid? oldRoleId, Guid newRoleId, Guid? actorUserId, string via, CancellationToken cancellationToken = default);
}

public class StaffProfileChangeNotifier : IStaffProfileChangeNotifier
{
    private readonly QMgrDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly INotificationHubService _hub;
    private readonly INotificationService _notifications;
    private readonly IActivityLogger _activity;
    private readonly IModuleAccessService _modules;
    private readonly ILogger<StaffProfileChangeNotifier> _logger;

    public StaffProfileChangeNotifier(
        QMgrDbContext db,
        IMemoryCache cache,
        INotificationHubService hub,
        INotificationService notifications,
        IActivityLogger activity,
        IModuleAccessService modules,
        ILogger<StaffProfileChangeNotifier> logger)
    {
        _db = db;
        _cache = cache;
        _hub = hub;
        _notifications = notifications;
        _activity = activity;
        _modules = modules;
        _logger = logger;
    }

    public async Task RoleChangedAsync(Guid organizationId, Guid userId, Guid? oldRoleId, Guid newRoleId, Guid? actorUserId, string via, CancellationToken cancellationToken = default)
    {
        if (oldRoleId == newRoleId) return;

        try { _cache.InvalidateUserPermissions(userId); }
        catch (Exception ex) { _logger.LogError(ex, "Permission cache for {UserId} could not be cleared after a role change", userId); }

        try { await _hub.NotifyPermissionsChangedAsync(userId); }
        catch (Exception ex) { _logger.LogDebug(ex, "PermissionsChanged push failed for {UserId}", userId); }

        try
        {
            var roleIds = new[] { newRoleId }.Concat(oldRoleId.HasValue ? new[] { oldRoleId.Value } : Array.Empty<Guid>()).ToList();
            var roles = await _db.Roles.IgnoreQueryFilters().AsNoTracking()
                .Where(r => roleIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
            var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.FirstName, u.LastName, u.Username, u.AssignedBranchId })
                .FirstOrDefaultAsync(cancellationToken);
            if (user == null) return;

            var name = $"{user.FirstName} {user.LastName}".Trim() is { Length: > 0 } n ? n : user.Username;
            var oldName = oldRoleId.HasValue ? roles.GetValueOrDefault(oldRoleId.Value, "another role") : "no role";
            var newName = roles.GetValueOrDefault(newRoleId, "a new role");

            await _activity.RecordAsync(ActivityActions.RoleChanged, "User", userId, userId,
                $"{name}'s role changed from {oldName} to {newName}{(string.IsNullOrWhiteSpace(via) ? "" : $" ({via})")}",
                new { OldRoleId = oldRoleId, NewRoleId = newRoleId, Via = via },
                user.AssignedBranchId, organizationId, actorUserId, cancellationToken);

            if (!await _modules.IsModuleActiveAsync(organizationId, ModuleCodes.StudentWelfare)) return;

            await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = userId,
                OrganizationId = organizationId,
                BranchId = user.AssignedBranchId,
                Title = "Your role changed",
                Message = $"Your role is now {newName} (it was {oldName}). What you can see and do has changed with it.",
                Type = NotificationType.StaffPerformance,
                Priority = NotificationPriority.Normal,
                Channels = NotificationChannel.InApp | NotificationChannel.Email,
                EventKey = NotificationEventKeys.StaffProfileChanged,
                ActionUrl = "/portal",
                IconClass = "person-badge"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Role-change follow-up for {UserId} failed", userId);
        }
    }
}
