using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// The platform administrator's test prompt — a small real collection that proves the whole path: the
/// key collects, the phone rings, and the gateway's signed webhook reaches this install (2026-09-19).
///
/// A test prompt belongs to no organization, so it has no ledger row; its progress is kept here, in the
/// distributed cache, for a day. The webhook receiver marks <see cref="Entry.WebhookReceived"/> for a
/// reference it finds here, which is the one thing a status read cannot tell the administrator.
/// </summary>
public static class SaccTestPrompts
{
    /// <summary>What a test prompt charges — the smallest sum every operator accepts.</summary>
    public const decimal Amount = 500m;

    private static readonly DistributedCacheEntryOptions Lifetime = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) };

    public sealed record Entry(string Phone, string State, bool IsFinal, bool WebhookReceived, string Message);

    private static string Key(Guid referenceId) => $"sacc-test-prompt:{referenceId:N}";

    public static async Task<Entry?> GetAsync(IDistributedCache cache, Guid referenceId, CancellationToken cancellationToken = default)
    {
        var json = await cache.GetStringAsync(Key(referenceId), cancellationToken);
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Entry>(json);
    }

    public static Task SetAsync(IDistributedCache cache, Guid referenceId, Entry entry, CancellationToken cancellationToken = default) =>
        cache.SetStringAsync(Key(referenceId), JsonSerializer.Serialize(entry), Lifetime, cancellationToken);

    /// <summary>Record that the gateway's webhook arrived for this reference. False when the reference
    /// is not a test prompt.</summary>
    public static async Task<bool> MarkWebhookAsync(IDistributedCache cache, Guid referenceId, string? state, CancellationToken cancellationToken = default)
    {
        var entry = await GetAsync(cache, referenceId, cancellationToken);
        if (entry == null) return false;
        await SetAsync(cache, referenceId, entry with { WebhookReceived = true, State = state ?? entry.State }, cancellationToken);
        return true;
    }
}
