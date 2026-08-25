using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

public interface ISignalRService
{
    event Action<TokenDto>? OnTokenCreated;
    event Action<TokenDto>? OnTokenCalled;
    event Action<Guid>? OnTokenCompleted;
    event Action<QueueStatusDto>? OnQueueUpdated;
    event Action<CounterStatusDto>? OnCounterStatusChanged;
    event Action<Guid>? OnPlaylistUpdated;
    event Action<Guid>? OnDisplayBannerUpdated;

    Task ConnectAsync(Guid branchId);
    Task DisconnectAsync();
    bool IsConnected { get; }
}

public class SignalRService : ISignalRService, IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private HubConnection? _displayHubConnection;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SignalRService> _logger;
    private Guid _currentBranchId;

    public event Action<TokenDto>? OnTokenCreated;
    public event Action<TokenDto>? OnTokenCalled;
    public event Action<Guid>? OnTokenCompleted;
    public event Action<QueueStatusDto>? OnQueueUpdated;
    public event Action<CounterStatusDto>? OnCounterStatusChanged;
    public event Action<Guid>? OnPlaylistUpdated;
    public event Action<Guid>? OnDisplayBannerUpdated;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    public SignalRService(IConfiguration configuration, ILogger<SignalRService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task ConnectAsync(Guid branchId)
    {
        if (_hubConnection != null && _currentBranchId == branchId && IsConnected)
            return;

        await DisconnectAsync();

        var hubUrl = $"{_configuration["ApiBaseUrl"]}/hubs/queue";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                // Add authentication token if needed
                // options.AccessTokenProvider = () => Task.FromResult(_authToken);
            })
            .WithAutomaticReconnect()
            .Build();

        // Register handlers
        _hubConnection.On<TokenDto>("TokenCreated", token =>
        {
            _logger.LogDebug("Token created: {TokenId}", token.Id);
            OnTokenCreated?.Invoke(token);
        });

        _hubConnection.On<TokenDto>("TokenCalled", token =>
        {
            _logger.LogDebug("Token called: {TokenId}", token.Id);
            OnTokenCalled?.Invoke(token);
        });

        _hubConnection.On<object>("TokenCompleted", data =>
        {
            var tokenId = Guid.Parse(data.GetType().GetProperty("tokenId")?.GetValue(data)?.ToString() ?? "");
            _logger.LogDebug("Token completed: {TokenId}", tokenId);
            OnTokenCompleted?.Invoke(tokenId);
        });

        _hubConnection.On<QueueStatusDto>("QueueUpdated", status =>
        {
            _logger.LogDebug("Queue updated");
            OnQueueUpdated?.Invoke(status);
        });

        _hubConnection.On<CounterStatusDto>("CounterStatusChanged", counter =>
        {
            _logger.LogDebug("Counter status changed: {CounterId}", counter.Id);
            OnCounterStatusChanged?.Invoke(counter);
        });

        _hubConnection.Closed += async (error) =>
        {
            _logger.LogWarning(error, "SignalR connection closed");
            await Task.Delay(5000);
            await ConnectAsync(branchId);
        };

        try
        {
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("SubscribeToBranch", branchId);
            _currentBranchId = branchId;
            _logger.LogInformation("Connected to SignalR hub for branch {BranchId}", branchId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to SignalR hub");
        }

        await ConnectDisplayHubAsync(branchId);
    }

    private async Task ConnectDisplayHubAsync(Guid branchId)
    {
        var displayHubUrl = $"{_configuration["ApiBaseUrl"]}/hubs/display";

        _displayHubConnection = new HubConnectionBuilder()
            .WithUrl(displayHubUrl)
            .WithAutomaticReconnect()
            .Build();

        _displayHubConnection.On<System.Text.Json.JsonElement>("PlaylistUpdated", data =>
        {
            if (data.TryGetProperty("id", out var idProp) && idProp.TryGetGuid(out var playlistId))
            {
                _logger.LogDebug("Playlist updated: {PlaylistId}", playlistId);
                OnPlaylistUpdated?.Invoke(playlistId);
            }
        });

        _displayHubConnection.On<Guid>("DisplayBannerUpdated", branchId =>
        {
            _logger.LogDebug("Display banner updated: {BranchId}", branchId);
            OnDisplayBannerUpdated?.Invoke(branchId);
        });

        try
        {
            await _displayHubConnection.StartAsync();
            await _displayHubConnection.InvokeAsync("RegisterDisplay", branchId, "customer", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to display hub");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_hubConnection != null)
        {
            try
            {
                if (IsConnected)
                {
                    await _hubConnection.InvokeAsync("UnsubscribeFromBranch", _currentBranchId);
                }
                await _hubConnection.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting from SignalR hub");
            }
            finally
            {
                await _hubConnection.DisposeAsync();
                _hubConnection = null;
            }
        }

        if (_displayHubConnection != null)
        {
            try
            {
                await _displayHubConnection.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting from display hub");
            }
            finally
            {
                await _displayHubConnection.DisposeAsync();
                _displayHubConnection = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
