using System.Security.Cryptography;
using System.Text;

namespace QMgr.Infrastructure.Data.Purge;

/// <summary>
/// The one-way hash a <c>TenantTombstone</c> stores, and the ONE home for it.
///
/// It exists as its own type because two places compute it and they must never disagree: the purge
/// writes the hash, and <c>RegistrationGuardService</c> looks a returning sign-up up by it. A second
/// copy of a hashing rule that drifts would silently stop matching, and nothing would look broken —
/// the guard would simply never flag anybody again.
///
/// Salted with a fixed application salt so a stored value is not a rainbow-table lookup over the
/// short list of Ugandan school domains. It is never reversed and never shown to anybody.
/// </summary>
public static class TenantTombstoneHash
{
    private const string Salt = "qmgr-tombstone-v1:";

    public static string? Of(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bytes = Encoding.UTF8.GetBytes(Salt + value.Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
