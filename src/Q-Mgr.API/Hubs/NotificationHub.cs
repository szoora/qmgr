using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Infrastructure.Data;

namespace QMgr.Hubs;

/// <summary>
/// SignalR Hub for real-time notifications. Requires authentication — unlike
/// QueueHub/DisplayHub, this delivers per-user data, not public queue-status
/// broadcasts, so there's no legitimate anonymous use case here.
/// </summary>
[Authorize]
public class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;
    private readonly QMgrDbContext _context;

    public NotificationHub(ILogger<NotificationHub> logger, QMgrDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    /// <summary>
    /// SECURITY: branch groups now carry visitor PII (name, phone, email, ID number, watchlist
    /// reason) via VisitorActivityBroadcaster, on top of ordinary notifications. Without this
    /// check any authenticated caller could join an arbitrary "branch-{id}" group — including one
    /// belonging to a different organization — and eavesdrop indefinitely. SuperAdmin is exempt,
    /// matching every REST controller's VerifyBranchOwnership pattern.
    /// </summary>
    private async Task<bool> IsAuthorizedForBranchAsync(string? branchId)
    {
        if (string.IsNullOrEmpty(branchId) || !Guid.TryParse(branchId, out var branchGuid))
            return false;

        var roleCode = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (RoleCodes.IsSuperAdmin(roleCode))
            return true;

        var orgIdClaim = Context.User?.FindFirst("org_id")?.Value;
        if (!Guid.TryParse(orgIdClaim, out var orgGuid))
            return false;

        return await _context.Branches.AnyAsync(b => b.Id == branchGuid && b.OrganizationId == orgGuid);
    }

    public override async Task OnConnectedAsync()
    {
        // The authenticated user's own ID, from the JWT — never trust the
        // client-supplied ?userId= query value for group membership.
        // Previously this hub had no [Authorize] and joined whatever
        // "user-{userId}" group the query string asked for, meaning any
        // caller could pass any other user's ID and receive their private
        // notifications.
        var authenticatedUserId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var branchId = Context.GetHttpContext()?.Request.Query["branchId"].ToString();

        if (!string.IsNullOrEmpty(authenticatedUserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{authenticatedUserId}");
            _logger.LogInformation("User {UserId} connected to notification hub", authenticatedUserId);
        }

        if (!string.IsNullOrEmpty(branchId))
        {
            if (await IsAuthorizedForBranchAsync(branchId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"branch-{branchId}");
                _logger.LogInformation("Connection added to branch group {BranchId}", branchId);
            }
            else
            {
                _logger.LogWarning("Rejected connection-time join to branch group {BranchId} by user {UserId}: not authorized for this branch", branchId, authenticatedUserId);
            }
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var authenticatedUserId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(authenticatedUserId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user-{authenticatedUserId}");
            _logger.LogInformation("User {UserId} disconnected from notification hub", authenticatedUserId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Join a specific branch notification group. See IsAuthorizedForBranchAsync — client-callable,
    /// so this is the primary path a malicious caller would use to reach another org's branch group.
    /// </summary>
    public async Task JoinBranch(string branchId)
    {
        if (!await IsAuthorizedForBranchAsync(branchId))
        {
            var authenticatedUserId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            _logger.LogWarning("Rejected JoinBranch({BranchId}) from user {UserId}: not authorized for this branch", branchId, authenticatedUserId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"branch-{branchId}");
        _logger.LogInformation("Connection joined branch group {BranchId}", branchId);
    }

    /// <summary>
    /// Leave a branch notification group
    /// </summary>
    public async Task LeaveBranch(string branchId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"branch-{branchId}");
        _logger.LogInformation("Connection left branch group {BranchId}", branchId);
    }

    /// <summary>
    /// Mark a notification as read
    /// </summary>
    public async Task MarkAsRead(Guid notificationId)
    {
        await Clients.Caller.SendAsync("NotificationRead", notificationId);
    }
}

/// <summary>
/// Service implementation for sending notifications via SignalR
/// </summary>
public class NotificationHubService : INotificationHubService
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<NotificationHubService> _logger;

    public NotificationHubService(
        IHubContext<NotificationHub> hubContext,
        ILogger<NotificationHubService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task SendToUserAsync(Guid userId, Notification notification)
    {
        var dto = MapToDto(notification);
        await _hubContext.Clients.Group($"user-{userId}").SendAsync("ReceiveNotification", dto);
        _logger.LogDebug("Sent notification {NotificationId} to user {UserId}", notification.Id, userId);
    }

    public async Task SendToBranchAsync(Guid branchId, Notification notification)
    {
        var dto = MapToDto(notification);
        await _hubContext.Clients.Group($"branch-{branchId}").SendAsync("ReceiveNotification", dto);
        _logger.LogDebug("Sent notification {NotificationId} to branch {BranchId}", notification.Id, branchId);
    }

    public async Task SendToAllAsync(Notification notification)
    {
        var dto = MapToDto(notification);
        await _hubContext.Clients.All.SendAsync("ReceiveNotification", dto);
        _logger.LogDebug("Sent notification {NotificationId} to all clients", notification.Id);
    }

    public async Task NotifyUnreadCountAsync(Guid userId, int count)
    {
        await _hubContext.Clients.Group($"user-{userId}").SendAsync("UnreadCountUpdated", count);
        _logger.LogDebug("Updated unread count for user {UserId}: {Count}", userId, count);
    }

    private static NotificationDto MapToDto(Notification notification) => new()
    {
        Id = notification.Id,
        Title = notification.Title,
        Message = notification.Message,
        Type = notification.Type.ToString(),
        Priority = notification.Priority.ToString(),
        IconClass = notification.IconClass,
        ActionUrl = notification.ActionUrl,
        CreatedAt = notification.CreatedAt,
        IsRead = notification.IsRead,
        ReadAt = notification.ReadAt
    };
}
