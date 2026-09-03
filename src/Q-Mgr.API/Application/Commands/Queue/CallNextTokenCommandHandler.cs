using Mediator;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;
using QMgr.Domain.Interfaces;

namespace QMgr.Application.Commands.Queue;

/// <summary>
/// SECURITY: shared by every handler in this file. CountersController takes only a bare
/// counterId/tokenId — unlike TokensController, it never verifies the target belongs to the
/// caller's own organization before calling into these handlers (confirmed exploitable: a
/// tenant admin could call/complete/no-show another tenant's live queue tokens). Counter and
/// Token have no global EF query filter (branch-scoped entities are deliberately filtered
/// through their Branch, not directly), so every handler here must check explicitly.
/// </summary>
internal static class QueueOwnershipCheck
{
    public static async Task<bool> OwnsBranchAsync(
        IUnitOfWork unitOfWork,
        ITenantContextAccessor tenantContextAccessor,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        var tenantContext = tenantContextAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return false;

        return await unitOfWork.Branches.ExistsAsync(
            b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId,
            cancellationToken);
    }
}

public class CallNextTokenCommandHandler : IRequestHandler<CallNextTokenCommand, TokenDto?>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IWebhookService _webhookService;
    private readonly IQueueHubService _queueHubService;
    private readonly IQueueCustomerNotifier _customerNotifier;

    public CallNextTokenCommandHandler(
        IUnitOfWork unitOfWork,
        ITenantContextAccessor tenantContextAccessor,
        IWebhookService webhookService,
        IQueueHubService queueHubService,
        IQueueCustomerNotifier customerNotifier)
    {
        _unitOfWork = unitOfWork;
        _tenantContextAccessor = tenantContextAccessor;
        _webhookService = webhookService;
        _queueHubService = queueHubService;
        _customerNotifier = customerNotifier;
    }

    public async ValueTask<TokenDto?> Handle(CallNextTokenCommand request, CancellationToken cancellationToken)
    {
        Counter? counter = null;
        Token? token = null;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            counter = await _unitOfWork.Counters.GetByIdAsync(request.CounterId, ct)
                ?? throw new InvalidOperationException("Counter not found.");

            if (!await QueueOwnershipCheck.OwnsBranchAsync(_unitOfWork, _tenantContextAccessor, counter.BranchId, ct))
                throw new InvalidOperationException("Counter not found.");

            // Get next waiting token for this counter's service types
            token = await _unitOfWork.Tokens.GetNextWaitingTokenForCounterAsync(request.CounterId, ct);

            if (token == null)
                return;

            // Update token status
            token.Status = TokenStatus.Called;
            token.CounterId = request.CounterId;
            token.CalledAt = DateTime.UtcNow;
            token.ActualWaitMinutes = (int)(DateTime.UtcNow - token.CreatedAt).TotalMinutes;

            // Add history
            await _unitOfWork.Tokens.AddHistoryAsync(new TokenHistory
            {
                TokenId = token.Id,
                FromStatus = TokenStatus.Waiting,
                ToStatus = TokenStatus.Called,
                CounterId = request.CounterId,
                UserId = request.UserId
            }, ct);

            // Update counter's current token
            counter.CurrentTokenId = token.Id;
            counter.Status = CounterStatus.Active;

            await _unitOfWork.Tokens.UpdateAsync(token, ct);
            await _unitOfWork.Counters.UpdateAsync(counter, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);

        if (token == null)
            return null;

        // Notify clients via SignalR
        await _queueHubService.NotifyTokenCalledAsync(token, counter!, cancellationToken);

        // Trigger webhook
        await _webhookService.TriggerTokenCalledAsync(token, cancellationToken);

        // Tell the customer, and warn whoever just moved into the front of the queue behind
        // them. Strictly AFTER the transaction has committed: a call must never be rolled back
        // because an SMS gateway timed out. Neither call throws or blocks — see
        // IQueueCustomerNotifier.
        await _customerNotifier.NotifyCalledToCounterAsync(token.Id, counter!.Id);
        await _customerNotifier.NotifyApproachingTurnAsync(token.BranchId, token.ServiceTypeId);

        return MapToDto(token, counter!);
    }

    private static TokenDto MapToDto(Token token, Counter counter)
    {
        return new TokenDto
        {
            Id = token.Id,
            TokenNumber = token.TokenNumber,
            DisplayNumber = token.DisplayNumber,
            Status = token.Status,
            Priority = token.Priority,
            Source = token.Source,
            BranchId = token.BranchId,
            ServiceTypeId = token.ServiceTypeId,
            CounterId = token.CounterId,
            Customer = new CustomerDto
            {
                Id = token.CustomerId,
                Name = token.CustomerName,
                Phone = token.CustomerPhone,
                Email = token.CustomerEmail
            },
            Counter = new CounterDto
            {
                Id = counter.Id,
                CounterNumber = counter.CounterNumber,
                DisplayName = counter.DisplayName,
                Status = counter.Status
            },
            ActualWaitMinutes = token.ActualWaitMinutes,
            CreatedAt = token.CreatedAt,
            CalledAt = token.CalledAt
        };
    }
}

