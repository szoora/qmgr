using QMgr.Application.Interfaces.Billing;

namespace QMgr.API.Application.Services;

/// <summary>
/// "Is there room for <c>n</c> more ACTIVE people in this organisation?" — the one home for the user
/// limit on every path that turns somebody on: creating a user (through <c>[CheckLimit("users")]</c>,
/// which reads the same count), the Users &amp; Roles toggle, the bulk "enable accounts" batch and the
/// undo of a bulk disable.
///
/// <para>WHY ACTIVE (2026-09-23). The limit used to count every user row, deactivated ones included,
/// while the only way the product removes a person — <c>DELETE /users/{id}</c> — is a SOFT delete that
/// sets <c>IsActive = false</c>. So a school that removed a teacher who had left never got the seat back;
/// once full, it was full for good. A deactivated account cannot sign in and holds nothing but history,
/// which is why seat-based products count active members. The price of counting active is that
/// re-enabling somebody is now an ADD, and every path that re-enables must ask here first — otherwise
/// disable-then-enable is a way round the limit.</para>
/// </summary>
public static class UserSeats
{
    public const string LimitType = "users";

    /// <summary>Null when <paramref name="adding"/> more active people fit; otherwise the sentence to show.</summary>
    public static async Task<string?> RefusalAsync(IBillingService billing, Guid organizationId, int adding)
    {
        if (adding <= 0) return null;

        var limit = await billing.CheckLimitAsync(organizationId, LimitType);
        var room = limit.MaxAllowed - limit.CurrentUsage;
        if (room >= adding) return null;

        var asked = adding == 1 ? "another account" : $"{adding:N0} more accounts";
        var left = room <= 0 ? "none are free" : $"only {room:N0} {(room == 1 ? "is" : "are")} free";
        return $"Your plan allows {limit.MaxAllowed:N0} active users and {limit.CurrentUsage:N0} are active, so {asked} cannot be enabled — {left}. " +
               "Disable somebody who has left, or add capacity on the Modules tab.";
    }

    /// <summary>
    /// Active seats still free, for a caller that adds many people in one unit of work before saving (the staff
    /// import saves in chunks, so a live count would not see the rows it has added but not yet written).
    /// </summary>
    public static async Task<(int Room, int Max)> RoomAsync(IBillingService billing, Guid organizationId)
    {
        var limit = await billing.CheckLimitAsync(organizationId, LimitType);
        return (Math.Max(0, limit.MaxAllowed - limit.CurrentUsage), limit.MaxAllowed);
    }

    /// <summary>The per-row sentence once an import has used the last free seat.</summary>
    public static string FullForImportRow(int max) =>
        $"Not created: your plan allows {max:N0} active users and they are all taken. " +
        "Disable somebody who has left, or add capacity on the Modules tab, then import this row again.";
}
