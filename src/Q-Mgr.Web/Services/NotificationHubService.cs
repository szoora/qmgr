using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// SignalR client service for real-time notifications
/// </summary>
public class NotificationClientService : INotificationClientService
{
    private readonly ILogger<NotificationClientService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ITokenStorageService _tokenStorage;
    private HubConnection? _hubConnection;
    private bool _isDisposed;

    /// <summary>
    /// The branch groups this circuit WANTS to be in, independent of whether the connection is
    /// up right now. JoinBranchAsync used to be a bare "invoke if Connected" with a silent no-op
    /// otherwise, which lost the membership in two ordinary situations: a page whose
    /// OnInitializedAsync runs before MainLayout has finished starting the hub, and any
    /// reconnect (SignalR groups do not survive one — the server rebuilds them from scratch).
    /// Either way the visitor activity board sat there looking connected and never received a
    /// single push. Recording the intent here and replaying it on every successful connect is
    /// what makes "live" actually mean live.
    /// </summary>
    private readonly HashSet<Guid> _joinedBranches = new();

    /// <summary>
    /// Whose connection this is. A pushed notification for anybody else is DROPPED, not shown
    /// (2026-09-23): the server once pushed recipient-less notifications through Clients.All, so a
    /// payment failure at one school rang the bell at every other. The server no longer can; this
    /// is the second line, so a routing mistake there can never again reach somebody's bell here.
    /// </summary>
    private Guid _userId;

    /// <summary>The newest unread count seen, by the server's own counting time. An older count arriving late is ignored.</summary>
    private long _lastCountTicks;

    public event Func<NotificationDto, Task>? OnNotificationReceived;
    public event Func<int, Task>? OnUnreadCountUpdated;
    public event Func<VisitorActivityEvent, Task>? OnVisitorActivityReceived;
    public event Func<RosterImportProgressEvent, Task>? OnRosterImportProgressReceived;
    public event Func<Task>? OnPermissionsChanged;
    public event Func<StaffScoreUpdatedEvent, Task>? OnStaffScoreUpdated;
    public event Action? ConnectionStateChanged;

    public HubConnectionState State => _hubConnection?.State ?? HubConnectionState.Disconnected;

    public NotificationClientService(
        ILogger<NotificationClientService> logger,
        IConfiguration configuration,
        ITokenStorageService tokenStorage)
    {
        _logger = logger;
        _configuration = configuration;
        _tokenStorage = tokenStorage;
    }

