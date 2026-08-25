using Mediator;
using QMgr.Application.DTOs;

namespace QMgr.Application.Commands.Queue;

public record CallNextTokenCommand : IRequest<TokenDto?>
{
    public Guid CounterId { get; init; }
    public Guid UserId { get; init; }
}

public record CallSpecificTokenCommand : IRequest<TokenDto?>
{
    public Guid TokenId { get; init; }
    public Guid CounterId { get; init; }
    public Guid UserId { get; init; }
}

public record CompleteServiceCommand : IRequest<TokenDto?>
{
    public Guid TokenId { get; init; }
    public Guid UserId { get; init; }
    public string? Notes { get; init; }
}

public record CancelTokenCommand : IRequest<bool>
{
    public Guid TokenId { get; init; }
    public Guid BranchId { get; init; }
    public string? Reason { get; init; }
    public string? CancelledBy { get; init; }
}

public record TransferTokenCommand : IRequest<TokenDto?>
{
    public Guid TokenId { get; init; }
    public Guid ToCounterId { get; init; }
    public Guid UserId { get; init; }
    public string? Reason { get; init; }
}

public record MarkNoShowCommand : IRequest<bool>
{
    public Guid TokenId { get; init; }
    public Guid UserId { get; init; }
}
