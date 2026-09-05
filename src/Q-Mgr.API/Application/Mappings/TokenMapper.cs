using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Queue;

namespace QMgr.Application.Mappings;

/// <summary>
/// The one place a <see cref="Token"/> becomes a <see cref="TokenDto"/>.
/// </summary>
/// <remarks>
/// <para>
/// There were eleven hand-written copies of this mapping across five files, and they had already
/// drifted: <b>not one of them set <c>Notes</c></b>, so a staff member's service-completion note —
/// written by <c>CompleteServiceCommandHandler</c> — went into the database and could never be read
/// back by anything. Two of the copies also returned a near-empty DTO with <c>BranchId</c> left at
/// <c>Guid.Empty</c>. Nobody wrote a bug; eleven copies of one mapping is the bug, and this project
/// has no auto-mapper to catch it (Mapster was removed in 2026-08 as unused).
/// </para>
/// <para>
/// The optional parameters exist because the call sites genuinely know different things: the
/// call-next handlers have the counter in hand, the create handler has the service type and a
/// freshly computed position, and the status queries compute a position per row. Anything not
/// passed is simply left off the DTO, exactly as the copies did.
/// </para>
/// <para>
/// <b>Two sites deliberately do not use this</b> — <c>BranchesController</c>'s counter listings
/// build their <c>CurrentToken</c> inside an EF <c>Select</c> that is translated to SQL, where a
/// method call cannot go and <c>JsonSerializer.Deserialize</c> has no translation either. They stay
/// inline and stay narrow on purpose: a counters list has no business carrying customer contact
/// details or integration metadata. That difference is now a decision with a comment on it rather
/// than an accident.
/// </para>
/// </remarks>
public static class TokenMapper
{
    /// <summary>
    /// Maps a materialised token. Pass whatever related data the caller already has; everything
    /// optional is omitted from the DTO when absent rather than guessed at.
    /// </summary>
    /// <param name="token">The token entity. Must be materialised — this is an in-memory mapper.</param>
    /// <param name="counter">The counter the token is at, when the caller has it loaded.</param>
    /// <param name="serviceType">The token's service type, when the caller has it loaded.</param>
    /// <param name="positionInQueue">Place in the waiting list, when the caller has computed it.</param>
    /// <param name="estimatedWaitMinutes">
    /// Overrides <c>Token.EstimatedWaitMinutes</c>. The create path computes a fresher figure than
    /// the column holds at that moment, which is why this is a parameter and not just the column.
    /// </param>
    /// <param name="customer">
    /// Overrides the customer built from the token's own columns. Only the create path needs this,
    /// where the request's customer object is the same data before a round trip.
    /// </param>
    public static TokenDto ToDto(
        Token token,
        Counter? counter = null,
        ServiceType? serviceType = null,
        int? positionInQueue = null,
        int? estimatedWaitMinutes = null,
        CustomerDto? customer = null) => new()
    {
        Id = token.Id,
        TokenNumber = token.TokenNumber,
        DisplayNumber = token.DisplayNumber,
        Notes = token.Notes,
        Status = token.Status,
        Priority = token.Priority,
        Source = token.Source,
        BranchId = token.BranchId,
        ServiceTypeId = token.ServiceTypeId,
        CounterId = token.CounterId,

        Customer = customer ?? new CustomerDto
        {
            Id = token.CustomerId,
            Name = token.CustomerName,
            Phone = token.CustomerPhone,
            Email = token.CustomerEmail
        },

        Counter = counter == null ? null : new CounterDto
        {
            Id = counter.Id,
            CounterNumber = counter.CounterNumber,
            DisplayName = counter.DisplayName,
            Status = counter.Status
        },

        ServiceType = serviceType == null ? null : new ServiceTypeDto
        {
            Id = serviceType.Id,
            Name = serviceType.Name,
            Code = serviceType.Code,
            Description = serviceType.Description,
            Prefix = serviceType.Prefix,
            AverageServiceTimeMinutes = serviceType.AverageServiceTimeMinutes,
            Color = serviceType.Color
        },

        ExternalReference = token.ExternalReference,
        ExternalSystem = token.ExternalSystem,
        Metadata = ReadMetadata(token.Metadata),

        PositionInQueue = positionInQueue,
        EstimatedWaitMinutes = estimatedWaitMinutes ?? token.EstimatedWaitMinutes,
        ActualWaitMinutes = token.ActualWaitMinutes,
        ServiceDurationMinutes = token.ServiceDurationMinutes,

        CreatedAt = token.CreatedAt,
        CalledAt = token.CalledAt,
        ServiceStartedAt = token.ServiceStartedAt,
        ServiceCompletedAt = token.ServiceCompletedAt
    };

    /// <summary>
    /// The display-safe shape: what a screen in a waiting room may show, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>QueueHub</c> is <c>[AllowAnonymous]</c> by design — public customer displays and queue
    /// boards subscribe to it without a login — so anyone who knows a branch GUID receives every
    /// token broadcast on it. That means a broadcast payload must be scrubbed, and it was not:
    /// <c>QueueHubService</c>'s own copy of the mapping carried <c>Customer</c>, so the customer's
    /// <b>name, phone number and email</b> were being pushed to anonymous subscribers on every
    /// TokenCreated and TokenServing. That predates this mapper and is the reason it exists as a
    /// separate method rather than the full one with a flag.
    /// </para>
    /// <para>
    /// The removed set is exactly the one <c>QueueController.GetPublicWaitingTokens</c> already
    /// strips for the public board — customer, notes, metadata and the external-system fields — so
    /// the two public paths now agree about what "public" means instead of each deciding alone.
    /// </para>
    /// </remarks>
    public static TokenDto ToPublicDto(Token token) => new()
    {
        Id = token.Id,
        TokenNumber = token.TokenNumber,
        DisplayNumber = token.DisplayNumber,
        Status = token.Status,
        Priority = token.Priority,
        Source = token.Source,
        BranchId = token.BranchId,
        ServiceTypeId = token.ServiceTypeId,
        CounterId = token.CounterId,
        EstimatedWaitMinutes = token.EstimatedWaitMinutes,
        ActualWaitMinutes = token.ActualWaitMinutes,
        CreatedAt = token.CreatedAt,
        CalledAt = token.CalledAt
    };

    /// <summary>
    /// The metadata blob, or null when it is absent or unreadable. The copies called
    /// <c>JsonSerializer.Deserialize</c> bare, so a malformed blob — which this column can hold,
    /// since integrations write into it — threw out of the middle of a queue read. An opaque
    /// integration field this app does not own must not be able to fail a Call Next.
    /// </summary>
    private static Dictionary<string, object>? ReadMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
