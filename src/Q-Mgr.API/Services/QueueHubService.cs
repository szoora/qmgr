using QMgr.Application.Mappings;
using Microsoft.AspNetCore.SignalR;
using QMgr.API.Hubs;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Queue;

namespace QMgr.API.Services;

public class QueueHubService : IQueueHubService
{
    private readonly IHubContext<QueueHub> _hubContext;
    private readonly ILogger<QueueHubService> _logger;

    public QueueHubService(IHubContext<QueueHub> hubContext, ILogger<QueueHubService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyTokenCreatedAsync(Token token, CancellationToken cancellationToken = default)
    {
        var dto = TokenMapper.ToPublicDto(token);
        await _hubContext.Clients.Group($"branch_{token.BranchId}")
            .SendAsync("TokenCreated", dto, cancellationToken);
        _logger.LogDebug("Notified TokenCreated for branch {BranchId}", token.BranchId);
    }

    public async Task NotifyTokenCalledAsync(Token token, Counter counter, CancellationToken cancellationToken = default)
    {
        var notification = new TokenCalledNotification
        {
            TokenId = token.Id,
            DisplayNumber = token.DisplayNumber,
            CounterNumber = counter.CounterNumber,
            // No name (2026-09-25). QueueHub is anonymous — every public board subscribes with the
            // branch id alone — so a customer's name here went to anybody holding that id. No
            // screen reads it; the number is what a board calls.
            CustomerName = null,
            WaitTimeMinutes = token.ActualWaitMinutes ?? 0
        };

        await _hubContext.Clients.Group($"branch_{token.BranchId}")
            .SendAsync("TokenCalled", notification, cancellationToken);
        _logger.LogDebug("Notified TokenCalled for token {TokenId} at counter {CounterNumber}", token.Id, counter.CounterNumber);
    }

    public async Task NotifyTokenServingAsync(Token token, CancellationToken cancellationToken = default)
    {
        var dto = TokenMapper.ToPublicDto(token);
        await _hubContext.Clients.Group($"branch_{token.BranchId}")
            .SendAsync("TokenServing", dto, cancellationToken);
        _logger.LogDebug("Notified TokenServing for token {TokenId}", token.Id);
    }

    public async Task NotifyTokenCompletedAsync(Token token, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.Group($"branch_{token.BranchId}")
            .SendAsync("TokenCompleted", new { tokenId = token.Id }, cancellationToken);
        _logger.LogDebug("Notified TokenCompleted for token {TokenId}", token.Id);
    }

    public async Task NotifyQueueUpdatedAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        // Notify that queue has been updated - clients should refresh
        await _hubContext.Clients.Group($"branch_{branchId}")
            .SendAsync("QueueUpdated", new { branchId, timestamp = DateTime.UtcNow }, cancellationToken);
        _logger.LogDebug("Notified QueueUpdated for branch {BranchId}", branchId);
    }

    public async Task NotifyCounterStatusChangedAsync(Counter counter, CancellationToken cancellationToken = default)
    {
        var dto = new CounterStatusDto
        {
            Id = counter.Id,
            CounterNumber = counter.CounterNumber,
            DisplayName = counter.DisplayName,
            Status = counter.Status.ToString(),
            CurrentTokenDisplay = counter.CurrentToken?.DisplayNumber,
            ServingCustomerName = null, // the anonymous hub carries numbers, never names (see TokenCalled)
            TokensServedToday = 0 // Would need to calculate this
        };

        await _hubContext.Clients.Group($"branch_{counter.BranchId}")
            .SendAsync("CounterStatusChanged", dto, cancellationToken);
        _logger.LogDebug("Notified CounterStatusChanged for counter {CounterId}", counter.Id);
    }
}
