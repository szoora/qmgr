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
    /// Event fired as a roster bulk-import job progresses — backs the live progress bar on the
    /// Bulk Import panel. Same connection, same branch-group membership.
    /// </summary>
    event Func<RosterImportProgressEvent, Task>? OnRosterImportProgressReceived;

    /// <summary>
    /// Fired when the server reports that this user's role or role permissions changed, so the
    /// client can re-fetch its permission set instead of showing stale controls until re-login.
    /// </summary>
    event Func<Task>? OnPermissionsChanged;

    /// <summary>Staff Performance: the caller's score for the current period changed (a record about them was finalised, annulled or corrected).</summary>
    event Func<StaffScoreUpdatedEvent, Task>? OnStaffScoreUpdated;

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

    /// <summary>Marks one read and says whether it worked. Prefer this over the void one.</summary>
    Task<bool> TryMarkAsReadAsync(Guid notificationId);

    /// <summary>Marks all read and returns the remaining unread count; null when the call failed.</summary>
    Task<int?> MarkAllAsReadRemainingAsync();

    /// <summary>
    /// The notification centre's page: <c>GET api/v1/notifications?eventKey=&amp;offset=&amp;limit=</c>.
    /// A null <paramref name="eventKey"/> is every notification. Returns the page and whether a
    /// further page exists. THROWS on failure with the server's message — the centre is a page
    /// of its own and must say why it is empty, unlike the bell preview above.
    /// </summary>
    Task<NotificationPageResult> GetNotificationsAsync(string? eventKey, int offset, int limit, bool unreadOnly = false);

    /// <summary>
    /// Mark every notification of one event key as read (<c>POST api/v1/notifications/read-all?eventKey=</c>);
    /// null marks all. THROWS on failure.
    /// </summary>
    Task MarkAllAsReadAsync(string? eventKey);

    /// <summary>
    /// Delete a notification
    /// </summary>
    Task DeleteAsync(Guid notificationId);

    // =====================================================================================
    // Preferences and the delivery log.
    //
    // These four THROW on failure, carrying the server's own message — unlike the five methods
    // above, which swallow and return an empty result. That split is deliberate rather than
    // untidy: a bell that fails to refresh should not break the page it sits in, but a preferences
    // form that silently discards a save is the exact failure mode CLAUDE.md records for
    // IQueueApiService (CounterTerminal reporting success on a failed call). A caller here wraps
    // them in try/catch and shows the real reason.
    // =====================================================================================

    /// <summary>
    /// The event categories a person can be reached about, with their defaults. Fetched rather than
    /// hardcoded in the UI so a new category added to <c>NotificationEventKeys</c> appears in the
    /// preferences panel without a Web change.
    /// </summary>
    Task<List<NotificationEventDefinition>> GetPreferenceEventsAsync();

    /// <summary>The caller's OWN channel preferences, every known event filled in at its default.</summary>
    Task<UserNotificationPreferencesDto> GetPreferencesAsync();

    Task SavePreferencesAsync(UserNotificationPreferencesDto preferences);

    /// <summary>
    /// Delivery attempts, newest first — "was this actually delivered?". Requires
    /// notifications.manage; recipient addresses arrive masked from the API.
    /// </summary>
    Task<List<NotificationDeliveryDto>> GetDeliveriesAsync(bool failuresOnly = false, int limit = 100);
}

/// <summary>
/// One page of the notification centre. The API returns a bare array with no total, so the page
/// asks for one more row than it shows and reports <see cref="HasMore"/> instead of a count.
/// </summary>
public record NotificationPageResult(List<NotificationDto> Items, bool HasMore);
