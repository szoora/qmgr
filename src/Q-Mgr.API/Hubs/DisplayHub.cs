using Microsoft.AspNetCore.SignalR;
using QMgr.Application.DTOs;

namespace QMgr.API.Hubs;

/// <summary>
/// Hub for customer displays and kiosks (no authentication required for public displays)
/// </summary>
public class DisplayHub : Hub
{
    private readonly ILogger<DisplayHub> _logger;

    public DisplayHub(ILogger<DisplayHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var deviceId = Context.GetHttpContext()?.Request.Query["deviceId"].FirstOrDefault();
        if (!string.IsNullOrEmpty(deviceId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"device_{deviceId}");
        }
        _logger.LogInformation("Display connected: {ConnectionId}, Device: {DeviceId}", Context.ConnectionId, deviceId);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Register display for a specific branch
    /// </summary>
    public async Task RegisterDisplay(Guid branchId, string displayType, string? deviceId = null)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"display_{branchId}");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"display_{branchId}_{displayType}");

        if (!string.IsNullOrEmpty(deviceId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"device_{deviceId}");
        }

        _logger.LogInformation("Display registered: Branch={BranchId}, Type={DisplayType}, Device={DeviceId}",
            branchId, displayType, deviceId);

        // Send initial queue status
        await Clients.Caller.SendAsync("DisplayRegistered", new
        {
            branchId,
            displayType,
            registeredAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Send heartbeat from display
    /// </summary>
    public async Task Heartbeat(string deviceId)
    {
        _logger.LogDebug("Heartbeat from device: {DeviceId}", deviceId);

        await Clients.Caller.SendAsync("HeartbeatAck", new
        {
            serverTime = DateTime.UtcNow
        });
    }
}

/// <summary>
/// Service to send updates to displays
/// </summary>
public interface IDisplayHubContext
{
    Task UpdateQueueBoard(Guid branchId, QueueBoardData data);
    Task UpdateNowServing(Guid branchId, NowServingData data);
    Task UpdatePlaylistContent(Guid branchId, PlaylistDto playlist);
    Task UpdateDisplayBanner(Guid branchId);
    Task AnnounceToken(Guid branchId, TokenAnnouncementData announcement);
    Task SendCommand(string deviceId, DisplayCommand command);
}

public class DisplayHubContext : IDisplayHubContext
{
    private readonly IHubContext<DisplayHub> _hubContext;

    public DisplayHubContext(IHubContext<DisplayHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task UpdateQueueBoard(Guid branchId, QueueBoardData data)
    {
        await _hubContext.Clients.Group($"display_{branchId}")
            .SendAsync("QueueBoardUpdated", data);
    }

    public async Task UpdateNowServing(Guid branchId, NowServingData data)
    {
        await _hubContext.Clients.Group($"display_{branchId}")
            .SendAsync("NowServingUpdated", data);
    }

    public async Task UpdatePlaylistContent(Guid branchId, PlaylistDto playlist)
    {
        await _hubContext.Clients.Group($"display_{branchId}")
            .SendAsync("PlaylistUpdated", playlist);
    }

    public async Task UpdateDisplayBanner(Guid branchId)
    {
        await _hubContext.Clients.Group($"display_{branchId}")
            .SendAsync("DisplayBannerUpdated", branchId);
    }

    public async Task AnnounceToken(Guid branchId, TokenAnnouncementData announcement)
    {
        await _hubContext.Clients.Group($"display_{branchId}")
            .SendAsync("TokenAnnouncement", announcement);
    }

    public async Task SendCommand(string deviceId, DisplayCommand command)
    {
        await _hubContext.Clients.Group($"device_{deviceId}")
            .SendAsync("Command", command);
    }
}

public record QueueBoardData
{
    public List<TokenDisplayItem> WaitingTokens { get; init; } = new();
    public List<CounterStatusItem> Counters { get; init; } = new();
    public DateTime UpdatedAt { get; init; }
}

public record TokenDisplayItem
{
    public string DisplayNumber { get; init; } = string.Empty;
    public string ServiceType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public record CounterStatusItem
{
    public string CounterNumber { get; init; } = string.Empty;
    public string? CurrentToken { get; init; }
    public string Status { get; init; } = string.Empty;
}

public record NowServingData
{
    public List<NowServingItem> Items { get; init; } = new();
}

public record NowServingItem
{
    public string TokenDisplayNumber { get; init; } = string.Empty;
    public string CounterNumber { get; init; } = string.Empty;
    public bool IsNew { get; init; }
}

public record TokenAnnouncementData
{
    public string DisplayNumber { get; init; } = string.Empty;
    public string CounterNumber { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public bool PlaySound { get; init; } = true;
    public bool TextToSpeech { get; init; } = false;
}

public record DisplayCommand
{
    public string Type { get; init; } = string.Empty; // refresh, restart, update_settings
    public Dictionary<string, object>? Parameters { get; init; }
}
