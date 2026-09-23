using System.Net.Http.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// THE ONE HOME for "what must a password be, and how do we say it".
///
/// <para>Four pages ask a person for a password — sign-up, the profile, the join page and the
/// forced set-password page — and before this, three of them carried a byte-identical copy of the
/// fetch, the swallowed failure and a default-constructed <see cref="PasswordRulesDto"/>. That is
/// this codebase's most-repeated bug (see the DTO section of CLAUDE.md): a rule stated in several
/// places drifts, and the copies had already drifted once, hardcoding 12, 8 and 6 while the server
/// enforced a fourth number.</para>
///
/// <para><b>Null means NOT READ, and that distinction is the point.</b> The previous copies each
/// began life as <c>new PasswordRulesDto()</c>, whose <c>MinimumLength</c> is 12 and whose
/// composition flags are all off — so a failed fetch rendered "At least 12 characters. Length is
/// what matters…", which is word for word what a SUCCESSFUL fetch against a 12-character policy
/// renders. A broken read and a correct one were byte-identical, which is exactly how the
/// set-password page shipped, deployed and lied for a day. Nothing here ever invents a number.</para>
///
/// <para>Scoped, so one Blazor circuit fetches once and every page after that is free. The policy is
/// platform-wide and changes about never; a person does not need it re-read between two pages of one
/// visit.</para>
/// </summary>
public interface IPasswordRulesService
{
    /// <summary>The rules if they have been read, otherwise null. Never a guess.</summary>
    PasswordRulesDto? Current { get; }

    /// <summary>
    /// Read them if they have not been read. Never throws and never returns a made-up record: a page
    /// that will not render because it could not fetch a HINT is worse than a briefly generic hint,
    /// and the server validates the password regardless of what any form said.
    /// </summary>
    Task<PasswordRulesDto?> LoadAsync();

    /// <summary>What a password box says before the rules land, or if they never do.</summary>
    string Placeholder { get; }

    /// <summary>The sentence under the box. Names no number until one has actually been read.</summary>
    string Summary { get; }

    /// <summary>
    /// The client-side length check, in one place rather than three.
    ///
    /// <para><b>It passes when the rules are unread</b>, deliberately. Refusing a password against a
    /// minimum nobody has fetched would block somebody on a rule this app invented; the server holds
    /// the real one and says so in words. A form guesses in the person's favour and lets the server
    /// be the authority — never the other way round.</para>
    /// </summary>
    bool MeetsLength(string? password);

    /// <summary>
    /// The refusal to show beside the box, or null when there is nothing to say. Same rule as
    /// <see cref="MeetsLength"/>: silent until the real minimum is known.
    /// </summary>
    string? LengthProblem(string? password);
}

public sealed class PasswordRulesService : IPasswordRulesService
{
    private const string Route = "api/v1/security-policy/password-rules";

    /// <summary>
    /// True under every policy this product can be configured to, so it is safe to show while the
    /// real rules are still in flight and is not a lie if they never arrive. The server states the
    /// exact rule when it refuses.
    /// </summary>
    private const string GenericSummary =
        "Length is what matters — a memorable phrase beats a short, complicated one. "
        + "Avoid your name, your school's name and passwords in common use.";

    private readonly HttpClient _http;
    private readonly ILogger<PasswordRulesService> _logger;
    private Task<PasswordRulesDto?>? _inFlight;

    public PasswordRulesService(HttpClient http, ILogger<PasswordRulesService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public PasswordRulesDto? Current { get; private set; }

    public Task<PasswordRulesDto?> LoadAsync()
    {
        if (Current is not null) return Task.FromResult<PasswordRulesDto?>(Current);
        // One fetch even when two components on a page ask at once — the second awaits the first
        // rather than issuing a duplicate request.
        return _inFlight ??= FetchAsync();
    }

    private async Task<PasswordRulesDto?> FetchAsync()
    {
        try
        {
            Current = await _http.GetFromJsonAsync<PasswordRulesDto>(Route);
        }
        catch (Exception ex)
        {
            // LOGGED, not silent. The three copies this replaces each swallowed it into an empty
            // catch, which is half of why the set-password page could be broken in production with
            // nothing anywhere to say so. A hint that could not be read is still not worth failing a
            // page for, but it is worth a line in the log.
            _logger.LogWarning(ex, "Password rules could not be read from {Route}; forms will show the generic wording", Route);
            _inFlight = null;
        }
        return Current;
    }

    public string Placeholder => Current?.Placeholder ?? "Choose a password";

    public string Summary => Current?.Summary ?? GenericSummary;

    public bool MeetsLength(string? password)
        => Current is null || (password?.Length ?? 0) >= Current.MinimumLength;

    public string? LengthProblem(string? password)
        => MeetsLength(password) ? null : $"At least {Current!.MinimumLength} characters";
}