public class CompleteServiceCommandHandler : IRequestHandler<CompleteServiceCommand, TokenDto?>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IWebhookService _webhookService;
    private readonly IQueueHubService _queueHubService;

    public CompleteServiceCommandHandler(
        IUnitOfWork unitOfWork,
        ITenantContextAccessor tenantContextAccessor,
        IWebhookService webhookService,
        IQueueHubService queueHubService)
    {
        _unitOfWork = unitOfWork;
        _tenantContextAccessor = tenantContextAccessor;
        _webhookService = webhookService;
        _queueHubService = queueHubService;
    }

    public async ValueTask<TokenDto?> Handle(CompleteServiceCommand request, CancellationToken cancellationToken)
    {
        var token = await _unitOfWork.Tokens.GetByIdAsync(request.TokenId, cancellationToken)
            ?? throw new InvalidOperationException("Token not found.");

        if (!await QueueOwnershipCheck.OwnsBranchAsync(_unitOfWork, _tenantContextAccessor, token.BranchId, cancellationToken))
            throw new InvalidOperationException("Token not found.");

        var previousStatus = token.Status;
        token.Status = TokenStatus.Completed;
        token.ServiceCompletedAt = DateTime.UtcNow;
        token.Notes = request.Notes;

        if (token.ServiceStartedAt.HasValue)
        {
            token.ServiceDurationMinutes = (int)(DateTime.UtcNow - token.ServiceStartedAt.Value).TotalMinutes;
        }

        await _unitOfWork.Tokens.AddHistoryAsync(new TokenHistory
        {
            TokenId = token.Id,
            FromStatus = previousStatus,
            ToStatus = TokenStatus.Completed,
            CounterId = token.CounterId,
            UserId = request.UserId,
            Notes = request.Notes
        }, cancellationToken);

        // Clear counter's current token
        if (token.CounterId.HasValue)
        {
            var counter = await _unitOfWork.Counters.GetByIdAsync(token.CounterId.Value, cancellationToken);
            if (counter != null)
            {
                counter.CurrentTokenId = null;
                await _unitOfWork.Counters.UpdateAsync(counter, cancellationToken);
            }
        }

        await _unitOfWork.Tokens.UpdateAsync(token, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Notify
        await _queueHubService.NotifyTokenCompletedAsync(token, cancellationToken);
        await _webhookService.TriggerTokenCompletedAsync(token, cancellationToken);

        return new TokenDto
        {
            Id = token.Id,
            TokenNumber = token.TokenNumber,
            DisplayNumber = token.DisplayNumber,
            Status = token.Status,
            ServiceDurationMinutes = token.ServiceDurationMinutes,
            ServiceCompletedAt = token.ServiceCompletedAt
        };
    }
}

