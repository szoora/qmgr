using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Daily data-minimization purge: hard-deletes visit records (and any profile left with no
/// remaining visits) once they're older than the organization's configured retention window
/// (VisitorsController.ReadRetentionSettings, default 730 days). This is a genuine hard delete,
/// not the soft-delete DeleteVisitor uses — the entire point of a retention limit is to actually
/// stop holding the PII, so a row already past its window gets purged regardless of whether
/// staff had separately soft-deleted it earlier.
/// </summary>
public class VisitorRetentionJob
{
    private readonly QMgrDbContext _context;
    private readonly ILogger<VisitorRetentionJob> _logger;

    public VisitorRetentionJob(QMgrDbContext context, ILogger<VisitorRetentionJob> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task PurgeExpiredVisitorDataAsync()
    {
        var organizations = await _context.Organizations.Select(o => new { o.Id, o.Settings }).ToListAsync();

        foreach (var org in organizations)
        {
            var retention = VisitorsController.ReadRetentionSettings(org.Settings);
            var cutoff = DateTime.UtcNow.AddDays(-retention.RetentionDays);

            // Never purge a visit that's still actively checked in, no matter how old — an
            // ancient CreatedAt on a still-open visit means someone forgot to check them out,
            // not that the record is safe to destroy; deleting it would break their live
            // checkout and silently drop them from "currently on site" counts/watchlist views.
            var expiredVisitIds = await _context.Visitors
                .Where(v => v.OrganizationId == org.Id && v.CreatedAt < cutoff && v.Status != VisitorStatus.CheckedIn)
                .Select(v => v.Id)
                .ToListAsync();

            // A run that purged nothing is still a run, and "the policy ran last night and found
            // nothing to remove" is exactly what an auditor wants to see rather than silence.
            if (expiredVisitIds.Count == 0)
            {
                await RecordEvidenceAsync(org.Id, org.Settings, retention.RetentionDays, 0, 0);
                continue;
            }

            var affectedProfileIds = await _context.Visitors
                .Where(v => expiredVisitIds.Contains(v.Id))
                .Select(v => v.VisitorProfileId)
                .Distinct()
                .ToListAsync();

            await _context.Visitors.Where(v => expiredVisitIds.Contains(v.Id)).ExecuteDeleteAsync();

            var orphanedProfileIds = await _context.VisitorProfiles
                .Where(p => affectedProfileIds.Contains(p.Id) && !_context.Visitors.Any(v => v.VisitorProfileId == p.Id))
                .Select(p => p.Id)
                .ToListAsync();

            if (orphanedProfileIds.Count > 0)
                await _context.VisitorProfiles.Where(p => orphanedProfileIds.Contains(p.Id)).ExecuteDeleteAsync();

            await RecordEvidenceAsync(org.Id, org.Settings, retention.RetentionDays, expiredVisitIds.Count, orphanedProfileIds.Count);

            _logger.LogInformation(
                "Visitor retention purge: organization {OrganizationId} — {VisitCount} visits older than {RetentionDays}d purged, {ProfileCount} orphaned profiles removed",
                org.Id, expiredVisitIds.Count, retention.RetentionDays, orphanedProfileIds.Count);
        }
    }

    /// <summary>
    /// Writes this run into Organization.Settings so the compliance report can show it. Running
    /// totals are kept alongside the last run because "we have removed 14,203 records under this
    /// policy" is the answer to a different question than "it ran at 03:00 this morning".
    ///
    /// Deliberately best-effort: a failure to record the evidence must never roll back or retry a
    /// purge that has already destroyed rows — the data is gone either way, and a retried purge is
    /// a no-op, but a job that throws here would look like the deletion failed when it did not.
    /// </summary>
    private async Task RecordEvidenceAsync(Guid organizationId, string? settingsJson, int retentionDays, int visitsPurged, int profilesPurged)
    {
        try
        {
            var previous = VisitorsController.ReadRetentionEvidence(settingsJson);
            var updated = previous with
            {
                RetentionDays = retentionDays,
                LastRunAt = DateTime.UtcNow,
                LastRunVisitsPurged = visitsPurged,
                LastRunProfilesPurged = profilesPurged,
                TotalVisitsPurged = previous.TotalVisitsPurged + visitsPurged,
                TotalProfilesPurged = previous.TotalProfilesPurged + profilesPurged
            };

            var merged = VisitorsController.WriteRetentionEvidence(settingsJson, updated);
            await _context.Organizations
                .Where(o => o.Id == organizationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(o => o.Settings, merged));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Purged visitor data for organization {OrganizationId} but could not record the retention evidence", organizationId);
        }
    }
}

public static class VisitorRetentionJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Daily at 3 AM UTC — off-peak, well clear of the badge-code generator's per-day counting
        // logic which keys off UTC midnight.
        RecurringJob.AddOrUpdate<VisitorRetentionJob>(
            "purge-expired-visitor-data",
            job => job.PurgeExpiredVisitorDataAsync(),
            "0 3 * * *");
    }
}
