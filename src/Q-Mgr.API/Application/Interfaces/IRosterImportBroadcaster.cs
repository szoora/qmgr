using QMgr.Application.DTOs;

namespace QMgr.Application.Interfaces;

/// <summary>
/// Pushes roster-import job progress to the admin UI as the background job processes rows.
/// Rides the same NotificationHub branch-group connection VisitorActivityBroadcaster uses —
/// no second hub, and the same branch-ownership check already guards who can receive it.
/// </summary>
public interface IRosterImportBroadcaster
{
    Task BroadcastAsync(RosterImportProgressEvent progress);
}
