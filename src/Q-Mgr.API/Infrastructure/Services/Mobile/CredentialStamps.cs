using QMgr.Domain.Entities.Identity;

namespace QMgr.Infrastructure.Services.Mobile;

/// <summary>
/// The ONE place <see cref="User.CredentialStamp"/> is written.
///
/// <para>Rolling the stamp signs out every device this person has, on that device's next refresh,
/// without finding or deleting a single row — each <see cref="UserDeviceSession"/> keeps a copy of
/// the value it was issued under and stops matching. That is the whole kill switch.</para>
///
/// <para><b>Why a class for one line.</b> Because the failure mode is silent and this codebase has
/// paid for it before: a new path that changes a password and forgets to roll the stamp leaves
/// every handset signed in with the old credential, and nothing anywhere says so. A single named
/// call is greppable; an inline assignment is not. Same reasoning as
/// <c>RegistrationIdentity</c> and <c>PersonCode</c>.</para>
///
/// <para><b>Call this from every path that changes what a password IS</b> — a self-service reset,
/// an administrator issuing a temporary password, a deactivation. Do NOT call it on an ordinary
/// sign-in: that would sign the person's other devices out every time they used one.</para>
/// </summary>
public static class CredentialStamps
{
    /// <summary>
    /// Give this user a new credential version. The caller still has to save.
    /// </summary>
    public static void Roll(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.CredentialStamp = Guid.NewGuid().ToString("n");
    }

    /// <summary>
    /// True when a stored session's stamp still matches the account's.
    ///
    /// <para>Null on BOTH sides matches, and that is deliberate rather than an oversight: a
    /// session issued before the column existed carries null, and an account whose password has
    /// never changed since carries null too. Treating that as a mismatch would have signed every
    /// existing device out the moment the column shipped.</para>
    /// </summary>
    public static bool Matches(string? sessionStamp, string? userStamp)
        => string.Equals(sessionStamp ?? string.Empty, userStamp ?? string.Empty, StringComparison.Ordinal);
}
