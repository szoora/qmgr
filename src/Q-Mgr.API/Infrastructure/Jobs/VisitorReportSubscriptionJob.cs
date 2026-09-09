using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Sends the scheduled visitor reports branches have subscribed to.
///
/// Runs HOURLY rather than at a fixed time, because a subscription's send hour is in the BRANCH'S
/// local time and this platform's branches are not all in one timezone. Each pass asks a simple
/// question of every subscription — "is it now past your send hour, on a period you have not been
/// sent for yet?" — which makes the job idempotent: a missed hour (deploy, restart, outage) is
/// picked up by the next pass instead of being lost, and a double pass sends nothing twice.
///
/// LastSentAt is the whole mechanism. It is written back into Branch.Settings after a successful
/// send, so a failed send is retried next hour rather than silently skipped for the period.
/// </summary>
public class VisitorReportSubscriptionJob
{
    private readonly QMgrDbContext _context;
    private readonly IVisitorReportingService _reporting;
    private readonly INotificationService _notifications;
    private readonly ILogger<VisitorReportSubscriptionJob> _logger;

    public VisitorReportSubscriptionJob(
        QMgrDbContext context,
        IVisitorReportingService reporting,
        INotificationService notifications,
        ILogger<VisitorReportSubscriptionJob> logger)
    {
        _context = context;
        _reporting = reporting;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task SendDueReportsAsync()
    {
        // Branch.Settings is a jsonb column. Filtering it with string.Contains() translated to a
        // SQL LIKE, which Postgres refuses outright — "operator does not exist: jsonb ~~ unknown".
        // The settings blob is parsed in memory anyway, and a tenant has a handful of branches, so
        // narrowing to "has any settings at all" in SQL and testing for the key here is both
        // correct and cheap. Found by running the job, not by the compiler.
        var branches = (await _context.Branches
                .Where(b => b.IsActive && b.Settings != null)
                .Select(b => new { b.Id, b.Name, b.Timezone, b.OrganizationId, b.Settings })
                .ToListAsync())
            .Where(b => b.Settings!.Contains("VisitorReporting", StringComparison.Ordinal))
            .ToList();

        foreach (var branch in branches)
        {
            VisitorReportingSettingsDto settings;
            try
            {
                settings = VisitorsController.ReadReportingSettings(branch.Settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read reporting settings for branch {BranchId}", branch.Id);
                continue;
            }

            var due = settings.Subscriptions.Where(sub => sub.Enabled && IsDue(sub, branch.Timezone)).ToList();
            if (due.Count == 0) continue;

            var scope = new VisitorReportScope(branch.OrganizationId, branch.Id, branch.Name, branch.Timezone);
            var anySent = false;

            foreach (var subscription in due)
            {
                try
                {
                    var sent = await SendAsync(scope, settings, subscription, branch.OrganizationId);
                    if (sent)
                    {
                        subscription.LastSentAt = DateTime.UtcNow;
                        anySent = true;
                    }
                }
                catch (Exception ex)
                {
                    // One bad subscription must not stop the others. LastSentAt is deliberately not
                    // advanced, so the next hourly pass tries this one again.
                    _logger.LogError(ex, "Scheduled {Kind} report failed for branch {BranchId}", subscription.Kind, branch.Id);
                }
            }

            if (!anySent) continue;

            var merged = VisitorsController.WriteReportingSettings(branch.Settings, settings);
            await _context.Branches.Where(b => b.Id == branch.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(b => b.Settings, merged));
        }
    }

    /// <summary>
    /// Whether this subscription's period has come round and it has not already been sent for it.
    /// The comparison is on the local calendar period, not on elapsed hours — "weekly" means a new
    /// week has started, not that 168 hours have passed since the last send.
    /// </summary>
    private static bool IsDue(ReportSubscriptionDto subscription, string timeZoneId)
    {
        var zone = SafeZone(timeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);

        if (nowLocal.Hour < subscription.SendAtHour) return false;
        if (subscription.LastSentAt == null) return true;

        var lastLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(subscription.LastSentAt.Value, DateTimeKind.Utc), zone);

        return subscription.Cadence switch
        {
            ReportSubscriptionCadence.Daily => lastLocal.Date < nowLocal.Date,
            // Monday-start weeks: a Sunday send and the following Monday send belong to different
            // weeks, which is what "weekly" means to the person reading it.
            ReportSubscriptionCadence.Weekly => StartOfWeek(lastLocal) < StartOfWeek(nowLocal),
            ReportSubscriptionCadence.Monthly => lastLocal.Year < nowLocal.Year || lastLocal.Month < nowLocal.Month,
            _ => false
        };
    }

    private static DateTime StartOfWeek(DateTime value)
    {
        var offset = ((int)value.DayOfWeek + 6) % 7; // Monday = 0
        return value.Date.AddDays(-offset);
    }

    private static TimeZoneInfo SafeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// The range each cadence reports on: yesterday for a daily digest, last week for a weekly one,
    /// last month for a monthly one. Deliberately the COMPLETED period rather than a rolling window
    /// — a Monday email covering "the last seven days" overlaps the previous one and double-counts
    /// in anyone's head.
    ///
    /// Exceptions are the exception: they report on today, because their whole purpose is telling
    /// somebody about a person who is on site right now.
    /// </summary>
    private static (DateOnly From, DateOnly To) RangeFor(ReportSubscriptionDto subscription, DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);

        if (subscription.Kind == ReportSubscriptionKind.Exceptions)
            return (today, today);

        return subscription.Cadence switch
        {
            ReportSubscriptionCadence.Daily => (today, today),
            ReportSubscriptionCadence.Weekly => (DateOnly.FromDateTime(StartOfWeek(nowLocal).AddDays(-7)), DateOnly.FromDateTime(StartOfWeek(nowLocal).AddDays(-1))),
            ReportSubscriptionCadence.Monthly => (
                new DateOnly(nowLocal.Year, nowLocal.Month, 1).AddMonths(-1),
                new DateOnly(nowLocal.Year, nowLocal.Month, 1).AddDays(-1)),
            _ => (today, today)
        };
    }

