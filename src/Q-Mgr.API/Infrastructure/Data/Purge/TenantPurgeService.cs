using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Platform;
using QMgr.Middleware;

namespace QMgr.Infrastructure.Data.Purge;

/// <inheritdoc cref="ITenantPurgeService"/>
public sealed class TenantPurgeService : ITenantPurgeService
{
    /// <summary>
    /// The column names that hold a path into the upload store. THE ONE DECLARED THING about files
    /// — which tables carry them is derived from the model, so a new table with a <c>PhotoUrl</c>
    /// is swept without anybody remembering to add it.
    /// </summary>
    private static readonly string[] FileColumns =
        ["FileUrl", "FilePath", "PhotoUrl", "LogoUrl", "FaviconUrl", "CoverImageUrl", "ThumbnailUrl"];

    /// <summary>
    /// Per-organization cache key prefixes, audited from the codebase. Stale rather than sensitive,
    /// but a cached entitlement set for a purged tenant is a tenant that still half-works.
    /// </summary>
    private static readonly string[] CachePrefixes =
        ["features:", "org-modules:", "usage:", "reg-attempts:", "organization-settings:", "staff-policy:", "staff-recognition:", "subjects-seed:", "email-brand:"];

    private readonly QMgrDbContext _db;
    private readonly IMediaStorageService _storage;
    private readonly IDistributedCache _cache;
    private readonly IFeatureFlagService _features;
    private readonly ICustomDomainService _domains;
    private readonly ILogger<TenantPurgeService> _logger;

    private TenantPurgeModel? _plan;
    private TenantPurgeModel Plan => _plan ??= TenantPurgeModel.Build(_db.Model);

    public TenantPurgeService(
        QMgrDbContext db,
        IMediaStorageService storage,
        IDistributedCache cache,
        IFeatureFlagService features,
        ICustomDomainService domains,
        ILogger<TenantPurgeService> logger)
    {
        _db = db;
        _storage = storage;
        _cache = cache;
        _features = features;
        _domains = domains;
        _logger = logger;
    }

    // ==========================================================================================
    // Inventory — read-only
    // ==========================================================================================

