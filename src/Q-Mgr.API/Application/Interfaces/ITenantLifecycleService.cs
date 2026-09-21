using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Application.Interfaces;

/// <summary>
/// The ONE home for where a tenant is in its life and what moves it on. Every status change goes
/// through here so that each one is written down, warned about, and has its next clock set.
///
/// The states and the clocks:
///
///   Pending   ──14d─→ Suspended        (signed up, never verified)
///   Trialing  ──────→ Suspended        (trial end; already real-time, in TenantStatusMiddleware)
///   Active    ──────→ Suspended        (payment failed)
///   Suspended ──60d─→ Cancelled
///   Cancelled ──30d─→ PendingDeletion
///   PendingDeletion ──14d─→ PURGED     (irreversible; no row left)
///
/// 104 days from the last day of a trial to irreversible deletion, with three chances to come back
/// and one explicit human-reversible window at the end. That sits between Google's 30 days and
/// Microsoft's 90-plus and is defensible against either.
///
/// Two rules that are easy to break:
///   - A PURGE IS NEVER TRIGGERED BY A CUSTOMER-FACING ACTION. Only the daily sweep on an elapsed
///     clock, or a platform administrator acting deliberately. There is no "delete my account"
///     button that reaches it in one press, because there is no way to make that safe.
///   - Reactivation from Suspended or Cancelled is a NORMAL operation and must stay one. Most of
///     these tenants are a school that meant to pay and did not get round to it.
/// </summary>
public interface ITenantLifecycleService
{
    /// <summary>Moves a tenant to a new state, writes the event, sets the next clock, and warns the tenant where the state calls for it.</summary>
    Task<TenantLifecycleResult> TransitionAsync(Guid organizationId, TenantStatus to, string actor, Guid? actorUserId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Where this tenant is, when its next automatic move is due, and its history.</summary>
    Task<TenantLifecycleStatusDto> GetStatusAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The daily sweep. Advances every tenant whose clock has elapsed and sends the warnings that
    /// fall due. Returns how many it moved.
    /// </summary>
    Task<int> RunDueTransitionsAsync(CancellationToken cancellationToken = default);
}

public sealed record TenantLifecycleResult(bool Ok, string? Error, TenantLifecycleStatusDto Status);
