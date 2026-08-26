using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;
using QMgr.Domain.Interfaces;

namespace QMgr.Infrastructure.Data.Repositories;

public class TokenRepository : Repository<Token>, ITokenRepository
{
    public TokenRepository(QMgrDbContext context) : base(context)
    {
    }

    public async Task<Token?> GetByDisplayNumberAsync(Guid branchId, string displayNumber, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(t => t.ServiceType)
            .Include(t => t.Counter)
            .FirstOrDefaultAsync(t => t.BranchId == branchId && t.DisplayNumber == displayNumber, cancellationToken);
    }

    public async Task<Token?> GetByExternalReferenceAsync(Guid branchId, string externalSystem, string externalReference, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(t => t.ServiceType)
            .Include(t => t.Counter)
            .FirstOrDefaultAsync(t =>
                t.BranchId == branchId &&
                t.ExternalSystem == externalSystem &&
                t.ExternalReference == externalReference,
                cancellationToken);
    }

    public async Task<IReadOnlyList<Token>> GetWaitingTokensAsync(Guid branchId, Guid? serviceTypeId = null, CancellationToken cancellationToken = default)
    {
        var query = _dbSet
            .Include(t => t.ServiceType)
            .Where(t => t.BranchId == branchId && t.Status == TokenStatus.Waiting);

        if (serviceTypeId.HasValue)
        {
            query = query.Where(t => t.ServiceTypeId == serviceTypeId.Value);
        }

        return await query
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Token>> GetTokensByStatusAsync(Guid branchId, TokenStatus status, DateTime? fromDate = null, CancellationToken cancellationToken = default)
    {
        var query = _dbSet
            .Include(t => t.ServiceType)
            .Include(t => t.Counter)
            .Where(t => t.BranchId == branchId && t.Status == status);

        if (fromDate.HasValue)
        {
            query = query.Where(t => t.CreatedAt >= fromDate.Value);
        }

        return await query
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Token>> GetTokensByCustomerIdAsync(Guid branchId, string customerId, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(t => t.ServiceType)
            .Include(t => t.Counter)
            .Where(t => t.BranchId == branchId && t.CustomerId == customerId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetQueuePositionAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var token = await _dbSet.FindAsync(new object[] { tokenId }, cancellationToken);
        if (token == null || token.Status != TokenStatus.Waiting)
            return 0;

        return await _dbSet
            .CountAsync(t =>
                t.BranchId == token.BranchId &&
                t.ServiceTypeId == token.ServiceTypeId &&
                t.Status == TokenStatus.Waiting &&
                (t.Priority < token.Priority || (t.Priority == token.Priority && t.CreatedAt < token.CreatedAt)),
                cancellationToken) + 1;
    }

    public async Task<int> GetNextTokenNumberAsync(Guid branchId, Guid serviceTypeId, CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.Date;

        // CONCURRENCY: without serialization, two simultaneous token creations for the same
        // branch/service/day (e.g. two kiosks issuing tickets at once) can both read the same
        // "last token" under READ COMMITTED and compute the same next number — a real,
        // previously-unguarded duplicate-ticket-number race (idx_tokens_display_number is not a
        // unique index, see docs/TASK_TRACKER.md Phase 4 audit). pg_advisory_xact_lock takes a
        // transaction-scoped lock keyed to this exact branch+service+day combination, so a second
        // concurrent caller blocks here until the first one's transaction commits or rolls back —
        // the caller (CreateTokenCommandHandler) must run this inside a real transaction for the
        // lock to serialize anything; it releases automatically on commit/rollback.
        var lockKey = $"{branchId}:{serviceTypeId}:{today:yyyyMMdd}";
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)",
            cancellationToken);

        var lastToken = await _dbSet
            .Where(t => t.BranchId == branchId &&
                       t.ServiceTypeId == serviceTypeId &&
                       t.CreatedAt >= today)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastToken == null)
            return 1;

        if (int.TryParse(lastToken.TokenNumber, out int lastNumber))
            return lastNumber + 1;

        return 1;
    }

    public async Task<Token?> GetNextWaitingTokenForCounterAsync(Guid counterId, CancellationToken cancellationToken = default)
    {
        var counter = await _context.Counters
            .Include(c => c.CounterServiceTypes)
            .FirstOrDefaultAsync(c => c.Id == counterId, cancellationToken);

        if (counter == null)
            return null;

        var serviceTypeIds = counter.CounterServiceTypes.Select(cst => cst.ServiceTypeId).ToArray();

        // CONCURRENCY: this is called from inside CallNextTokenCommandHandler's transaction.
        // Under Postgres's default READ COMMITTED isolation, a plain SELECT here has no locking
        // effect at all — two counters sharing an overlapping service type that both call "next"
        // at the same moment can each read the SAME Waiting token before either transaction
        // commits, and both go on to successfully assign it (double-assignment). "FOR UPDATE"
        // takes a row lock on the chosen token for the rest of this transaction; "SKIP LOCKED"
        // means a concurrent caller racing for the same row doesn't block on the lock, it simply
        // skips that (already-claimed) row and picks the next candidate instead — exactly the
        // "each counter gets a different waiting token" semantics this handler needs.
        // BUG FIX: this raw SQL previously read "FROM tokens" unqualified. EF's own generated SQL
        // is always schema-qualified (HasDefaultSchema("qmgr") in QMgrDbContext handles that
        // automatically), but raw FromSqlInterpolated bypasses that entirely and is resolved via
        // Postgres's connection-level search_path instead — which for this DB is the server
        // default ("$user", public), not qmgr. That made every single "Call Next" request 500
        // with "relation \"tokens\" does not exist" — found live, not by inspection, while
        // e2e-testing an unrelated flow. The fix is the same schema-qualification EF already does
        // for you everywhere else; write it explicitly since raw SQL doesn't get it for free.
        var tokenId = await _dbSet
            .FromSqlInterpolated($@"
                SELECT * FROM qmgr.tokens
                WHERE ""BranchId"" = {counter.BranchId}
                  AND ""ServiceTypeId"" = ANY({serviceTypeIds})
                  AND ""Status"" = {(int)TokenStatus.Waiting}
                ORDER BY ""Priority"" DESC, ""CreatedAt"" ASC
                LIMIT 1
                FOR UPDATE SKIP LOCKED")
            .Select(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (tokenId == Guid.Empty)
            return null;

        return await _dbSet
            .Include(t => t.ServiceType)
            .FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
    }

    public Task AddHistoryAsync(TokenHistory history, CancellationToken cancellationToken = default)
    {
        _context.TokenHistories.Add(history);
        return Task.CompletedTask;
    }
}
