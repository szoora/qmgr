using System.Collections.Concurrent;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services.Domains;

/// <summary>
/// A DNS TXT lookup that answers from memory. DEVELOPMENT ONLY, and registered only when
/// <c>Dns:Stub</c> is set — <c>DependencyInjection</c> checks the environment as well as the key,
/// so it can never be reached on a deployed server.
///
/// It exists because verifying a domain needs a zone somebody owns, and the whole point of the
/// standing verification rule here is that a feature is exercised against a live path rather than
/// reasoned about. This is the one dependency in the flow that cannot be satisfied locally, so it
/// is the one thing stubbed — everything else in <c>CustomDomainService</c> runs for real against
/// the real database.
/// </summary>
public sealed class StubDnsTxtLookup : IDnsTxtLookup
{
    /// <summary>Static, so the Development-only endpoint that seeds it and the scoped service that reads it share one map.</summary>
    private static readonly ConcurrentDictionary<string, string[]> Records = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<StubDnsTxtLookup> _logger;

    public StubDnsTxtLookup(ILogger<StubDnsTxtLookup> logger)
    {
        _logger = logger;
        _logger.LogWarning("DNS TXT lookups are STUBBED (Dns:Stub). Development only — no real zone is read.");
    }

    public static void Publish(string name, params string[] values) => Records[name.Trim().TrimEnd('.')] = values;

    public static void Clear(string name) => Records.TryRemove(name.Trim().TrimEnd('.'), out _);

    public Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken = default)
    {
        var key = (name ?? string.Empty).Trim().TrimEnd('.');
        var found = Records.TryGetValue(key, out var values) ? values : [];
        _logger.LogInformation("Stub TXT lookup for {Name}: {Count} record(s)", key, found.Length);
        return Task.FromResult<IReadOnlyList<string>>(found);
    }
}