    public async Task StartAsync(Guid userId, Guid? branchId = null)
    {
        if (_hubConnection != null)
        {
            // Already connected AS THIS PERSON. A connection opened for somebody else — a sign-out
            // and sign-in on the same circuit — is torn down below: its groups are the previous
            // user's, and keeping it would deliver their notifications to the new one.
            if (_hubConnection.State == HubConnectionState.Connected && userId == _userId)
            {
                _logger.LogDebug("Notification hub already connected");
                return;
            }

            // BUG FIX: any other state (Connecting/Reconnecting/Disconnected) used to fall
            // through and build a brand-new HubConnection while leaving this one running —
            // if it later connected/reconnected on its own, both ended up live at once, each
            // forwarding "ReceiveNotification" to the same shared OnNotificationReceived
            // event, so a single server-side notification rendered as N duplicates in the UI.
            // Always tear down any existing connection before replacing it.
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }

        // "ApiBaseUrl" — the same key Program.cs, ISignalRService and every other outbound
        // caller reads. This line asked for "ApiSettings:BaseUrl", which is defined in NO
        // appsettings file in this repository, so it ALWAYS fell through to the hardcoded
        // https://localhost:5001 default. On a dev machine that is coincidentally where the API
        // listens, which is why it never showed up locally; in production the API listens on
        // http://127.0.0.1:<ApiPort> behind nginx, so the notification hub connected to nothing,
        // failed, and was swallowed by MainLayout's catch — leaving the visitor activity board
        // stuck on "Reconnecting..." and every SignalR push (notifications, live board, roster
        // import progress, permission changes) silently dead in the deployed app.
        var apiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:5001";
        // userId/branchId stay as routing hints for which groups to join, but
        // the hub no longer trusts them for identity — it derives the real
        // user from the JWT below and only honors userId if it matches.
        var hubUrl = $"{apiBaseUrl}/hubs/notifications?userId={userId}";
        if (branchId.HasValue)
        {
            hubUrl += $"&branchId={branchId}";
        }

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(_tokenStorage.AccessToken);
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _userId = userId;
        _lastCountTicks = 0;

        // Handle incoming notifications
        _hubConnection.On<NotificationDto>("ReceiveNotification", async notification =>
        {
            if (notification.UserId != _userId)
            {
                _logger.LogWarning("Dropped notification {NotificationId} pushed to this connection for another user", notification.Id);
                return;
            }

            _logger.LogDebug("Received notification: {Title}", notification.Title);
            if (OnNotificationReceived != null)
            {
                await OnNotificationReceived.Invoke(notification);
            }
        });

        // Handle unread count updates
        // Carries the moment the server COUNTED. Two notifications landing together each count and
        // push, and the first count can arrive last; keeping only the newest stops the badge
        // settling one short.
        _hubConnection.On<int, long>("UnreadCountUpdated", async (count, countedAtTicks) =>
        {
            if (countedAtTicks < _lastCountTicks) return;
            _lastCountTicks = countedAtTicks;

            _logger.LogDebug("Unread count updated: {Count}", count);
            if (OnUnreadCountUpdated != null)
            {
                await OnUnreadCountUpdated.Invoke(count);
            }
        });

        // Handle visitor activity (live board)
        _hubConnection.On<VisitorActivityEvent>("VisitorActivity", async activity =>
        {
            _logger.LogDebug("Visitor activity: {Kind} - {VisitorName}", activity.Kind, activity.Visitor.FullName);
            if (OnVisitorActivityReceived != null)
            {
                await OnVisitorActivityReceived.Invoke(activity);
            }
        });

        // Handle roster bulk-import progress
        _hubConnection.On<RosterImportProgressEvent>("RosterImportProgress", async progress =>
        {
            _logger.LogDebug("Roster import progress: job {JobId} {Processed}/{Total}", progress.JobId, progress.ProcessedRows, progress.TotalRows);
            if (OnRosterImportProgressReceived != null)
            {
                await OnRosterImportProgressReceived.Invoke(progress);
            }
        });

        // Role / permission changes pushed by UsersController / RolesController
        // Staff Performance: a record about this user was finalised; the portal's score tile
        // updates live. Same shape as the other events; subscribers unsubscribe before subscribing.
        _hubConnection.On<StaffScoreUpdatedEvent>("StaffScoreUpdated", async update =>
        {
            if (OnStaffScoreUpdated != null)
            {
                try { await OnStaffScoreUpdated.Invoke(update); }
                catch (Exception ex) { _logger.LogError(ex, "StaffScoreUpdated handler failed"); }
            }
        });

        _hubConnection.On<Guid>("PermissionsChanged", async _ =>
        {
            _logger.LogInformation("Server reported a permission change for the current user");
            if (OnPermissionsChanged != null)
            {
                await OnPermissionsChanged.Invoke();
            }
        });

        // Handle reconnection events
        _hubConnection.Reconnecting += error =>
        {
            _logger.LogWarning("Notification hub reconnecting: {Error}", error?.Message);
            ConnectionStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += async connectionId =>
        {
            _logger.LogInformation("Notification hub reconnected: {ConnectionId}", connectionId);
            await RejoinBranchesAsync();
            ConnectionStateChanged?.Invoke();
        };

        _hubConnection.Closed += error =>
        {
            _logger.LogWarning("Notification hub connection closed: {Error}", error?.Message);
            ConnectionStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        try
        {
            await _hubConnection.StartAsync();
            _logger.LogInformation("Connected to notification hub for user {UserId}", userId);
            await RejoinBranchesAsync();
            ConnectionStateChanged?.Invoke();
        }
        catch (OperationCanceledException ex) when (ex.InnerException is not TimeoutException)
        {
            // The circuit went away while the hub was still negotiating — someone navigated or
            // closed the tab within a second of opening a page. Cancellation is the correct outcome,
            // not a fault, and logging it as an error with a stack trace (as this did until the UI
            // sweep of 2026-09-16 filled the log with them) buries real failures. A genuine
            // connect TIMEOUT carries a TimeoutException inside and still logs as an error below.
            _logger.LogDebug("Notification hub connect cancelled before it completed (circuit closing)");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to notification hub");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
            _logger.LogInformation("Disconnected from notification hub");
        }
    }

    public async Task JoinBranchAsync(Guid branchId)
    {
        lock (_joinedBranches) _joinedBranches.Add(branchId);

        if (_hubConnection is { State: HubConnectionState.Connected })
        {
            await _hubConnection.InvokeAsync("JoinBranch", branchId.ToString());
            _logger.LogDebug("Joined branch notification group: {BranchId}", branchId);
        }
        else
        {
            // Not a failure — the membership is recorded and RejoinBranchesAsync will send it
            // the moment the connection comes up. Callers race hub startup all the time.
            _logger.LogDebug("Deferred joining branch group {BranchId} until the hub connects", branchId);
        }
    }

    public async Task LeaveBranchAsync(Guid branchId)
    {
        lock (_joinedBranches) _joinedBranches.Remove(branchId);

        if (_hubConnection is { State: HubConnectionState.Connected })
        {
            await _hubConnection.InvokeAsync("LeaveBranch", branchId.ToString());
            _logger.LogDebug("Left branch notification group: {BranchId}", branchId);
        }
    }

    /// <summary>Replays every wanted branch group onto a freshly established connection.</summary>
    private async Task RejoinBranchesAsync()
    {
        Guid[] wanted;
        lock (_joinedBranches) wanted = _joinedBranches.ToArray();
        if (wanted.Length == 0 || _hubConnection is not { State: HubConnectionState.Connected }) return;

        foreach (var branchId in wanted)
        {
            try
            {
                await _hubConnection.InvokeAsync("JoinBranch", branchId.ToString());
                _logger.LogDebug("Re-joined branch notification group: {BranchId}", branchId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not re-join branch notification group {BranchId}", branchId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}

/// <summary>
/// API service for fetching and managing notifications
/// </summary>
public class NotificationApiService : INotificationApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NotificationApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public NotificationApiService(
        HttpClient httpClient,
        ILogger<NotificationApiService> logger,
        JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public async Task<IEnumerable<NotificationDto>> GetNotificationsAsync(bool unreadOnly = false, int limit = 50)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/v1/notifications?unreadOnly={unreadOnly}&limit={limit}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IEnumerable<NotificationDto>>(_jsonOptions) ?? Enumerable.Empty<NotificationDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch notifications");
            return Enumerable.Empty<NotificationDto>();
        }
    }

    public async Task<int> GetUnreadCountAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/v1/notifications/count");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<int>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch unread count");
            return 0;
        }
    }

    public async Task MarkAsReadAsync(Guid notificationId)
    {
        try
        {
            await _httpClient.PostAsync($"api/v1/notifications/{notificationId}/read", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark notification as read: {NotificationId}", notificationId);
        }
    }

    public async Task MarkAllAsReadAsync()
    {
        try
        {
            await _httpClient.PostAsync("api/v1/notifications/read-all", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark all notifications as read");
        }
    }

    /// <summary>
    /// Marks one as read and SAYS WHETHER IT WORKED. <see cref="MarkAsReadAsync"/> swallows every
    /// failure and returns void, so a caller could only show the row as read and hope — and a
    /// refused call then reappeared as unread on the next load with nothing having said so. This is
    /// the same rule as IQueueApiService's ten silent methods: branch on the return.
    /// </summary>
    public async Task<bool> TryMarkAsReadAsync(Guid notificationId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/notifications/{notificationId}/read", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark notification as read: {NotificationId}", notificationId);
            return false;
        }
    }

    /// <summary>
    /// Marks everything read and returns the caller's REMAINING unread count, which the API answers
    /// with. Null means the call failed, so the badge is left where it was rather than zeroed on a
    /// request that never landed.
    /// </summary>
    public async Task<int?> MarkAllAsReadRemainingAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("api/v1/notifications/read-all", null);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync();
            return int.TryParse(body, out var remaining) ? remaining : 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark all notifications as read");
            return null;
        }
    }

