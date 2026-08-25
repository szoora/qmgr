using Mediator;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Application.Commands.Queue;

public record CreateTokenCommand : IRequest<TokenDto>
{
    public Guid BranchId { get; init; }
    public string ServiceTypeCode { get; init; } = string.Empty;

    public CustomerDto? Customer { get; init; }
    public TokenSource Source { get; init; } = TokenSource.Kiosk;
    public TokenPriority Priority { get; init; } = TokenPriority.Normal;

    public string? ExternalReference { get; init; }
    public string? ExternalSystem { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public DateTime? EstimatedArrival { get; init; }
}

public record CreateTokenBulkCommand : IRequest<List<TokenDto>>
{
    public Guid BranchId { get; init; }
    public List<CreateTokenCommand> Tokens { get; init; } = new();
}
