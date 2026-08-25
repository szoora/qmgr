using QMgr.Domain.Entities.Queue;

namespace QMgr.Application.Interfaces;

public interface IWebhookService
{
    Task TriggerTokenCreatedAsync(Token token, CancellationToken cancellationToken = default);
    Task TriggerTokenCalledAsync(Token token, CancellationToken cancellationToken = default);
    Task TriggerTokenServingAsync(Token token, CancellationToken cancellationToken = default);
    Task TriggerTokenCompletedAsync(Token token, CancellationToken cancellationToken = default);
    Task TriggerTokenCancelledAsync(Token token, CancellationToken cancellationToken = default);
    Task TriggerTokenNoShowAsync(Token token, CancellationToken cancellationToken = default);
    Task ProcessPendingWebhooksAsync(CancellationToken cancellationToken = default);
}