    public async Task<TenantResidueDto> InventoryAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var org = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);

        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        var retained = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var step in Plan.AllTenantTables)
        {
            var count = await CountAsync(step, organizationId, cancellationToken);
            if (count == 0) continue;
            if (step.Class == TenantDataClass.StatutoryRetention) retained[step.EntityName] = count;
            else rows[step.EntityName] = count;
        }

        var files = await CollectFileNamesAsync(organizationId, cancellationToken);
        long bytes = 0;
        var missing = 0;
        foreach (var name in files)
        {
            var disk = DiskPath(name);
            if (disk != null && File.Exists(disk)) bytes += new FileInfo(disk).Length;
            else missing++;
        }

        var external = new List<string>();
        if (!string.IsNullOrWhiteSpace(org?.StripeCustomerId)) external.Add($"Stripe customer {org.StripeCustomerId}");
        if (!string.IsNullOrWhiteSpace(org?.CustomDomain)) external.Add($"Custom domain {org.CustomDomain} (nginx server block and certificate)");
        if (!string.IsNullOrWhiteSpace(org?.CustomDomainPending)) external.Add($"Unverified domain claim {org.CustomDomainPending}");

        return new TenantResidueDto
        {
            OrganizationId = organizationId,
            OrganizationName = org?.Name ?? "(gone)",
            RowsByTable = rows,
            TotalRows = rows.Values.Sum(),
            RetainedByTable = retained,
            Files = files,
            FileBytes = bytes,
            FilesMissing = missing,
            BackgroundJobs = await CountBackgroundJobsAsync(organizationId, cancellationToken),
            CacheKeys = [.. CachePrefixes.Select(p => p + organizationId)],
            ExternalReferences = external,
        };
    }

    // ==========================================================================================
    // Purge
    // ==========================================================================================

    public async Task<TenantPurgeResultDto> PurgeAsync(Guid organizationId, string actor, Guid? actorUserId, string? reason, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var org = await _db.Organizations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (org == null)
            return new TenantPurgeResultDto { Ok = false, Error = "Organisation not found.", OrganizationId = organizationId };

        var name = org.Name;
        var liveDomain = org.CustomDomain;
        var pendingDomain = org.CustomDomainPending;
        var emailDomain = DomainOf(org.ContactEmail ?? org.BillingEmail);
        var nameKey = org.NameBlockingKey;

        // ── 1. FILES FIRST, while the rows that name them still exist ─────────────────────────
        var fileNames = await CollectFileNamesAsync(organizationId, cancellationToken);
        var (filesDeleted, filesFailed) = await DeleteFilesAsync(fileNames, cancellationToken);

        // ── 2. Background jobs, before the rows they reference ────────────────────────────────
        var jobsRemoved = await DeleteBackgroundJobsAsync(organizationId, cancellationToken);

        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        var retainedCount = 0;
        var verificationOk = false;
        string? verificationDetail = null;

        // ── 3-5. Rows, retention, and the completeness check — one transaction ────────────────
        // Through the execution strategy: this context uses NpgsqlRetryingExecutionStrategy, and
        // BeginTransactionAsync outside CreateExecutionStrategy().ExecuteAsync throws.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // The one cycle in the graph: Organization.SubscriptionId points at Subscription while
            // Subscription.OrganizationId points back. Break it first — which is what that FK's own
            // SetNull behaviour intends — so the ordering below has a straight run.
            await ExecuteAsync($"UPDATE \"qmgr\".\"organizations\" SET \"SubscriptionId\" = NULL WHERE \"Id\" = @org", organizationId, cancellationToken);

            foreach (var step in Plan.Steps)
            {
                var affected = await ExecuteAsync(
                    $"DELETE FROM {step.QualifiedTable} AS t WHERE {step.WhereClause}", organizationId, cancellationToken);
                if (affected > 0) deleted[step.EntityName] = affected;
            }

            // Statutory rows: de-identified in place, never deleted. Retained does not mean
            // untouched — the amounts and dates a tax audit needs stay, the contact details go.
            foreach (var (step, columns) in Plan.Retained)
            {
                if (columns.Length == 0) continue;
                var sets = string.Join(", ", columns.Select(c => $"\"{c}\" = NULL"));
                var affected = await ExecuteAsync(
                    $"UPDATE {step.QualifiedTable} AS t SET {sets} WHERE {step.WhereClause}", organizationId, cancellationToken);
                retainedCount += affected;
            }

            // ── VERIFY, before committing ──────────────────────────────────────────────────────
            var leftovers = await FindLeftoversAsync(organizationId, cancellationToken);
            if (leftovers.Count > 0)
            {
                verificationDetail = string.Join("; ", leftovers.Select(l => $"{l.Table}={l.Count}"));
                _logger.LogError(
                    "Tenant purge for {OrganizationId} ROLLED BACK: {Count} table(s) still held rows after the delete — {Detail}",
                    organizationId, leftovers.Count, verificationDetail);
                await tx.RollbackAsync(cancellationToken);
                return;
            }

            verificationOk = true;
            await tx.CommitAsync(cancellationToken);
        });

        stopwatch.Stop();

        // ── The certificate and the tombstone, written whatever happened ──────────────────────
        // A failed purge is a thing somebody needs to know about too.
        _db.Set<TenantPurgeCertificate>().Add(new TenantPurgeCertificate
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PurgedAt = DateTime.UtcNow,
            PurgedBy = actor,
            RowsDeletedJson = JsonSerializer.Serialize(deleted),
            FilesDeleted = filesDeleted,
            FilesFailed = filesFailed,
            BackgroundJobsRemoved = jobsRemoved,
            CacheKeysDropped = CachePrefixes.Length,
            RowsRetained = retainedCount,
            VerificationPassed = verificationOk,
            VerificationDetail = verificationDetail,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
        });

        if (verificationOk)
        {
            _db.Set<TenantTombstone>().Add(new TenantTombstone
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                PurgedAt = DateTime.UtcNow,
                PurgedByUserId = actorUserId,
                PurgedBy = actor,
                Reason = reason,
                EmailDomainHash = TenantTombstoneHash.Of(emailDomain),
                NameKeyHash = TenantTombstoneHash.Of(nameKey),
                HadFinancialRecords = retainedCount > 0,
                StatutoryRetentionUntil = retainedCount > 0 ? DateTime.UtcNow.Add(TenantDataManifest.StatutoryRetentionPeriod) : null,
            });

            _db.Set<TenantLifecycleEvent>().Add(new TenantLifecycleEvent
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                OrganizationName = name,
                FromStatus = Domain.Enums.TenantStatus.PendingDeletion,
                ToStatus = Domain.Enums.TenantStatus.Deleted,
                OccurredAt = DateTime.UtcNow,
                ActorUserId = actorUserId,
                Actor = actor,
                Reason = reason ?? "purged",
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        // ── 6. Everything outside the database, after the commit ──────────────────────────────
        if (verificationOk)
        {
            await DropCachesAsync(organizationId, cancellationToken);
            TenantResolutionMiddleware.ForgetCustomDomain(liveDomain);
            TenantResolutionMiddleware.ForgetCustomDomain(pendingDomain);

            // The nginx server block and the certificate. ReleaseAsync tolerates a missing
            // organisation row — it is a no-op once there is nothing to release — so the withdraw
            // is issued directly rather than through it.
            if (!string.IsNullOrWhiteSpace(liveDomain))
            {
                try { await _domains.ReleaseAsync(organizationId, cancellationToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not withdraw the custom domain for the purged tenant {OrganizationId}", organizationId); }
            }

            _logger.LogWarning(
                "Tenant {OrganizationId} ({Name}) PURGED by {Actor}: {Rows} row(s) across {Tables} table(s), {Files} file(s), {Jobs} background job(s), {Retained} row(s) retained under statute, in {Ms}ms",
                organizationId, name, actor, deleted.Values.Sum(), deleted.Count, filesDeleted, jobsRemoved, retainedCount, stopwatch.ElapsedMilliseconds);
        }

        return new TenantPurgeResultDto
        {
            Ok = verificationOk,
            Error = verificationOk ? null : $"The completeness check failed and nothing was deleted. Still present: {verificationDetail}",
            OrganizationId = organizationId,
            RowsDeleted = deleted,
            TotalRowsDeleted = deleted.Values.Sum(),
            FilesDeleted = filesDeleted,
            FilesFailed = filesFailed,
            BackgroundJobsRemoved = jobsRemoved,
            CacheKeysDropped = CachePrefixes.Length,
            RowsRetained = retainedCount,
            VerificationPassed = verificationOk,
            VerificationDetail = verificationDetail,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
        };
    }

    // ==========================================================================================
    // Verification
    // ==========================================================================================

    /// <summary>
    /// Every table that still holds a row for this tenant.
    ///
    /// READ FROM information_schema, NOT FROM THE EF MODEL, and that is the whole point: a table
    /// the model forgot about is still in the catalogue. Two passes that overlap on purpose — the
    /// catalogue pass catches anything with an <c>OrganizationId</c> column whatever the model
    /// thinks, and the model pass catches the derived tables the catalogue cannot see a link from.
    /// Statutory tables are excluded by name, because they are SUPPOSED to still be there.
    /// </summary>
    private async Task<List<(string Table, int Count)>> FindLeftoversAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var leftovers = new List<(string, int)>();

        // Tables that are SUPPOSED to still hold rows for this organization afterwards, and so are
        // not leftovers. Two kinds, and both are declared rather than guessed:
        //
        //   - StatutoryRetention: an invoice kept for five years, de-identified in place.
        //   - PlatformOwned: the tombstone, the certificate and the lifecycle log. Each carries an
        //     OrganizationId — which is exactly why the catalogue pass below finds them — and each
        //     is meant to OUTLIVE the organization. The purge's own last act writes two of them,
        //     so without this exclusion a purge could never commit: it would always find the rows
        //     it had just written and roll itself back. Found by the e2e on its first real run.
        var declaredSurvivors = TenantDataManifest.ByEntity
            .Where(kv => kv.Value is TenantDataClass.StatutoryRetention or TenantDataClass.PlatformOwned)
            .Select(kv => _db.Model.GetEntityTypes().FirstOrDefault(e => e.ClrType.Name == kv.Key))
            .Where(e => e?.GetTableName() != null)
            .Select(e => $"\"{e!.GetSchema() ?? "qmgr"}\".\"{e.GetTableName()}\"")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pass 1 — the catalogue. Anything, anywhere in our schema, with an OrganizationId column.
        var tables = new List<string>();
        await using (var command = _db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText =
                "SELECT table_schema, table_name FROM information_schema.columns " +
                "WHERE column_name = 'OrganizationId' AND table_schema = 'qmgr' " +
                "AND table_name IN (SELECT table_name FROM information_schema.tables WHERE table_schema = 'qmgr' AND table_type = 'BASE TABLE')";
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(cancellationToken);
            if (_db.Database.CurrentTransaction is { } catalogueTx) command.Transaction = catalogueTx.GetDbTransaction();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tables.Add($"\"{reader.GetString(0)}\".\"{reader.GetString(1)}\"");
        }

        foreach (var table in tables)
        {
            if (declaredSurvivors.Contains(table)) continue;
            var count = await ScalarAsync($"SELECT COUNT(*) FROM {table} AS t WHERE t.\"OrganizationId\" = @org", organizationId, cancellationToken);
            if (count > 0) leftovers.Add((table, count));
        }

        // Pass 2 — the model, for the derived tables that have no OrganizationId of their own.
        foreach (var step in Plan.Steps.Where(s => s.Depth > 0))
        {
            var count = await ScalarAsync($"SELECT COUNT(*) FROM {step.QualifiedTable} AS t WHERE {step.WhereClause}", organizationId, cancellationToken);
            if (count > 0) leftovers.Add((step.QualifiedTable, count));
        }

        // The organization row itself.
        var orgLeft = await ScalarAsync("SELECT COUNT(*) FROM \"qmgr\".\"organizations\" AS t WHERE t.\"Id\" = @org", organizationId, cancellationToken);
        if (orgLeft > 0) leftovers.Add(("\"qmgr\".\"organizations\"", orgLeft));

        return leftovers;
    }

    public async Task<IReadOnlyList<string>> ReVerifyRecentAsync(int sampleSize, CancellationToken cancellationToken = default)
    {
        var recent = await _db.Set<TenantPurgeCertificate>().AsNoTracking()
            .Where(c => c.VerificationPassed)
            .OrderByDescending(c => c.PurgedAt)
            .Take(Math.Max(1, sampleSize))
            .Select(c => c.OrganizationId)
            .ToListAsync(cancellationToken);

        var findings = new List<string>();
        foreach (var id in recent)
        {
            var leftovers = await FindLeftoversAsync(id, cancellationToken);
            if (leftovers.Count > 0)
                findings.Add($"{id}: {string.Join(", ", leftovers.Select(l => $"{l.Table}={l.Count}"))}");
        }

        return findings;
    }

    // ==========================================================================================
    // Files, jobs, caches
    // ==========================================================================================

    /// <summary>
    /// Every stored file name this tenant owns, gathered by asking the MODEL which tables have a
    /// file-path column rather than by naming them. A new table with a <c>PhotoUrl</c> is swept
    /// without anybody remembering it exists.
    /// </summary>
    private async Task<List<string>> CollectFileNamesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in Plan.AllTenantTables)
        {
            var entity = _db.Model.GetEntityTypes().FirstOrDefault(e => e.ClrType.Name == step.EntityName);
            if (entity == null) continue;

            var columns = entity.GetProperties()
                .Select(p => p.GetColumnName())
                .Where(c => c != null && FileColumns.Contains(c, StringComparer.Ordinal))
                .ToList();
            if (columns.Count == 0) continue;

            var selects = string.Join(", ", columns.Select(c => $"t.\"{c}\""));
            var sql = $"SELECT {selects} FROM {step.QualifiedTable} AS t WHERE {step.WhereClause}";

            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("org", organizationId));
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(cancellationToken);
            if (_db.Database.CurrentTransaction is { } fileTx) command.Transaction = fileTx.GetDbTransaction();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i)) continue;
                    var stored = StoredFileName(reader.GetString(i));
                    if (stored != null) names.Add(stored);
                }
            }
        }

        return [.. names];
    }

    /// <summary>
    /// The stored file name inside OUR upload store, or null. A tenant may have typed a link to
    /// their own website into the logo field, and deleting that is neither ours to do nor possible.
    /// </summary>
    private static string? StoredFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.Contains("/uploads/media/", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : value;
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private string? DiskPath(string fileName) =>
        _storage is Services.Storage.LocalDiskMediaStorageService local ? local.DiskPathFor(fileName) : null;

    private async Task<(int Deleted, int Failed)> DeleteFilesAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var failed = 0;
        foreach (var name in names)
        {
            try
            {
                if (await _storage.DeleteAsync(name, cancellationToken)) deleted++;
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Could not delete the stored file {FileName} during a tenant purge", name);
            }
        }
        return (deleted, failed);
    }

    /// <summary>
    /// Hangfire's own tables, in the same database, holding job arguments that carry this tenant's
    /// id — and, for a queued notification, a recipient's email address and the message body. The
    /// EF model knows nothing about any of it, so this is the one place the purge deliberately
    /// reaches outside the model.
    /// </summary>
    /// <remarks>
    /// <c>hangfire.job.arguments</c> is <c>jsonb</c>, so every match here is on <c>arguments::text</c>. Until
    /// 2026-09-23 these statements applied LIKE to the jsonb itself, which PostgreSQL refuses (42883): every purge
    /// logged an error, removed no job, and left each queued email's recipient and body in the database.
    /// </remarks>
    private async Task<int> DeleteBackgroundJobsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var pattern = $"%{organizationId}%";
            var removed = await ExecuteRawAsync(
                "DELETE FROM hangfire.jobparameter WHERE jobid IN (SELECT id FROM hangfire.job WHERE arguments::text LIKE @p)", pattern, cancellationToken);
            removed += await ExecuteRawAsync(
                "DELETE FROM hangfire.state WHERE jobid IN (SELECT id FROM hangfire.job WHERE arguments::text LIKE @p)", pattern, cancellationToken);
            removed += await ExecuteRawAsync(
                "DELETE FROM hangfire.jobqueue WHERE jobid IN (SELECT id FROM hangfire.job WHERE arguments::text LIKE @p)", pattern, cancellationToken);
            var jobs = await ExecuteRawAsync(
                "DELETE FROM hangfire.job WHERE arguments::text LIKE @p", pattern, cancellationToken);
            return jobs;
        }
        catch (Exception ex)
        {
            // A purge must not fail because Hangfire's schema is not where it was expected. Logged
            // loudly: this is personal data left behind, and somebody has to know.
            _logger.LogError(ex, "Could not remove Hangfire jobs for the purged tenant {OrganizationId} — check hangfire.job by hand", organizationId);
            return 0;
        }
    }

    private async Task<int> CountBackgroundJobsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM hangfire.job WHERE arguments::text LIKE @p";
            command.Parameters.Add(new NpgsqlParameter("p", $"%{organizationId}%"));
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(cancellationToken);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        }
        catch (Exception ex)
        {
            // -1, never 0: "the count failed" must not read as "nothing is left" (found 2026-09-23 — this query
            // applied LIKE to a jsonb column, threw on every call, and reported 0 jobs for every tenant ever
            // checked, so the suite's "no background jobs left" passed while jobs carrying emails remained).
            _logger.LogError(ex, "Could not count Hangfire jobs for tenant {OrganizationId}", organizationId);
            return -1;
        }
    }

    private async Task DropCachesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        foreach (var prefix in CachePrefixes)
        {
            try { await _cache.RemoveAsync(prefix + organizationId, cancellationToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "Cache key {Key} could not be dropped", prefix + organizationId); }
        }

        try { await _features.InvalidateCacheAsync(organizationId); }
        catch (Exception ex) { _logger.LogDebug(ex, "Feature cache for {OrganizationId} could not be dropped", organizationId); }
    }

    // ==========================================================================================

    private async Task<int> CountAsync(TenantPurgeModel.PurgeStep step, Guid organizationId, CancellationToken cancellationToken)
        => await ScalarAsync($"SELECT COUNT(*) FROM {step.QualifiedTable} AS t WHERE {step.WhereClause}", organizationId, cancellationToken);

    private async Task<int> ScalarAsync(string sql, Guid organizationId, CancellationToken cancellationToken)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("org", organizationId));
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        if (_db.Database.CurrentTransaction is { } tx) command.Transaction = tx.GetDbTransaction();
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private async Task<int> ExecuteAsync(string sql, Guid organizationId, CancellationToken cancellationToken)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("org", organizationId));
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        if (_db.Database.CurrentTransaction is { } tx) command.Transaction = tx.GetDbTransaction();
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> ExecuteRawAsync(string sql, string parameter, CancellationToken cancellationToken)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("p", parameter));
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        if (_db.Database.CurrentTransaction is { } tx) command.Transaction = tx.GetDbTransaction();
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? DomainOf(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.LastIndexOf('@');
        return at >= 0 && at < email.Length - 1 ? email[(at + 1)..].Trim().ToLowerInvariant() : null;
    }

    // The hash lives in TenantTombstoneHash: the registration guard computes it too, and a second
    // copy that drifted would silently stop matching with nothing looking broken.
}
