namespace QMgr.Domain.Identity;

/// <summary>
/// Throwaway-mailbox providers, shipped as data rather than fetched from a reputation API, because
/// this project does not take external service dependencies at runtime. The built-in list covers
/// the providers that actually appear in the wild; a platform administrator can extend or override
/// it from Platform Settings without a deploy, which is what keeps a static list workable.
/// <para>
/// A match is a strong signal, not a verdict. Some people genuinely use a forwarding service for
/// legitimate privacy reasons, so this contributes to a risk score rather than refusing outright.
/// </para>
/// </summary>
public static class DisposableEmailDomains
{
    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "10minutemail.com", "10minutemail.net", "20minutemail.com",
        "guerrillamail.com", "guerrillamail.net", "guerrillamail.org", "guerrillamail.biz",
        "sharklasers.com", "grr.la", "spam4.me",
        "mailinator.com", "mailinator.net", "reallymymail.com",
        "tempmail.com", "temp-mail.org", "tempmail.net", "tempr.email",
        "throwawaymail.com", "trashmail.com", "trashmail.de", "wegwerfmail.de",
        "yopmail.com", "yopmail.net", "cool.fr.nf", "jetable.org",
        "dispostable.com", "getnada.com", "nada.email",
        "maildrop.cc", "mintemail.com", "mytemp.email",
        "fakeinbox.com", "spamgourmet.com", "mailnesia.com",
        "moakt.com", "tmpmail.org", "tmpmail.net",
        "emailondeck.com", "burnermail.io", "mohmal.com",
        "inboxbear.com", "tempinbox.com", "spambog.com",
        "discard.email", "mailcatch.com", "harakirimail.com",
        "anonaddy.me", "simplelogin.com", "duck.com"
    };

    /// <summary>
    /// True when the address belongs to a known throwaway provider.
    /// </summary>
    /// <param name="email">The address as supplied; only its domain is examined.</param>
    /// <param name="additionalDomains">
    /// Extra domains configured by a platform administrator, merged with the built-in list.
    /// </param>
    public static bool IsDisposable(string? email, IEnumerable<string>? additionalDomains = null)
    {
        var domain = RegistrationIdentity.EmailDomain(email);
        if (string.IsNullOrEmpty(domain)) return false;

        if (BuiltIn.Contains(domain)) return true;

        if (additionalDomains != null)
        {
            foreach (var extra in additionalDomains)
            {
                if (!string.IsNullOrWhiteSpace(extra) &&
                    string.Equals(extra.Trim().TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The built-in list, for showing an administrator what is already covered.</summary>
    public static IReadOnlyCollection<string> BuiltInDomains => BuiltIn;
}
