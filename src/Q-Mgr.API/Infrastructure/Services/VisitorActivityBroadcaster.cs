using Microsoft.AspNetCore.SignalR;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Hubs;

namespace QMgr.Infrastructure.Services;

public class VisitorActivityBroadcaster : IVisitorActivityBroadcaster
{
    private readonly IHubContext<NotificationHub> _hubContext;

    public VisitorActivityBroadcaster(IHubContext<NotificationHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task BroadcastAsync(Guid branchId, VisitorActivityKind kind, VisitorDto visitor)
    {
        var evt = new VisitorActivityEvent { Kind = kind, Visitor = visitor, OccurredAt = DateTime.UtcNow };
        await _hubContext.Clients.Group($"branch-{branchId}").SendAsync("VisitorActivity", evt);
    }
}
