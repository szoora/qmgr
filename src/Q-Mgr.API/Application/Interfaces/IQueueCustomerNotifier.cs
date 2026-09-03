namespace QMgr.Application.Interfaces;

/// <summary>
/// Sends queue updates to the *customer* (the person holding the ticket), as opposed to
/// <see cref="INotificationService"/>, which is the low-level channel plumbing, and
/// <see cref="INotificationHubService"/>, which pushes to signed-in staff.
///
/// Why this exists: until this was added, nothing in the queue ever contacted a customer.
/// CreateTokenCommandHandler captured CustomerPhone/CustomerEmail and then never used them, so
/// the only way to know your turn had come was to stand in front of the display.
///
/// CONTRACT — every method here is fire-and-forget and MUST NOT throw or block the caller:
/// the work is dispatched onto a background task with its own DI scope, and every failure is
/// caught and logged. A dead SMS gateway or a slow SMTP server must never fail (or slow down)
/// a Call Next — that is the core action of the whole product.
///
/// Because the work outlives the request, these methods deliberately take no CancellationToken:
/// passing the request's token would cancel the send the moment the HTTP response completed.
/// </summary>
public interface IQueueCustomerNotifier
{
    /// <summary>
    /// "Ticket issued" — confirmation carrying the ticket number and the customer's current
    /// position. Call AFTER the creating transaction has committed.
    /// </summary>
    Task NotifyTicketIssuedAsync(Guid tokenId, int positionInQueue);

    /// <summary>
    /// "It's your turn" — fired when a token is called to a counter, naming the counter.
    /// Call AFTER the call-next/call-specific transaction has committed, so a gateway timeout
    /// can never roll the call back.
    /// </summary>
    Task NotifyCalledToCounterAsync(Guid tokenId, Guid counterId);

    /// <summary>
    /// "You're nearly up" — fired for the tokens that are now within the configured position
    /// threshold of the front of this branch/service queue, after someone has been called off
    /// the front of it. Reads the waiting list once and skips any token already notified for
    /// this stage (Token.LastNotifiedStage).
    /// </summary>
    Task NotifyApproachingTurnAsync(Guid branchId, Guid serviceTypeId);
}
