using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// The one reader and writer of <c>Branch.Settings["Timetable"]</c> — the bell schedule, the cycle and declared
/// unavailability (duty rota plan §3.1, §6.1). A second JSON reader of that key is the drift this exists to prevent.
/// </summary>
public interface ITimetableSettingsService
{
    /// <summary>What is stored, or the defaults (with <c>IsSaved = false</c>) when nothing is.</summary>
    TimetableSettingsDto Read(string? branchSettingsJson);

    Task<TimetableSettingsDto> ReadAsync(Guid branchId, CancellationToken ct = default);

    /// <summary>
    /// Writes the settings. MUST run inside the caller's transaction: it takes the branch-settings advisory lock and
    /// re-reads the blob under it, so a vocabulary save on the same branch at the same moment cannot lose this key or
    /// have its own key lost (they share one JSON column).
    /// </summary>
    Task WriteAsync(Guid branchId, TimetableSettingsDto settings, CancellationToken ct = default);
}

public class TimetableSettingsService : ITimetableSettingsService
{
    public const string SettingsKey = "Timetable";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly QMgrDbContext _db;

    public TimetableSettingsService(QMgrDbContext db) => _db = db;

    public TimetableSettingsDto Read(string? branchSettingsJson)
    {
        if (!string.IsNullOrEmpty(branchSettingsJson))
        {
            try
            {
                var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(branchSettingsJson);
                if (root != null && root.TryGetValue(SettingsKey, out var element))
                {
                    var stored = element.Deserialize<TimetableSettingsDto>(Json);
                    if (stored != null && stored.DayTypes.Count > 0)
                    {
                        stored.IsSaved = true;
                        stored.CycleWeeks = Math.Clamp(stored.CycleWeeks, 1, 2);
                        return stored;
                    }
                }
            }
            catch (JsonException) { /* a malformed blob reads as not configured */ }
        }
        return TimetableCycle.Defaults();
    }

    public async Task<TimetableSettingsDto> ReadAsync(Guid branchId, CancellationToken ct = default)
        => Read(await _db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync(ct));

    public async Task WriteAsync(Guid branchId, TimetableSettingsDto settings, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Timetable settings are written inside a transaction holding the branch-settings lock.");

        await BranchSettingsLock.AcquireAsync(_db, branchId, ct);
        var current = await _db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync(ct);

        var merged = string.IsNullOrEmpty(current)
            ? new Dictionary<string, object>()
            : (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current) ?? new()).ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        settings.IsSaved = true;
        merged[SettingsKey] = settings;
        var json = JsonSerializer.Serialize(merged);
        var now = DateTime.UtcNow;

        await _db.Branches.IgnoreQueryFilters().Where(b => b.Id == branchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Settings, json).SetProperty(b => b.UpdatedAt, now), ct);
    }
}

/// <summary>
/// <c>Branch.Settings</c> is one JSON column holding several independent keys (vocabularies, class colours, the
/// timetable). Every read-modify-write of it takes this lock inside its transaction and re-reads under it.
/// </summary>
public static class BranchSettingsLock
{
    public static Task AcquireAsync(QMgrDbContext db, Guid branchId, CancellationToken ct = default)
    {
        var lockKey = $"branch-settings:{branchId}";
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);
    }
}

/// <summary>
/// The same rule one level up. <c>Organization.Settings</c> is also one JSON column holding several
/// independent keys — the document-sharing policy, the staff-performance policy, staff onboarding,
/// industry features, visitor retention and module billing — and each writer deserialises the whole
/// blob, sets its own key and re-serialises it. Two concurrent saves on *different* keys therefore
/// lose one of them.
///
/// <para>Found by the 2026-09-18 audit: six writers, and only the two staff ones took a lock (on
/// <c>staff-policy:{orgId}</c>, which the onboarding service's own comment explains). A lock that
/// some writers take and others do not protects nothing, so this is the one key they all share.</para>
///
/// <para><b>Take it inside the transaction and re-read the blob under it</b> — acquiring it and then
/// using a copy of the settings fetched beforehand reintroduces exactly the race it prevents.</para>
/// </summary>
/// <para><b>Finished 2026-09-23.</b> The 2026-09-18 note above said this was "the one key they all
/// share" — and one writer took it. The staff policy and onboarding kept their own
/// <c>staff-policy:</c> key, and the industry features, feature overrides, visitor retention and
/// module-billing writers took no lock at all, so any of them could silently drop another's key. Every
/// writer now goes through <see cref="MutateAsync"/>, which is the only way this column is written.
/// PostgreSQL advisory locks are re-entrant within a session, so a caller that already holds this lock
/// (the staff policy editor, around a read-modify-write of its own key) may call MutateAsync inside it.</para>
public static class OrganizationSettingsLock
{
    public static string Key(Guid organizationId) => $"organization-settings:{organizationId}";

    public static Task AcquireAsync(QMgrDbContext db, Guid organizationId, CancellationToken ct = default)
    {
        var lockKey = Key(organizationId);
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);
    }

    /// <summary>
    /// THE ONE WAY <c>Organization.Settings</c> IS WRITTEN. Takes the lock, re-reads the organization
    /// under it (reloading a tracked copy rather than trusting it), hands it to <paramref name="mutate"/>
    /// and saves only when that returns true. Joins a transaction the caller already has; otherwise opens
    /// one through the execution strategy, as every user transaction here must.
    ///
    /// <para>Change other columns of the organization INSIDE the mutation, not before calling: the
    /// re-read under the lock discards whatever the caller had changed on a tracked copy.</para>
    /// </summary>
    /// <summary>
    /// <paramref name="json"/> with one top-level key set to <paramref name="value"/> (removed when it is
    /// null), every other key carried across untouched. The merge each writer used to hand-roll.
    /// </summary>
    public static string WithKey(string? json, string key, object? value)
    {
        Dictionary<string, JsonElement> root;
        try
        {
            root = string.IsNullOrWhiteSpace(json)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException) { root = new Dictionary<string, JsonElement>(); }

        if (value == null) root.Remove(key);
        else root[key] = JsonSerializer.SerializeToElement(value);
        return JsonSerializer.Serialize(root);
    }

    /// <returns>False when the organization does not exist.</returns>
    public static async Task<bool> MutateAsync(QMgrDbContext db, Guid organizationId,
        Func<QMgr.Domain.Entities.Organization.Organization, bool> mutate, CancellationToken ct = default)
    {
        async Task<bool> Body()
        {
            await AcquireAsync(db, organizationId, ct);

            var org = db.Organizations.Local.FirstOrDefault(o => o.Id == organizationId);
            if (org != null) await db.Entry(org).ReloadAsync(ct);
            else org = await db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == organizationId, ct);
            if (org == null) return false;

            if (mutate(org)) await db.SaveChangesAsync(ct);
            return true;
        }

        if (db.Database.CurrentTransaction != null) return await Body();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var found = await Body();
            await tx.CommitAsync(ct);
            return found;
        });
    }
}
