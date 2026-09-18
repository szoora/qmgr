using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Content;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;
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
    private readonly INotificationService _notifications;
    private readonly ILogger<DocumentShareRetentionJob> _logger;

    public DocumentShareRetentionJob(
        QMgrDbContext context,
        IDocumentShareService shares,
        INotificationService notifications,
        ILogger<DocumentShareRetentionJob> logger)
    {
        _context = context;
        _shares = shares;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task PurgeExpiredAttributionAsync()
    {
        var organizations = await _context.Organizations.AsNoTracking().Select(o => new { o.Id, o.Settings }).ToListAsync();

        var total = 0;
        foreach (var org in organizations)
        {
            var policy = _shares.ReadPolicy(org.Settings);

            // One window per classification, not one per tenant (plan §14 finding 5). A record of who
            // read a document about a named person is the evidence for Uganda s.24(1)(c) and reg.
            // 39(7), and it outlives the link; a foyer poster's access log does not need the same
            // life. EffectiveFor falls back to the tenant window wherever a classification has no
            // rule of its own, so a tenant that has classified nothing behaves exactly as before.
            foreach (var classification in Enum.GetValues<DocumentClassification>())
            {
                var days = Math.Max(1, policy.EffectiveFor(classification).AttributionRetentionDays ?? policy.AttributionRetentionDays);
                var cutoff = DateTime.UtcNow.AddDays(-days);

                // IgnoreQueryFilters: a job has no tenant. The organization is the join, explicitly.
                var updated = await _context.DocumentShareEvents
                    .IgnoreQueryFilters()
                    .Where(e => e.CreatedAt < cutoff
                                && e.Share!.MediaContent!.OrganizationId == org.Id
                                && e.Share.MediaContent.Classification == classification
                                && (e.Email != null || e.IpAddress != null || e.UserAgent != null))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Email, (string?)null)
                        .SetProperty(e => e.IpAddress, (string?)null)
                        .SetProperty(e => e.UserAgent, (string?)null));

                if (updated > 0)
                    _logger.LogInformation("Share-link attribution purge: organization {OrganizationId}, {Classification} — {Count} event(s) older than {Days}d anonymised", org.Id, classification, updated, days);
                total += updated;
            }
        }

        _logger.LogInformation("Share-link attribution purge complete: {Total} event(s) anonymised", total);
    }

    /// <summary>
    /// Tells the person who issued a link that it is about to stop working, once, seven days ahead.
    /// Box does the same (notify item owners a configurable period before expiry, default seven days)
    /// and for the same reason: a link that dies silently is discovered by the person on the other
    /// end of it, and becomes a support call rather than a renewal.
    ///
    /// <para>The claim is <see cref="DocumentShare.ExpiryWarningSentAt"/>, written with the send, so
    /// a second run the same week warns nobody twice. Only a link that is actually usable is worth
    /// warning about, so the state has to be Active — a revoked or already-expired link is not news.</para>
    /// </summary>
    public async Task WarnBeforeExpiryAsync()
    {
        var now = DateTime.UtcNow;
        var horizon = now.AddDays(WarnDaysAhead);

        var due = await _context.DocumentShares
            .IgnoreQueryFilters()
            .Include(s => s.MediaContent)
            .Where(s => s.ExpiryWarningSentAt == null
                        && s.CreatedBy != null
                        && s.ExpiresAt != null && s.ExpiresAt > now && s.ExpiresAt <= horizon)
            .ToListAsync();

        var sent = 0;
        foreach (var share in due)
        {
            try
            {
                // Fail closed on anything that is not genuinely live: revoked, locked, view-limited,
                // or whose document has been un-shared or retired. EvaluateState is the one rule.
                if (DocumentShareService.EvaluateStateOf(share, now) != DocumentShareState.Active) continue;

                var doc = share.MediaContent!;
                var creator = await _context.Users.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.Id == share.CreatedBy!.Value)
                    .Select(u => new { u.Email })
                    .FirstOrDefaultAsync();

                var label = string.IsNullOrWhiteSpace(share.Label) ? "" : $" ({share.Label})";
                var when = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{share.ExpiresAt!.Value:dd MMM yyyy}");

                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = share.CreatedBy,
                    OrganizationId = doc.OrganizationId,
                    EventKey = NotificationEventKeys.DocumentShareExpiring,
                    Title = "A shared link is about to expire",
                    Message = $"The link to \"{doc.Name}\"{label} stops working on {when}.",
                    Type = NotificationType.Custom,
                    IconClass = "bi-hourglass-split",
                    ActionUrl = $"/content/documents?document={doc.Id}",
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    Email = creator?.Email,
                    EmailSubject = $"Q-Mgr: the link to \"{doc.Name}\" expires on {when}"
                });

                // Claimed after the send, not before: a link nobody was told about is better warned
                // late than never, and this job is idempotent by the timestamp either way.
                share.ExpiryWarningSentAt = now;
                await _context.SaveChangesAsync();
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Expiry warning for share {ShareId} failed", share.Id);
            }
        }

        if (sent > 0) _logger.LogInformation("Share-link expiry warnings: {Count} sent", sent);
    }

    /// <summary>Box's default, and a working week's notice.</summary>
    private const int WarnDaysAhead = 7;
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

        // 07:00 UTC — a warning is only useful in somebody's working day, not at 3am.
        RecurringJob.AddOrUpdate<DocumentShareRetentionJob>(
            "warn-document-share-expiry",
            job => job.WarnBeforeExpiryAsync(),
            "0 7 * * *");
    }
}
