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
    /// Current connection state
    /// </summary>
    HubConnectionState State { get; }

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
