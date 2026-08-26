using Microsoft.AspNetCore.SignalR;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Hubs;

namespace QMgr.Infrastructure.Services;

public class RosterImportBroadcaster : IRosterImportBroadcaster
{
    private readonly IHubContext<NotificationHub> _hubContext;

    public RosterImportBroadcaster(IHubContext<NotificationHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task BroadcastAsync(RosterImportProgressEvent progress)
    {
        await _hubContext.Clients.Group($"branch-{progress.BranchId}").SendAsync("RosterImportProgress", progress);
    }
}
