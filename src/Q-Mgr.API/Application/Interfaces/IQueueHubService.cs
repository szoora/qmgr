using QMgr.Domain.Entities.Queue;

namespace QMgr.Application.Interfaces;

public interface IQueueHubService
{
    Task NotifyTokenCreatedAsync(Token token, CancellationToken cancellationToken = default);
    Task NotifyTokenCalledAsync(Token token, Counter counter, CancellationToken cancellationToken = default);
    Task NotifyTokenServingAsync(Token token, CancellationToken cancellationToken = default);
    Task NotifyTokenCompletedAsync(Token token, CancellationToken cancellationToken = default);
    Task NotifyQueueUpdatedAsync(Guid branchId, CancellationToken cancellationToken = default);
    Task NotifyCounterStatusChangedAsync(Counter counter, CancellationToken cancellationToken = default);
}
