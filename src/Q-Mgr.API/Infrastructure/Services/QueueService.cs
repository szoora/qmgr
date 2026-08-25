using QMgr.Application.Interfaces;
using QMgr.Domain.Enums;
using QMgr.Domain.Interfaces;

namespace QMgr.Infrastructure.Services;

public class QueueService : IQueueService
{
    private readonly IUnitOfWork _unitOfWork;

    public QueueService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<int> CalculateEstimatedWaitAsync(Guid branchId, Guid serviceTypeId, CancellationToken cancellationToken = default)
    {
        // Get service type average time
        var serviceType = await _unitOfWork.ServiceTypes.GetByIdAsync(serviceTypeId, cancellationToken);
        if (serviceType == null)
            return 0;

        // Get waiting tokens count
        var waitingTokens = await _unitOfWork.Tokens.GetWaitingTokensAsync(branchId, serviceTypeId, cancellationToken);
        var waitingCount = waitingTokens.Count;

        // Get active counters for this service type
        var activeCounters = await GetActiveCountersForServiceTypeAsync(serviceTypeId, cancellationToken);

        if (activeCounters == 0)
            return waitingCount * serviceType.AverageServiceTimeMinutes;

        // Estimated wait = (waiting tokens * avg service time) / active counters
        return (int)Math.Ceiling((double)(waitingCount * serviceType.AverageServiceTimeMinutes) / activeCounters);
    }

    public async Task<int> GetActiveCountersForServiceTypeAsync(Guid serviceTypeId, CancellationToken cancellationToken = default)
    {
        var counters = await _unitOfWork.Counters.FindAsync(
            c => c.Status == CounterStatus.Active &&
                 c.CounterServiceTypes.Any(cst => cst.ServiceTypeId == serviceTypeId),
            cancellationToken);

        return counters.Count;
    }

    public async Task RecalculateQueueEstimatesAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var waitingTokens = await _unitOfWork.Tokens.GetWaitingTokensAsync(branchId, null, cancellationToken);

        foreach (var token in waitingTokens)
        {
            var estimatedWait = await CalculateEstimatedWaitAsync(branchId, token.ServiceTypeId, cancellationToken);
            token.EstimatedWaitMinutes = estimatedWait;
            await _unitOfWork.Tokens.UpdateAsync(token, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
