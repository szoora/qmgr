namespace QMgr.Application.DTOs;

/// <summary>
/// Shapes served by the anonymous, customer-facing half of QueueController
/// (<c>api/v1/branches/{branchId}/queue/...</c>): the unattended lobby kiosk, the phone
/// "join the queue" page and the ticket-status page.
///
/// These deliberately do NOT reuse <see cref="TokenDto"/>/<see cref="ServiceTypeDto"/>. Those
/// carry customer name/phone/email and internal ids, and every one of these endpoints is
/// reachable with no credentials at all — several of them at a guessable URL. Narrow,
/// purpose-built records make "what a stranger can read" a property of the type rather than
/// something each action has to remember to strip.
///
/// Lives in Q-Mgr.Shared (per the SSoT convention in CLAUDE.md) so Q-Mgr.API and Q-Mgr.Web
/// share one definition instead of each keeping a drift-prone copy.
/// </summary>
public record PublicServiceTypeDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? IconUrl { get; init; }
    public string? Color { get; init; }
    public int WaitingCount { get; init; }
    public int EstimatedWaitMinutes { get; init; }
}

/// <summary>
/// Body of <c>POST api/v1/branches/{branchId}/queue/tokens</c>. Exactly one of
/// <see cref="ServiceTypeId"/> / <see cref="ServiceTypeCode"/> is required; the free-text
/// fields are optional and are length-capped server-side (this is an unauthenticated write).
/// </summary>
public record PublicJoinQueueRequest
{
    public Guid? ServiceTypeId { get; init; }
    public string? ServiceTypeCode { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public string? CustomerEmail { get; init; }
}

/// <summary>The ticket just issued, echoed back to whoever issued it.</summary>
public record PublicTicketDto
{
    public Guid TokenId { get; init; }
    public string DisplayNumber { get; init; } = string.Empty;
    public Guid ServiceTypeId { get; init; }
    public string ServiceTypeName { get; init; } = string.Empty;
    public int Position { get; init; }
    public int EstimatedWaitMinutes { get; init; }
    public DateTime IssuedAt { get; init; }
}

/// <summary>
/// Live state of one ticket, served at a URL that is trivially guessable
/// (<c>/queue/tokens/A007</c>). Nothing here identifies the customer — no name, phone, email,
/// notes or external reference — and it must stay that way; the ticket number, the service and
/// the counter are all already on the public display board in the lobby.
/// </summary>
public record PublicTicketStatusDto
{
    public string DisplayNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ServiceTypeName { get; init; } = string.Empty;
    /// <summary>1-based place in the waiting line; 0 once the ticket is no longer waiting.</summary>
    public int Position { get; init; }
    public int PeopleAhead { get; init; }
    public int EstimatedWaitMinutes { get; init; }
    /// <summary>Set only once the ticket has been called to a counter.</summary>
    public string? CounterNumber { get; init; }
    public DateTime IssuedAt { get; init; }
    public DateTime ServerTime { get; init; }
}
