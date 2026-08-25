namespace QMgr.Application.Interfaces;

public interface IQueueService
{
    Task<int> CalculateEstimatedWaitAsync(Guid branchId, Guid serviceTypeId, CancellationToken cancellationToken = default);
    Task<int> GetActiveCountersForServiceTypeAsync(Guid serviceTypeId, CancellationToken cancellationToken = default);
    Task RecalculateQueueEstimatesAsync(Guid branchId, CancellationToken cancellationToken = default);
}
