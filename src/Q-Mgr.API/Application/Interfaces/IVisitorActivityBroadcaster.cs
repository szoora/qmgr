using QMgr.Application.DTOs;

namespace QMgr.Application.Interfaces;

/// <summary>
/// Pushes visitor activity to the admin-facing live board. Rides the same SignalR connection
/// and branch-group membership every admin page already opens for notifications (NotificationHub)
/// rather than standing up a second hub + a second client connection.
/// </summary>
public interface IVisitorActivityBroadcaster
{
    Task BroadcastAsync(Guid branchId, VisitorActivityKind kind, VisitorDto visitor);
}
