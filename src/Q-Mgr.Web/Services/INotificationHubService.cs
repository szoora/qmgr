using Microsoft.AspNetCore.SignalR.Client;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Service for connecting to the notification SignalR hub and managing real-time notifications (client-side)
/// </summary>
public interface INotificationClientService : IAsyncDisposable
{
    /// <summary>
    /// Event fired when a new notification is received
    /// </summary>
    event Func<NotificationDto, Task>? OnNotificationReceived;

    /// <summary>
    /// Event fired when unread count is updated
    /// </summary>
    event Func<int, Task>? OnUnreadCountUpdated;

    /// <summary>
    /// Event fired when a visitor check-in/out/flag/etc. happens on a joined branch — backs the
    /// live visitor activity board. Rides this same connection rather than a second hub.
    /// </summary>
    event Func<VisitorActivityEvent, Task>? OnVisitorActivityReceived;

    /// <summary>
    /// Current connection state
    /// </summary>
    HubConnectionState State { get; }

    /// <summary>
    /// Fires whenever the underlying connection state changes (connecting/reconnecting/
    /// reconnected/closed) — for any UI showing a live/reconnecting indicator tied to this
    /// connection (e.g. the visitor activity board) rather than just checking `State` once at
    /// load and never again.
    /// </summary>
    event Action? ConnectionStateChanged;

    /// <summary>
    /// Start the SignalR connection
    /// </summary>
    Task StartAsync(Guid userId, Guid? branchId = null);

    /// <summary>
    /// Stop the SignalR connection
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Join a branch notification group
    /// </summary>
    Task JoinBranchAsync(Guid branchId);

    /// <summary>
    /// Leave a branch notification group
    /// </summary>
    Task LeaveBranchAsync(Guid branchId);
}

/// <summary>
/// Service for fetching notifications from the API
/// </summary>
public interface INotificationApiService
{
    /// <summary>
    /// Get notifications for the current user
    /// </summary>
    Task<IEnumerable<NotificationDto>> GetNotificationsAsync(bool unreadOnly = false, int limit = 50);

    /// <summary>
    /// Get unread notification count
    /// </summary>
    Task<int> GetUnreadCountAsync();

    /// <summary>
    /// Mark a notification as read
    /// </summary>
    Task MarkAsReadAsync(Guid notificationId);

    /// <summary>
    /// Mark all notifications as read
    /// </summary>
    Task MarkAllAsReadAsync();

    /// <summary>
    /// Delete a notification
    /// </summary>
    Task DeleteAsync(Guid notificationId);
}
