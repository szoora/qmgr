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
public static class OrganizationSettingsLock
{
    public static Task AcquireAsync(QMgrDbContext db, Guid organizationId, CancellationToken ct = default)
    {
        var lockKey = $"organization-settings:{organizationId}";
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);
    }
}
