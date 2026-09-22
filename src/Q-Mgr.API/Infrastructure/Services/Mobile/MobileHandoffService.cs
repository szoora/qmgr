using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace QMgr.Infrastructure.Services.Mobile;

/// <summary>
/// The one-shot code that carries a signed-in identity from the native app into its own WebView.
///
/// <para><b>Why a code at all.</b> The app holds a bearer token; the web UI signs in with what it
/// keeps in <c>localStorage</c>. A WebView navigation is a GET, so the obvious move — put the
/// access token in the query string — writes a live credential into proxy logs, browser history and
/// referrer headers. This trades the bearer token for a value that is worthless 60 seconds later
/// and worthless after a single use.</para>
///
/// <para><b>Single use is enforced HERE and nowhere else.</b> <see cref="Redeem"/> removes the entry
/// before returning it, so two simultaneous redemptions cannot both win, and a failure downstream
/// cannot leave a replayable code behind. That ordering is the rule the ERP learned the hard way and
/// it must not be relaxed into "check, sign in, then remove".</para>
///
/// <para><b>In-memory on purpose.</b> The code's whole life is one navigation on one device,
/// measured in milliseconds. Persisting it would mean a row to expire, a table to purge and a
/// second thing to get wrong, for a value that is dead before a background job would notice it.
/// The cost is that a code minted by one Web instance cannot be redeemed by another — which does
/// not arise here, because the API is a single unit behind nginx, and if it ever became a farm this
/// is the note that says what to change.</para>
/// </summary>
public interface IMobileHandoffService
{
    /// <summary>Mint a code for this user. Returns the code and when it dies.</summary>
    (string Code, DateTime ExpiresAt) Issue(Guid userId);

    /// <summary>
    /// Consume a code. Returns the user id, or null when the code is unknown, already used or
    /// expired — which read the same on purpose, because telling them apart tells a caller which
    /// guesses were close.
    /// </summary>
    Guid? Redeem(string? code);
}

public sealed class MobileHandoffService : IMobileHandoffService
{
    /// <summary>
    /// Deliberately tiny. It exists to carry an identity across one hop, not to be stored.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private const string Prefix = "mobile:handoff:";

    private readonly IMemoryCache _cache;

    public MobileHandoffService(IMemoryCache cache) => _cache = cache;

    public (string Code, DateTime ExpiresAt) Issue(Guid userId)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                          .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        _cache.Set(Prefix + code, userId, Lifetime);
        return (code, DateTime.UtcNow.Add(Lifetime));
    }

    public Guid? Redeem(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var key = Prefix + code;
        if (!_cache.TryGetValue(key, out Guid userId)) return null;

        // Removed BEFORE the caller does anything with it. See the class comment: this ordering is
        // what makes the code genuinely single-use.
        _cache.Remove(key);

        return userId == Guid.Empty ? null : userId;
    }
}
