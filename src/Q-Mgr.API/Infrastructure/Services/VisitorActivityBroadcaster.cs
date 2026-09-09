using Microsoft.AspNetCore.SignalR;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Hubs;

namespace QMgr.Infrastructure.Services;

public class VisitorActivityBroadcaster : IVisitorActivityBroadcaster
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<VisitorActivityBroadcaster> _logger;

    public VisitorActivityBroadcaster(IHubContext<NotificationHub> hubContext, ILogger<VisitorActivityBroadcaster> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Pushes to the live board — and swallows its own failures on purpose.
    ///
    /// Every call site is positioned AFTER the database write it announces has already committed
    /// (a check-in, a check-out, a flag). Letting a push failure bubble would turn "the live board
    /// missed one row" into "the check-in returned 500 to the front desk while the visitor is on
    /// site" — the worst possible trade, and the same shape of bug as the badge-token 500 that
    /// TryIssueVisitToken in VisitorsController exists to prevent. The board recovers on its next
    /// load; a phantom failure on a committed write does not.
    /// </summary>
    public async Task BroadcastAsync(Guid branchId, VisitorActivityKind kind, VisitorDto visitor)
    {
        try
        {
            var evt = new VisitorActivityEvent { Kind = kind, Visitor = visitor, OccurredAt = DateTime.UtcNow };
            await _hubContext.Clients.Group($"branch-{branchId}").SendAsync("VisitorActivity", evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to push {Kind} for visitor {VisitorId} to the live board on branch {BranchId}", kind, visitor.Id, branchId);
        }
    }
}