/// <summary>
/// Calls a caller-specified token (not just "next in queue") to a counter. This handler
/// previously did not exist at all — CountersController.CallSpecificToken called
/// IMediator.Send(new CallSpecificTokenCommand {...}) with no registered handler, which threw
/// "No handler registered for message type" (HTTP 500) on every call. Reachable from the real
/// staff UI (CounterTerminal.razor's per-token "Call" button), so this was a live, user-facing
/// bug, not dead code.
/// </summary>
public class CallSpecificTokenCommandHandler : IRequestHandler<CallSpecificTokenCommand, TokenDto?>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IWebhookService _webhookService;
    private readonly IQueueHubService _queueHubService;
    private readonly IQueueCustomerNotifier _customerNotifier;

    public CallSpecificTokenCommandHandler(
        IUnitOfWork unitOfWork,
        ITenantContextAccessor tenantContextAccessor,
        IWebhookService webhookService,
        IQueueHubService queueHubService,
        IQueueCustomerNotifier customerNotifier)
    {
        _unitOfWork = unitOfWork;
        _tenantContextAccessor = tenantContextAccessor;
        _webhookService = webhookService;
        _queueHubService = queueHubService;
        _customerNotifier = customerNotifier;
    }

    public async ValueTask<TokenDto?> Handle(CallSpecificTokenCommand request, CancellationToken cancellationToken)
    {
        Counter? counter = null;
        Token? token = null;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            counter = await _unitOfWork.Counters.GetByIdAsync(request.CounterId, ct);
            if (counter == null) return;

            if (!await QueueOwnershipCheck.OwnsBranchAsync(_unitOfWork, _tenantContextAccessor, counter.BranchId, ct))
            {
                counter = null;
                return;
            }

            var candidate = await _unitOfWork.Tokens.GetByIdAsync(request.TokenId, ct);
            // Must be the same branch as the counter and still waiting — can't call an
            // already-called/completed token, or one from a different branch's queue.
            if (candidate == null || candidate.BranchId != counter.BranchId || candidate.Status != TokenStatus.Waiting)
                return;

            token = candidate;
            token.Status = TokenStatus.Called;
            token.CounterId = request.CounterId;
            token.CalledAt = DateTime.UtcNow;
            token.ActualWaitMinutes = (int)(DateTime.UtcNow - token.CreatedAt).TotalMinutes;

            await _unitOfWork.Tokens.AddHistoryAsync(new TokenHistory
            {
                TokenId = token.Id,
                FromStatus = TokenStatus.Waiting,
                ToStatus = TokenStatus.Called,
                CounterId = request.CounterId,
                UserId = request.UserId
            }, ct);

            counter.CurrentTokenId = token.Id;
            counter.Status = CounterStatus.Active;

            await _unitOfWork.Tokens.UpdateAsync(token, ct);
            await _unitOfWork.Counters.UpdateAsync(counter, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);

        if (token == null || counter == null)
            return null;

        await _queueHubService.NotifyTokenCalledAsync(token, counter, cancellationToken);
        await _webhookService.TriggerTokenCalledAsync(token, cancellationToken);

        // Same post-commit customer notification as CallNextTokenCommandHandler — calling a
        // specific token is the same event from the customer's point of view.
        await _customerNotifier.NotifyCalledToCounterAsync(token.Id, counter.Id);
        await _customerNotifier.NotifyApproachingTurnAsync(token.BranchId, token.ServiceTypeId);

        return new TokenDto
        {
            Id = token.Id,
            TokenNumber = token.TokenNumber,
            DisplayNumber = token.DisplayNumber,
            Status = token.Status,
            Priority = token.Priority,
            Source = token.Source,
            BranchId = token.BranchId,
            ServiceTypeId = token.ServiceTypeId,
            CounterId = token.CounterId,
            Customer = new CustomerDto
            {
                Id = token.CustomerId,
                Name = token.CustomerName,
                Phone = token.CustomerPhone,
                Email = token.CustomerEmail
            },
            Counter = new CounterDto
            {
                Id = counter.Id,
                CounterNumber = counter.CounterNumber,
                DisplayName = counter.DisplayName,
                Status = counter.Status
            },
            ActualWaitMinutes = token.ActualWaitMinutes,
            CreatedAt = token.CreatedAt,
            CalledAt = token.CalledAt
        };
    }
}

/// <summary>
/// Marks a token as no-show. Previously had no registered handler at all — same "500 on every
/// call" bug as CallSpecificTokenCommand, also reachable from the real staff UI
/// (CounterTerminal.razor's "Mark No-Show" button + confirmation dialog).
/// </summary>
public class MarkNoShowCommandHandler : IRequestHandler<MarkNoShowCommand, bool>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IQueueHubService _queueHubService;

    public MarkNoShowCommandHandler(
        IUnitOfWork unitOfWork,
        ITenantContextAccessor tenantContextAccessor,
        IQueueHubService queueHubService)
    {
        _unitOfWork = unitOfWork;
        _tenantContextAccessor = tenantContextAccessor;
        _queueHubService = queueHubService;
    }

    public async ValueTask<bool> Handle(MarkNoShowCommand request, CancellationToken cancellationToken)
    {
        var success = false;
        Token? token = null;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var candidate = await _unitOfWork.Tokens.GetByIdAsync(request.TokenId, ct);
            if (candidate == null) return;

            if (!await QueueOwnershipCheck.OwnsBranchAsync(_unitOfWork, _tenantContextAccessor, candidate.BranchId, ct))
                return;

            token = candidate;
            var previousStatus = token.Status;
            token.Status = TokenStatus.NoShow;

            await _unitOfWork.Tokens.AddHistoryAsync(new TokenHistory
            {
                TokenId = token.Id,
                FromStatus = previousStatus,
                ToStatus = TokenStatus.NoShow,
                CounterId = token.CounterId,
                UserId = request.UserId
            }, ct);

            if (token.CounterId.HasValue)
            {
                var counter = await _unitOfWork.Counters.GetByIdAsync(token.CounterId.Value, ct);
                if (counter != null && counter.CurrentTokenId == token.Id)
                {
                    counter.CurrentTokenId = null;
                    await _unitOfWork.Counters.UpdateAsync(counter, ct);
                }
            }

            await _unitOfWork.Tokens.UpdateAsync(token, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            success = true;
        }, cancellationToken);

        if (success && token != null)
        {
            await _queueHubService.NotifyQueueUpdatedAsync(token.BranchId, cancellationToken);
        }

        return success;
    }
}

