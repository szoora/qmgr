using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// The one reader and writer of <c>Organization.Settings["StaffOnboarding"]</c> (duty rota plan §12.4),
/// and the one home for what a join code is: how it is made, how it is stored (a hash, never the code)
/// and whether a presented code is live.
/// </summary>
public interface IStaffOnboardingPolicyService
{
    StaffOnboardingPolicyDto ReadPolicy(string? organizationSettingsJson);
    Task<StaffOnboardingPolicyDto> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-modify-write of the whole settings blob under <c>pg_advisory_xact_lock</c> — THE SAME lock key
    /// the staff performance policy takes, because both live in the one <c>Organization.Settings</c>
    /// column and two writers on different locks would overwrite each other's key. The mutation returns
    /// false to write nothing.
    /// </summary>
    Task MutateAsync(Guid organizationId, Func<StaffOnboardingPolicyDto, Task<bool>> mutate, CancellationToken cancellationToken = default);

    /// <summary>Finds the organization whose live join link matches the code, or null — unknown, rotated, expired and switched-off all read the same.</summary>
    Task<(Guid OrganizationId, StaffOnboardingPolicyDto Policy)?> ResolveCodeAsync(string? code, CancellationToken cancellationToken = default);
}

public class StaffOnboardingPolicyService : IStaffOnboardingPolicyService
{
    private const string PolicyKey = "StaffOnboarding";
    private readonly QMgrDbContext _db;

    public StaffOnboardingPolicyService(QMgrDbContext db) => _db = db;

    /// <summary>The lock every writer of Organization.Settings shares — OrganizationSettingsLock's, the one key.</summary>
    public static string SettingsLockKey(Guid organizationId) => OrganizationSettingsLock.Key(organizationId);

    /// <summary>
    /// 160 random bits as 32 characters of base32 (A–Z, 2–7). Base32 cannot parse as a GUID, so
    /// <c>/join/{code}</c> never collides with the queue's <c>/join/{branchId:guid}</c>.
    /// </summary>
    public static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder(32);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static string HashCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()))).ToLowerInvariant();

    public StaffOnboardingPolicyDto ReadPolicy(string? organizationSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(organizationSettingsJson)) return new StaffOnboardingPolicyDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson);
            if (root != null && root.TryGetValue(PolicyKey, out var element))
            {
                var policy = JsonSerializer.Deserialize<StaffOnboardingPolicyDto>(element.GetRawText()) ?? new StaffOnboardingPolicyDto();
                policy.AllowedEmailDomains ??= new();
                policy.RequireApproval = true; // v1: always
                policy.RequestExpiryDays = Math.Clamp(policy.RequestExpiryDays, 1, 90);
                policy.PurgeAfterDays = Math.Clamp(policy.PurgeAfterDays, 1, 365);
                return policy;
            }
        }
        catch (JsonException) { /* malformed blob — defaults */ }
        return new StaffOnboardingPolicyDto();
    }

    private static string WritePolicy(string? organizationSettingsJson, StaffOnboardingPolicyDto policy)
    {
        Dictionary<string, JsonElement> root;
        try
        {
            root = string.IsNullOrWhiteSpace(organizationSettingsJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson) ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException) { root = new Dictionary<string, JsonElement>(); }

        root[PolicyKey] = JsonSerializer.SerializeToElement(policy);
        return JsonSerializer.Serialize(root);
    }

    public async Task<StaffOnboardingPolicyDto> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => o.Settings)
            .FirstOrDefaultAsync(cancellationToken);
        return ReadPolicy(settings);
    }

    public async Task MutateAsync(Guid organizationId, Func<StaffOnboardingPolicyDto, Task<bool>> mutate, CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            var lockKey = SettingsLockKey(organizationId);
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", cancellationToken);

            var org = await _db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken)
                      ?? throw new InvalidOperationException("Organization not found");
            var policy = ReadPolicy(org.Settings);
            if (await mutate(policy))
            {
                org.Settings = WritePolicy(org.Settings, policy);
                await _db.SaveChangesAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
        });
    }

    public async Task<(Guid OrganizationId, StaffOnboardingPolicyDto Policy)?> ResolveCodeAsync(string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length < 20 || code.Trim().Length > 64) return null;
        var hash = HashCode(code);

        // Settings is a jsonb column, so match the stored hash by its JSON path — exact, and raw SQL, so the
        // table is schema-qualified by hand (CLAUDE.md: raw SQL does not get the default schema). The policy
        // is then read back properly to check the switch and the expiry.
        var candidates = await _db.Organizations
            .FromSqlInterpolated($"SELECT * FROM qmgr.organizations WHERE \"Settings\"->'StaffOnboarding'->>'JoinCodeHash' = {hash}")
            .IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Status == QMgr.Domain.Enums.TenantStatus.Active || o.Status == QMgr.Domain.Enums.TenantStatus.Trialing)
            .Select(o => new { o.Id, o.Settings })
            .Take(2)
            .ToListAsync(cancellationToken);

        foreach (var c in candidates)
        {
            var policy = ReadPolicy(c.Settings);
            if (!policy.JoinEnabled || policy.JoinCodeHash == null) continue;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(policy.JoinCodeHash), Encoding.ASCII.GetBytes(hash))) continue;
            if (policy.JoinCodeExpiresAt is { } expires && expires <= DateTime.UtcNow) return null;
            return (c.Id, policy);
        }
        return null;
    }
}