    private async Task<bool> SendAsync(VisitorReportScope scope, VisitorReportingSettingsDto settings, ReportSubscriptionDto subscription, Guid organizationId)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SafeZone(scope.TimeZoneId));
        var (from, to) = RangeFor(subscription, nowLocal);
        var filter = new VisitorReportFilter { From = from, To = to };

        string subject;
        string html;

        switch (subscription.Kind)
        {
            case ReportSubscriptionKind.Exceptions:
                var exceptions = await _reporting.BuildExceptionsAsync(scope, filter, settings);
                subject = exceptions.TotalCount == 0
                    ? $"Visitors: all clear — {scope.Name}"
                    : $"Visitors: {exceptions.TotalCount} item(s) need attention — {scope.Name}";
                html = VisitorReportEmail.RenderExceptions(exceptions);
                break;

            case ReportSubscriptionKind.Compliance:
                var orgSettings = await _context.Organizations
                    .Where(o => o.Id == organizationId).Select(o => o.Settings).FirstOrDefaultAsync();
                var branchSettings = await _context.Branches
                    .Where(b => b.Id == scope.BranchId).Select(b => b.Settings).FirstOrDefaultAsync();

                var compliance = await _reporting.BuildComplianceAsync(
                    scope, filter,
                    VisitorsController.ReadConsentSettings(branchSettings).Required,
                    VisitorsController.ReadRetentionSettings(orgSettings),
                    VisitorsController.ReadRetentionEvidence(orgSettings));

                subject = $"Visitor compliance — {scope.Name} — {from:d MMM} to {to:d MMM}";
                html = VisitorReportEmail.RenderCompliance(compliance);
                break;

            case ReportSubscriptionKind.VisitorLog:
                var logReport = await _reporting.BuildReportAsync(scope, filter);
                var csv = await _reporting.BuildLogCsvAsync(scope, filter);
                subject = $"Visitor log — {scope.Name} — {from:d MMM} to {to:d MMM}";
                html = VisitorReportEmail.RenderVisitorLog(logReport, csv);
                break;

            default:
                var summary = await _reporting.BuildReportAsync(scope, filter);
                subject = $"Visitor summary — {scope.Name} — {from:d MMM} to {to:d MMM}";
                html = VisitorReportEmail.RenderSummary(summary);
                break;
        }

        var recipients = subscription.RecipientList().ToList();
        var delivered = 0;

        foreach (var recipient in recipients)
        {
            if (await _notifications.SendEmailAsync(organizationId, recipient, subject, html, isHtml: true))
                delivered++;
            else
                _logger.LogWarning("Scheduled {Kind} report could not be delivered to {Recipient} for branch {BranchId}",
                    subscription.Kind, recipient, scope.BranchId);
        }

        // Counted as sent if it reached anybody. Holding the whole subscription back because one
        // address of five bounced would re-send to the other four every hour.
        if (delivered > 0)
            _logger.LogInformation("Sent scheduled {Kind} report for branch {BranchId} to {Delivered}/{Total} recipient(s)",
                subscription.Kind, scope.BranchId, delivered, recipients.Count);

        return delivered > 0;
    }
}

public static class VisitorReportSubscriptionJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Hourly on the hour. Each branch's own send hour is evaluated in its own timezone inside
        // the job — a single daily trigger could only ever be right for one timezone.
        RecurringJob.AddOrUpdate<VisitorReportSubscriptionJob>(
            "send-visitor-report-subscriptions",
            job => job.SendDueReportsAsync(),
            "0 * * * *");
    }
}