/// <summary>
/// Transfers a token currently at one counter directly to another counter. Previously had no
/// registered handler at all (500 on every call), but — unlike TransferTokenCommand's earlier
/// documented status — the controller endpoint (CountersController.TransferToken) and the Web
/// client (CounterTerminal.razor's "Transfer" button) were always fully wired; only this
/// handler was missing. The Web button still shows "coming soon" pending a destination-counter
/// picker UI (a separate, larger UX task), so this closes the backend gap without yet being
/// reachable from the button — matching the same "scaffolding complete, one piece missing"
/// shape as CallSpecificToken/MarkNoShow in Phase 11 and CancelToken in Phase 14.
///
/// SEMANTICS (a product decision this session previously deferred — documented here so it's
/// easy to revise if it doesn't match actual intent):
/// - Only a token currently AT a counter (Called or Serving) can be transferred — matches the
///   Web button's own disabled-state logic (only enabled when a counter has a `currentToken`).
///   A waiting (not yet called) token isn't "transferred", it's just called normally.
/// - Same branch only: the destination counter must belong to the same branch as the token.
///   A customer physically queued at one branch location cannot be moved to a counter at a
///   different branch — this is a physical/logical constraint, not a preference.
/// - No auto-serve: transfer always lands the token in `Called` status at the destination
///   (mirroring CallSpecificToken), never `Serving` — the destination counter's staff must
///   explicitly begin service, the same as calling any other customer. This avoids assuming
///   the new counter is mid-conversation with a customer it never interacted with.
/// - Destination must be `Active`: transferring into a Closed/OnBreak/Inactive counter would
///   silently strand the customer with no one to serve them.
/// - `ServiceStartedAt` is cleared and `ActualWaitMinutes` is recomputed from the token's
///   original `CreatedAt` (not reset) — the customer's total wait time is preserved for
///   fairness/reporting; only the in-progress service session at the old counter is ended.
/// </summary>
public class TransferTokenCommandHandler : IRequestHandler<TransferTokenCommand, TokenDto?>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IWebhookService _webhookService;
    private readonly IQueueHubService _queueHubService;

    public TransferTokenCommandHandler(
        IUnitOfWork unitOfWork,
        ITenantContextAccessor tenantContextAccessor,
        IWebhookService webhookService,
        IQueueHubService queueHubService)
    {
        _unitOfWork = unitOfWork;
        _tenantContextAccessor = tenantContextAccessor;
        _webhookService = webhookService;
        _queueHubService = queueHubService;
    }

    public async ValueTask<TokenDto?> Handle(TransferTokenCommand request, CancellationToken cancellationToken)
    {
        Token? token = null;
        Counter? destCounter = null;
        Counter? oldCounter = null;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            destCounter = await _unitOfWork.Counters.GetByIdAsync(request.ToCounterId, ct);
            if (destCounter == null) return;

            if (!await QueueOwnershipCheck.OwnsBranchAsync(_unitOfWork, _tenantContextAccessor, destCounter.BranchId, ct))
            {
                destCounter = null;
                return;
            }

            var candidate = await _unitOfWork.Tokens.GetByIdAsync(request.TokenId, ct);
            if (candidate == null
                || (candidate.Status != TokenStatus.Called && candidate.Status != TokenStatus.Serving)
                || candidate.BranchId != destCounter.BranchId
                || candidate.CounterId == destCounter.Id
                || destCounter.Status != CounterStatus.Active)
            {
                destCounter = null;
                return;
            }

            token = candidate;
            var previousStatus = token.Status;
            var previousCounterId = token.CounterId;

            if (previousCounterId.HasValue)
            {
                oldCounter = await _unitOfWork.Counters.GetByIdAsync(previousCounterId.Value, ct);
                if (oldCounter != null && oldCounter.CurrentTokenId == token.Id)
                {
                    oldCounter.CurrentTokenId = null;
                    await _unitOfWork.Counters.UpdateAsync(oldCounter, ct);
                }
            }

            token.Status = TokenStatus.Called;
            token.CounterId = destCounter.Id;
            token.CalledAt = DateTime.UtcNow;
            token.ServiceStartedAt = null;
            token.ActualWaitMinutes = (int)(DateTime.UtcNow - token.CreatedAt).TotalMinutes;

            await _unitOfWork.Tokens.AddHistoryAsync(new TokenHistory
            {
                TokenId = token.Id,
                FromStatus = previousStatus,
                ToStatus = TokenStatus.Called,
                CounterId = destCounter.Id,
                UserId = request.UserId,
                Notes = string.IsNullOrWhiteSpace(request.Reason) ? "Transferred to another counter" : request.Reason
            }, ct);

            destCounter.CurrentTokenId = token.Id;
            destCounter.Status = CounterStatus.Active;

            await _unitOfWork.Tokens.UpdateAsync(token, ct);
            await _unitOfWork.Counters.UpdateAsync(destCounter, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);

        if (token == null || destCounter == null)
            return null;

        await _queueHubService.NotifyTokenCalledAsync(token, destCounter, cancellationToken);
        if (oldCounter != null)
            await _queueHubService.NotifyCounterStatusChangedAsync(oldCounter, cancellationToken);
        await _webhookService.TriggerTokenCalledAsync(token, cancellationToken);

        return new TokenDto
        {
            Id = token.Id,
            TokenNumber = token.TokenNumber,
            DisplayNumber = token.DisplayNumber,
            Status = token.Status,
            Priority = token.Priority,
            Source = token.Source,
            BranchId = token.BranchId,
            ServiceTypeId = token.ServiceTypeId,
            CounterId = token.CounterId,
            Customer = new CustomerDto
            {
                Id = token.CustomerId,
                Name = token.CustomerName,
                Phone = token.CustomerPhone,
                Email = token.CustomerEmail
            },
            Counter = new CounterDto
            {
                Id = destCounter.Id,
                CounterNumber = destCounter.CounterNumber,
                DisplayName = destCounter.DisplayName,
                Status = destCounter.Status
            },
            ActualWaitMinutes = token.ActualWaitMinutes,
            CreatedAt = token.CreatedAt,
            CalledAt = token.CalledAt
        };
    }
}

