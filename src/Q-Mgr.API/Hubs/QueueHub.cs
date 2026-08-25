using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using QMgr.Application.DTOs;

namespace QMgr.API.Hubs;

// Intentionally anonymous, not a dev-mode leftover: every method here is
// subscribe/unsubscribe to a SignalR group (real-time queue status
// broadcasts), never a mutating action — CallNextToken/CompleteToken/etc.
// go through the already-authorized REST API. Public customer-display and
// queue-board screens (CustomerDisplay.razor, QueueBoard.razor) connect to
// this hub without login by design, same as CounterTerminal.razor for
// staff — so this can't require auth without breaking the public displays.
[AllowAnonymous]
public class QueueHub : Hub
{
    private readonly ILogger<QueueHub> _logger;

    public QueueHub(ILogger<QueueHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to queue updates for a specific branch
    /// </summary>
    public async Task SubscribeToBranch(Guid branchId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"branch_{branchId}");
        _logger.LogInformation("Client {ConnectionId} subscribed to branch {BranchId}", Context.ConnectionId, branchId);
    }

    /// <summary>
    /// Unsubscribe from branch updates
    /// </summary>
    public async Task UnsubscribeFromBranch(Guid branchId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"branch_{branchId}");
        _logger.LogInformation("Client {ConnectionId} unsubscribed from branch {BranchId}", Context.ConnectionId, branchId);
    }

    /// <summary>
    /// Subscribe to counter-specific updates
    /// </summary>
    public async Task SubscribeToCounter(Guid counterId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"counter_{counterId}");
        _logger.LogInformation("Client {ConnectionId} subscribed to counter {CounterId}", Context.ConnectionId, counterId);
    }

    /// <summary>
    /// Unsubscribe from counter updates
    /// </summary>
    public async Task UnsubscribeFromCounter(Guid counterId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"counter_{counterId}");
    }
}

/// <summary>
/// Service to send real-time updates via SignalR
/// </summary>
public interface IQueueHubContext
{
    Task NotifyTokenCreated(Guid branchId, TokenDto token);
    Task NotifyTokenCalled(Guid branchId, TokenCalledNotification notification);
    Task NotifyTokenCompleted(Guid branchId, Guid tokenId);
    Task NotifyQueueUpdated(Guid branchId, QueueStatusDto status);
    Task NotifyCounterStatusChanged(Guid branchId, CounterStatusDto counter);
}

public class QueueHubContext : IQueueHubContext
{
    private readonly IHubContext<QueueHub> _hubContext;

    public QueueHubContext(IHubContext<QueueHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyTokenCreated(Guid branchId, TokenDto token)
    {
        await _hubContext.Clients.Group($"branch_{branchId}")
            .SendAsync("TokenCreated", token);
    }

    public async Task NotifyTokenCalled(Guid branchId, TokenCalledNotification notification)
    {
        await _hubContext.Clients.Group($"branch_{branchId}")
            .SendAsync("TokenCalled", notification);
    }

    public async Task NotifyTokenCompleted(Guid branchId, Guid tokenId)
    {
        await _hubContext.Clients.Group($"branch_{branchId}")
            .SendAsync("TokenCompleted", new { tokenId });
    }

    public async Task NotifyQueueUpdated(Guid branchId, QueueStatusDto status)
    {
        await _hubContext.Clients.Group($"branch_{branchId}")
            .SendAsync("QueueUpdated", status);
    }

    public async Task NotifyCounterStatusChanged(Guid branchId, CounterStatusDto counter)
    {
        await _hubContext.Clients.Group($"branch_{branchId}")
            .SendAsync("CounterStatusChanged", counter);
    }
}

public record TokenCalledNotification
{
    public Guid TokenId { get; init; }
    public string DisplayNumber { get; init; } = string.Empty;
    public string CounterNumber { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public int WaitTimeMinutes { get; init; }
}
