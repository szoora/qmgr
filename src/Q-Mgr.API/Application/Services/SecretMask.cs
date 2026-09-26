namespace QMgr.API.Application.Services;

/// <summary>
/// The one home for how a stored secret leaves the API and comes back in. A non-empty secret is
/// always sent as <see cref="Value"/>, the eight dots every editor already shows; empty stays
/// empty, so "not set" still reads as not set. On save, an incoming value that IS the mask means
/// "unchanged" and <see cref="Keep"/> puts the stored secret back, so a save that did not retype a
/// password does not blank it.
///
/// <para>WHY THERE IS ONE (2026-09-25). The platform settings were masked on 2026-09-15, but the
/// TENANT notification settings were not: <c>GET notifications/settings</c> returned the school's
/// SMS API key and password, SMTP password, Telegram token and WhatsApp token as stored, to every
/// holder of <c>notifications.view</c> — nine seeded roles, every teacher included. Both
/// controllers now use this class, and a new secret property goes through it or it leaks.</para>
/// </summary>
public static class SecretMask
{
    /// <summary>The mask a set secret is shown as.</summary>
    public const string Value = "••••••••";

    /// <summary>The mask for a set secret; empty or null unchanged.</summary>
    public static string? Hide(string? secret) => string.IsNullOrEmpty(secret) ? secret : Value;

    /// <summary>Whether a secret is set, for a read that needs only that.</summary>
    public static bool IsSet(string? secret) => !string.IsNullOrEmpty(secret);

    /// <summary>The value to store: the stored one when the mask came back, otherwise what was sent.</summary>
    public static string? Keep(string? incoming, string? stored) => incoming == Value ? stored : incoming;
}
