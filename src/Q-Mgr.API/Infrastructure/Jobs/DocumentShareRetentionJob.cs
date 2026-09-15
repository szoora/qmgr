using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Blanks the attribution columns (viewer email, truncated address, browser) on share-link events
/// older than each organization's retention window, keeping the event row itself. The always-kept
/// tier — what happened, when, to which link, with what outcome — is not personal data; the
/// attribution tier is, and an access log holding it for ever "just in case" is a liability, not
/// diligence. See docs/plans/SECURE_DOCUMENT_SHARING.md §8.
///
/// Hangfire on PostgreSQL storage, daily. Same shape as VisitorRetentionJob.
/// </summary>
public class DocumentShareRetentionJob
{
    private readonly QMgrDbContext _context;
    private readonly IDocumentShareService _shares;
    private readonly ILogger<DocumentShareRetentionJob> _logger;

    public DocumentShareRetentionJob(QMgrDbContext context, IDocumentShareService shares, ILogger<DocumentShareRetentionJob> logger)
    {
        _context = context;
        _shares = shares;
        _logger = logger;
    }

    public async Task PurgeExpiredAttributionAsync()
    {
        var organizations = await _context.Organizations.AsNoTracking().Select(o => new { o.Id, o.Settings }).ToListAsync();

        var total = 0;
        foreach (var org in organizations)
        {
            var policy = _shares.ReadPolicy(org.Settings);
            var days = Math.Max(1, policy.AttributionRetentionDays);
            var cutoff = DateTime.UtcNow.AddDays(-days);

            // IgnoreQueryFilters: a job has no tenant. The organization is the join, explicitly.
            var updated = await _context.DocumentShareEvents
                .IgnoreQueryFilters()
                .Where(e => e.CreatedAt < cutoff
                            && e.Share!.MediaContent!.OrganizationId == org.Id
                            && (e.Email != null || e.IpAddress != null || e.UserAgent != null))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Email, (string?)null)
                    .SetProperty(e => e.IpAddress, (string?)null)
                    .SetProperty(e => e.UserAgent, (string?)null));

            if (updated > 0)
                _logger.LogInformation("Share-link attribution purge: organization {OrganizationId} — {Count} event(s) older than {Days}d anonymised", org.Id, updated, days);
            total += updated;
        }

        _logger.LogInformation("Share-link attribution purge complete: {Total} event(s) anonymised", total);
    }
}

public static class DocumentShareJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // 03:30 UTC, after the visitor purge at 03:00.
        RecurringJob.AddOrUpdate<DocumentShareRetentionJob>(
            "purge-document-share-attribution",
            job => job.PurgeExpiredAttributionAsync(),
            "30 3 * * *");
    }
}
