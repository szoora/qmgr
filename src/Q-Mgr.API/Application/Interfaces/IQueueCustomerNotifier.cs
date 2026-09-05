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

    /// <summary>
    /// The one blocking send in this interface, and the one that reports back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything above is fire-and-forget because a customer notification must never sit on the
    /// critical path of a queue operation. This is the opposite situation: somebody is standing at
    /// the kiosk having just asked for their ticket to be texted to them, and "we have accepted
    /// your request" is not an answer — they need to know whether to wait for a message or write
    /// the number down. So this one awaits the gateway and says what happened.
    /// </para>
    /// <para>
    /// It also <em>stores</em> the number on the token, which is the larger half of the value: a
    /// customer who skipped the phone step at the start now gets the "it's your turn" message too,
    /// not just this one confirmation.
    /// </para>
    /// <para>
    /// Unlike the others it may throw — the caller is an HTTP action that wants to translate a
    /// failure into a response, not a queue operation that must not be disturbed.
    /// </para>
    /// </remarks>
    Task<TicketDetailsSendResult> SendTicketDetailsAsync(Guid tokenId, Guid branchId, string phoneNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Why a ticket SMS did or did not go out. Specific on purpose: "not configured" is an
/// administrator's job, "notifications are switched off" is a settings toggle, and "the gateway
/// refused it" is a support call — telling a customer the same thing for all three would send them
/// to the wrong person.
/// </summary>
public enum TicketDetailsSendOutcome
{
    Sent = 0,

    /// <summary>The organization has SMS switched off, or queue SMS specifically.</summary>
    ChannelDisabled = 1,

    /// <summary>No SMS provider credentials are configured for this organization.</summary>
    NotConfigured = 2,

    /// <summary>The gateway was called and did not accept the message.</summary>
    SendFailed = 3,

    /// <summary>No such token in this branch.</summary>
    TokenNotFound = 4
}

/// <param name="Outcome">What happened.</param>
/// <param name="PhoneStored">
/// True when the number was saved to the token, which happens even when the message itself could
/// not be sent — the later "it's your turn" notification will then still reach them if the channel
/// is fixed in the meantime.
/// </param>
public record TicketDetailsSendResult(TicketDetailsSendOutcome Outcome, bool PhoneStored);