    public async Task<NotificationPageResult> GetNotificationsAsync(string? eventKey, int offset, int limit, bool unreadOnly = false)
    {
        limit = Math.Clamp(limit, 1, 199);
        var url = $"api/v1/notifications?offset={Math.Max(0, offset)}&limit={limit + 1}";
        if (unreadOnly) url += "&unreadOnly=true";
        if (!string.IsNullOrWhiteSpace(eventKey))
            url += $"&eventKey={Uri.EscapeDataString(eventKey)}";

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));

        var rows = await response.Content.ReadFromJsonAsync<List<NotificationDto>>(_jsonOptions) ?? new();
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new NotificationPageResult(rows, hasMore);
    }

    public async Task MarkAllAsReadAsync(string? eventKey)
    {
        var url = "api/v1/notifications/read-all";
        if (!string.IsNullOrWhiteSpace(eventKey))
            url += $"?eventKey={Uri.EscapeDataString(eventKey)}";

        var response = await _httpClient.PostAsync(url, null);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    public async Task DeleteAsync(Guid notificationId)
    {
        try
        {
            await _httpClient.DeleteAsync($"api/v1/notifications/{notificationId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete notification: {NotificationId}", notificationId);
        }
    }

    // ---------------------------------------------------------------------------------
    // Preferences and the delivery log — these THROW. See the interface for why.
    // ---------------------------------------------------------------------------------

    public async Task<List<NotificationEventDefinition>> GetPreferenceEventsAsync()
    {
        var response = await _httpClient.GetAsync("api/v1/notifications/preferences/events");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<List<NotificationEventDefinition>>(_jsonOptions) ?? new();
    }

    public async Task<UserNotificationPreferencesDto> GetPreferencesAsync()
    {
        var response = await _httpClient.GetAsync("api/v1/notifications/preferences");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<UserNotificationPreferencesDto>(_jsonOptions) ?? new();
    }

    public async Task SavePreferencesAsync(UserNotificationPreferencesDto preferences)
    {
        var response = await _httpClient.PutAsJsonAsync("api/v1/notifications/preferences", preferences, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    public async Task<List<NotificationDeliveryDto>> GetDeliveriesAsync(bool failuresOnly = false, int limit = 100)
    {
        var response = await _httpClient.GetAsync($"api/v1/notifications/deliveries?failuresOnly={failuresOnly}&limit={limit}");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<List<NotificationDeliveryDto>>(_jsonOptions) ?? new();
    }
}
