using Microsoft.EntityFrameworkCore.Storage;
using QMgr.Domain.Entities.Content;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Integration;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Interfaces;

namespace QMgr.Infrastructure.Data.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly QMgrDbContext _context;

    private ITokenRepository? _tokens;
    private IRepository<Counter>? _counters;
    private IRepository<ServiceType>? _serviceTypes;
    private IRepository<Organization>? _organizations;
    private IRepository<Branch>? _branches;
    private IRepository<MediaContent>? _mediaContents;
    private IRepository<Playlist>? _playlists;
    private IRepository<PlaylistItem>? _playlistItems;
    private IRepository<Display>? _displays;
    private IRepository<DisplayZone>? _displayZones;
    private IRepository<User>? _users;
    private IRepository<ApiClient>? _apiClients;

    public UnitOfWork(QMgrDbContext context)
    {
        _context = context;
    }

    public ITokenRepository Tokens => _tokens ??= new TokenRepository(_context);
    public IRepository<Counter> Counters => _counters ??= new Repository<Counter>(_context);
    public IRepository<ServiceType> ServiceTypes => _serviceTypes ??= new Repository<ServiceType>(_context);
    public IRepository<Organization> Organizations => _organizations ??= new Repository<Organization>(_context);
    public IRepository<Branch> Branches => _branches ??= new Repository<Branch>(_context);
    public IRepository<MediaContent> MediaContents => _mediaContents ??= new Repository<MediaContent>(_context);
    public IRepository<Playlist> Playlists => _playlists ??= new Repository<Playlist>(_context);
    public IRepository<PlaylistItem> PlaylistItems => _playlistItems ??= new Repository<PlaylistItem>(_context);
    public IRepository<Display> Displays => _displays ??= new Repository<Display>(_context);
    public IRepository<DisplayZone> DisplayZones => _displayZones ??= new Repository<DisplayZone>(_context);
    public IRepository<User> Users => _users ??= new Repository<User>(_context);
    public IRepository<ApiClient> ApiClients => _apiClients ??= new Repository<ApiClient>(_context);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        // EnableRetryOnFailure means EF Core owns retries; a manually opened transaction that
        // isn't run through this execution strategy throws immediately (not just on transient
        // failure) — the whole begin/work/commit unit has to be retriable together.
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync<object?, bool>(
            state: null,
            operation: async (_, _, ct) =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(ct);
                try
                {
                    await operation(ct);
                    await transaction.CommitAsync(ct);
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            },
            verifySucceeded: null,
            cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
