using Mediator;
using QMgr.Application.DTOs;

namespace QMgr.Application.Queries.Queue;

public record GetQueueStatusQuery : IRequest<QueueStatusDto>
{
    public Guid BranchId { get; init; }
}

public record GetTokenQuery : IRequest<TokenDto?>
{
    public Guid TokenId { get; init; }
    public Guid BranchId { get; init; }
}

public record GetTokenByExternalReferenceQuery : IRequest<TokenDto?>
{
    public Guid BranchId { get; init; }
    public string ExternalSystem { get; init; } = string.Empty;
    public string ExternalReference { get; init; } = string.Empty;
}

public record GetTokensByCustomerQuery : IRequest<List<TokenDto>>
{
    public Guid BranchId { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public bool ActiveOnly { get; init; } = true;
}

public record GetWaitingTokensQuery : IRequest<List<TokenDto>>
{
    public Guid BranchId { get; init; }
    public Guid? ServiceTypeId { get; init; }
    public int? Limit { get; init; }
}

public record GetCounterTokensQuery : IRequest<List<TokenDto>>
{
    public Guid CounterId { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
}
