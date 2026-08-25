using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Interfaces;

public interface ITokenRepository : IRepository<Token>
{
    Task<Token?> GetByDisplayNumberAsync(Guid branchId, string displayNumber, CancellationToken cancellationToken = default);
    Task<Token?> GetByExternalReferenceAsync(Guid branchId, string externalSystem, string externalReference, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Token>> GetWaitingTokensAsync(Guid branchId, Guid? serviceTypeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Token>> GetTokensByStatusAsync(Guid branchId, TokenStatus status, DateTime? fromDate = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Token>> GetTokensByCustomerIdAsync(Guid branchId, string customerId, CancellationToken cancellationToken = default);
    Task<int> GetQueuePositionAsync(Guid tokenId, CancellationToken cancellationToken = default);
    Task<int> GetNextTokenNumberAsync(Guid branchId, Guid serviceTypeId, CancellationToken cancellationToken = default);
    Task<Token?> GetNextWaitingTokenForCounterAsync(Guid counterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a TokenHistory row directly to its DbSet. Must be used instead of
    /// token.History.Add(...) — the latter is a navigation-collection add on an
    /// already-tracked parent, and since TokenHistory.Id is set client-side
    /// (BaseEntity.Id = Guid.NewGuid()) before it's ever tracked, EF Core's default
    /// change detection sees a non-default key and marks it Modified instead of
    /// Added, generating an UPDATE that matches 0 rows.
    /// </summary>
    Task AddHistoryAsync(TokenHistory history, CancellationToken cancellationToken = default);
}
