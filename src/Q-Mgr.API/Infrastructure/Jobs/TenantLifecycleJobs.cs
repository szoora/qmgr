using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Data.Purge;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// The clocks that move a tenant through its life, and the two sweeps that check the result.
/// </summary>
public class TenantLifecycleJobs
{
    private readonly ITenantLifecycleService _lifecycle;
    private readonly ITenantPurgeService _purge;
    private readonly QMgrDbContext _db;
    private readonly ILogger<TenantLifecycleJobs> _logger;

    public TenantLifecycleJobs(ITenantLifecycleService lifecycle, ITenantPurgeService purge, QMgrDbContext db, ILogger<TenantLifecycleJobs> logger)
    {
        _lifecycle = lifecycle;
        _purge = purge;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Advances every tenant whose clock has elapsed, and sends the warnings that fall due.
    ///
    /// This is the ONLY automatic path to a purge, and it reaches one only from PendingDeletion —
    /// which is itself 14 days after Cancelled, which is 30 days after Suspended. Nothing a
    /// customer can press gets here.
    /// </summary>
    [AutomaticRetry(Attempts = 1)]
    public async Task AdvanceAsync()
    {
        try
        {
            await _lifecycle.RunDueTransitionsAsync();
        }
        catch (Exception ex)
        {
            // Never let a single bad tenant stop the sweep for ever; tomorrow's run retries.
            _logger.LogError(ex, "The tenant lifecycle sweep failed");
        }
    }

    /// <summary>
    /// NIST SP 800-88's representative-sampling verification: re-check recent purges by a DIFFERENT
    /// route from the one that made them, so a bug in the purge cannot also hide it. Monthly, and
    /// it alarms rather than fixes — anything found here is a defect, not a chore.
    /// </summary>
    [AutomaticRetry(Attempts = 1)]
    public async Task ReVerifyAsync()
    {
        try
        {
            var findings = await _purge.ReVerifyRecentAsync(sampleSize: 10);
            if (findings.Count == 0)
            {
                _logger.LogInformation("Purge re-verification: sample clean");
                return;
            }

            _logger.LogError(
                "PURGE RE-VERIFICATION FAILED for {Count} tenant(s) — data has come back after a certified purge: {Findings}",
                findings.Count, string.Join(" | ", findings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Purge re-verification could not run");
        }
    }

    /// <summary>
    /// Financial rows kept only because a statute required it, once the statute no longer does.
    ///
    /// Uganda's Tax Procedures Code asks for five years. The rows have already been de-identified —
    /// the contact details went at purge time and only amounts, dates and references remained — so
    /// this is the last step rather than the interesting one, and it runs monthly because a day
    /// either way in year five does not matter.
    /// </summary>
    [AutomaticRetry(Attempts = 1)]
    public async Task PurgeExpiredStatutoryRecordsAsync()
    {
        try
        {
            var due = await _db.Set<Domain.Entities.Platform.TenantTombstone>()
                .Where(t => t.HadFinancialRecords && t.StatutoryRetentionUntil != null && t.StatutoryRetentionUntil <= DateTime.UtcNow)
                .ToListAsync();

            foreach (var tombstone in due)
            {
                var payments = await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM qmgr.payments WHERE \"OrganizationId\" = {0}", tombstone.OrganizationId);
                var invoices = await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM qmgr.invoices WHERE \"OrganizationId\" = {0}", tombstone.OrganizationId);

                tombstone.HadFinancialRecords = false;
                tombstone.StatutoryRetentionUntil = null;

                _logger.LogInformation(
                    "Statutory retention elapsed for purged tenant {OrganizationId}: removed {Invoices} invoice(s) and {Payments} payment(s)",
                    tombstone.OrganizationId, invoices, payments);
            }

            if (due.Count > 0) await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The statutory-retention sweep failed");
        }
    }
}

public static class TenantLifecycleJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // 02:30 UTC — before the overnight purges and backups, so a tenant that reaches its purge
        // today is gone before the night's backup is taken rather than one day after it.
        RecurringJob.AddOrUpdate<TenantLifecycleJobs>(
            "tenant-lifecycle-sweep",
            job => job.AdvanceAsync(),
            "30 2 * * *");

        // Monthly, on the 3rd at 05:00 UTC.
        RecurringJob.AddOrUpdate<TenantLifecycleJobs>(
            "purge-reverification",
            job => job.ReVerifyAsync(),
            "0 5 3 * *");

        RecurringJob.AddOrUpdate<TenantLifecycleJobs>(
            "purge-expired-statutory-records",
            job => job.PurgeExpiredStatutoryRecordsAsync(),
            "30 5 3 * *");
    }
}