public class CancelTokenCommandHandler : IRequestHandler<CancelTokenCommand, bool>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IQueueHubService _queueHubService;

    public CancelTokenCommandHandler(
        IUnitOfWork unitOfWork,
        IQueueHubService queueHubService)
    {
        _unitOfWork = unitOfWork;
        _queueHubService = queueHubService;
    }

    public async ValueTask<bool> Handle(CancelTokenCommand request, CancellationToken cancellationToken)
    {
        // SECURITY: VerifyBranchOwnership(branchId) in TokensController.CancelToken only proves
        // the caller owns *a* branch in their org — it does not prove this specific TokenId
        // belongs to that branch. Without the BranchId check below, any caller could cancel
        // another tenant's token by guessing/enumerating a tokenId.
        var success = false;
        Token? token = null;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var candidate = await _unitOfWork.Tokens.GetByIdAsync(request.TokenId, ct);
            if (candidate == null || candidate.BranchId != request.BranchId) return;

            // Can't cancel a token that's already reached a terminal state.
            if (candidate.Status is TokenStatus.Completed or TokenStatus.Cancelled
                or TokenStatus.NoShow or TokenStatus.Transferred)
                return;

            token = candidate;
            var previousStatus = token.Status;
            token.Status = TokenStatus.Cancelled;

            var noteParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.CancelledBy)) noteParts.Add($"Cancelled by: {request.CancelledBy}");
            if (!string.IsNullOrWhiteSpace(request.Reason)) noteParts.Add(request.Reason);

            await _unitOfWork.Tokens.AddHistoryAsync(new TokenHistory
            {
                TokenId = token.Id,
                FromStatus = previousStatus,
                ToStatus = TokenStatus.Cancelled,
                CounterId = token.CounterId,
                Notes = noteParts.Count > 0 ? string.Join(" — ", noteParts) : null
            }, ct);

            if (token.CounterId.HasValue)
            {
                var counter = await _unitOfWork.Counters.GetByIdAsync(token.CounterId.Value, ct);
                if (counter != null && counter.CurrentTokenId == token.Id)
                {
                    counter.CurrentTokenId = null;
                    await _unitOfWork.Counters.UpdateAsync(counter, ct);
                }
            }

            await _unitOfWork.Tokens.UpdateAsync(token, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            success = true;
        }, cancellationToken);

        if (success && token != null)
        {
            await _queueHubService.NotifyQueueUpdatedAsync(token.BranchId, cancellationToken);
        }

        return success;
    }
}
