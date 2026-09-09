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

    public event Func<NotificationDto, Task>? OnNotificationReceived;
    public event Func<int, Task>? OnUnreadCountUpdated;
    public event Func<VisitorActivityEvent, Task>? OnVisitorActivityReceived;
    public event Func<RosterImportProgressEvent, Task>? OnRosterImportProgressReceived;
    public event Func<Task>? OnPermissionsChanged;
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
            if (_hubConnection.State == HubConnectionState.Connected)
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

        // Handle incoming notifications
        _hubConnection.On<NotificationDto>("ReceiveNotification", async notification =>
        {
            _logger.LogDebug("Received notification: {Title}", notification.Title);
            if (OnNotificationReceived != null)
            {
                await OnNotificationReceived.Invoke(notification);
            }
        });

        // Handle unread count updates
        _hubConnection.On<int>("UnreadCountUpdated", async count =>
        {
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
}
