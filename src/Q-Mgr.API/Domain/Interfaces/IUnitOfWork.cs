namespace QMgr.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    ITokenRepository Tokens { get; }
    IRepository<Entities.Queue.Counter> Counters { get; }
    IRepository<Entities.Queue.ServiceType> ServiceTypes { get; }
    IRepository<Entities.Organization.Organization> Organizations { get; }
    IRepository<Entities.Organization.Branch> Branches { get; }
    IRepository<Entities.Content.MediaContent> MediaContents { get; }
    IRepository<Entities.Content.Playlist> Playlists { get; }
    IRepository<Entities.Content.PlaylistItem> PlaylistItems { get; }
    IRepository<Entities.Content.Display> Displays { get; }
    IRepository<Entities.Content.DisplayZone> DisplayZones { get; }
    IRepository<Entities.Identity.User> Users { get; }
    IRepository<Entities.Integration.ApiClient> ApiClients { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside a database transaction, committing on success
    /// and rolling back on any exception. Always use this instead of manually pairing
    /// Begin/Commit/RollbackTransactionAsync — the DbContext is configured with
    /// EnableRetryOnFailure, and EF Core's retrying execution strategy does not support
    /// user-managed transactions opened outside of CreateExecutionStrategy().ExecuteAsync():
    /// doing so throws InvalidOperationException on the very first call, unconditionally, not
    /// just under transient failure. This wraps that correctly so callers don't have to.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
}
