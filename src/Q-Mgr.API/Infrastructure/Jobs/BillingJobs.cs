using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Background jobs for billing automation and tenant management
/// </summary>
public class BillingJobs
{
    private readonly QMgrDbContext _dbContext;
    private readonly IBillingService _billingService;
    private readonly IUsageTrackingService _usageTrackingService;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly INotificationService _notificationService;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly ILogger<BillingJobs> _logger;

    public BillingJobs(
        QMgrDbContext dbContext,
        IBillingService billingService,
        IUsageTrackingService usageTrackingService,
        ITenantProvisioningService provisioningService,
        INotificationService notificationService,
        IPlatformSettingsService platformSettingsService,
        ILogger<BillingJobs> logger)
    {
        _dbContext = dbContext;
        _billingService = billingService;
        _usageTrackingService = usageTrackingService;
        _provisioningService = provisioningService;
        _notificationService = notificationService;
        _platformSettingsService = platformSettingsService;
        _logger = logger;
    }

    /// <summary>
    /// These billing emails previously hardcoded "https://{slug}.qmgr.app/..." links, independent
    /// of PlatformSettings.SaaS.BaseDomain — the same fact asserted in two places, and the one
    /// that actually drifted when the platform's real domain changed. Resolved once per job run
    /// (GetSettingsAsync is memory-cached) rather than baked into the template strings.
    /// </summary>
    private async Task<string> GetBaseDomainAsync()
    {
        var saas = await _platformSettingsService.GetSettingsAsync<SaasSettings>("SaaS");
        return saas?.BaseDomain ?? "";
    }

    /// <summary>
    /// Check for trials expiring soon and send reminder emails
    /// Runs daily at 9 AM
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task CheckExpiringTrialsAsync()
    {
        _logger.LogInformation("Starting check for expiring trials");

        var baseDomain = await GetBaseDomainAsync();
        var now = DateTime.UtcNow;
        var warningThreshold = now.AddDays(3); // Warn 3 days before expiry

        // Find trials expiring within 3 days
        var expiringTrials = await _dbContext.Organizations
            .Where(o => o.Status == TenantStatus.Trialing &&
                        o.TrialEndsAt != null &&
                        o.TrialEndsAt <= warningThreshold &&
                        o.TrialEndsAt > now)
            .ToListAsync();

        foreach (var org in expiringTrials)
        {
            try
            {
                var daysLeft = (org.TrialEndsAt!.Value - now).Days;

                // Send reminder email
                await _notificationService.SendEmailAsync(
                    org.Id,
                    org.EffectiveBillingEmail,
                    $"Your Q-Mgr trial expires in {daysLeft} days",
                    GetTrialExpiringEmailBody(org.Name, daysLeft, org.Slug, baseDomain),
                    true);

                _logger.LogInformation(
                    "Sent trial expiring reminder to organization {OrganizationId}, {DaysLeft} days left",
                    org.Id, daysLeft);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send trial expiring reminder to {OrganizationId}", org.Id);
            }
        }

        // Find and expire trials that have ended
        var expiredTrials = await _dbContext.Organizations
            .Where(o => o.Status == TenantStatus.Trialing &&
                        o.TrialEndsAt != null &&
                        o.TrialEndsAt <= now)
            .ToListAsync();

        foreach (var org in expiredTrials)
        {
            try
            {
                // Check if they have a subscription
                if (org.SubscriptionId.HasValue)
                {
                    org.Status = TenantStatus.Active;
                    _logger.LogInformation("Trial ended, activated subscription for {OrganizationId}", org.Id);
                }
                else
                {
                    // Downgrade to suspended (they need to subscribe)
                    org.Status = TenantStatus.Suspended;
                    _logger.LogInformation("Trial expired for {OrganizationId}, suspended account", org.Id);

                    // Send trial expired email
                    await _notificationService.SendEmailAsync(
                        org.Id,
                        org.EffectiveBillingEmail,
                        "Your Q-Mgr trial has ended",
                        GetTrialExpiredEmailBody(org.Name, org.Slug, baseDomain),
                        true);
                }

                _dbContext.Organizations.Update(org);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process expired trial for {OrganizationId}", org.Id);
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Completed expiring trials check. Warned: {Warned}, Expired: {Expired}",
            expiringTrials.Count, expiredTrials.Count);
    }

    /// <summary>
    /// Aggregate usage metrics for all organizations
    /// Runs hourly
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task AggregateUsageMetricsAsync()
    {
        _logger.LogInformation("Starting usage metrics aggregation");

        var activeOrgs = await _dbContext.Organizations
            .Where(o => o.Status == TenantStatus.Active || o.Status == TenantStatus.Trialing)
            .Select(o => o.Id)
            .ToListAsync();

        var aggregated = 0;
        foreach (var orgId in activeOrgs)
        {
            try
            {
                await _usageTrackingService.AggregateUsageAsync(orgId);
                aggregated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to aggregate usage for organization {OrganizationId}", orgId);
            }
        }

        _logger.LogInformation("Completed usage aggregation for {Count} organizations", aggregated);
    }

    /// <summary>
    /// Check usage limits and send warnings when approaching limits
    /// Runs daily
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task CheckUsageLimitsAsync()
    {
        _logger.LogInformation("Starting usage limits check");

        var activeOrgs = await _dbContext.Organizations
            .Where(o => o.Status == TenantStatus.Active || o.Status == TenantStatus.Trialing)
            .ToListAsync();

        foreach (var org in activeOrgs)
        {
            try
            {
                var usage = await _usageTrackingService.GetCurrentUsageAsync(org.Id);
                var limits = await _billingService.GetEffectiveLimitsAsync(org.Id);

                // Check token usage (warn at 80%)
                if (limits.MaxTokensPerMonth > 0)
                {
                    var tokenPercentage = (double)usage.TokensCreated / limits.MaxTokensPerMonth * 100;
                    if (tokenPercentage >= 80 && tokenPercentage < 100)
                    {
                        await SendUsageWarningAsync(org, "tokens", usage.TokensCreated, limits.MaxTokensPerMonth);
                    }
                    else if (tokenPercentage >= 100)
                    {
                        await SendLimitExceededAsync(org, "tokens", usage.TokensCreated, limits.MaxTokensPerMonth);
                    }
                }

                // Check API calls (warn at 80%)
                if (limits.MaxApiCallsPerMonth > 0)
                {
                    var apiPercentage = (double)usage.ApiCalls / limits.MaxApiCallsPerMonth * 100;
                    if (apiPercentage >= 80 && apiPercentage < 100)
                    {
                        await SendUsageWarningAsync(org, "API calls", usage.ApiCalls, limits.MaxApiCallsPerMonth);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check usage limits for organization {OrganizationId}", org.Id);
            }
        }

        _logger.LogInformation("Completed usage limits check");
    }

    /// <summary>
    /// Generate monthly invoices for all active subscriptions
    /// Runs on the 1st of each month
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task GenerateMonthlyInvoicesAsync()
    {
        _logger.LogInformation("Starting monthly invoice generation");

        var subscriptions = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .Include(s => s.Organization)
            .Where(s => s.Status == SubscriptionStatus.Active &&
                        s.CurrentPeriodEnd <= DateTime.UtcNow)
            .ToListAsync();

        var generated = 0;
        foreach (var subscription in subscriptions)
        {
            try
            {
                await _billingService.GenerateInvoiceAsync(subscription.Id);
                generated++;

                _logger.LogInformation(
                    "Generated invoice for subscription {SubscriptionId}, organization {OrganizationId}",
                    subscription.Id, subscription.OrganizationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to generate invoice for subscription {SubscriptionId}",
                    subscription.Id);
            }
        }

        _logger.LogInformation("Completed invoice generation. Generated: {Count}", generated);
    }

    /// <summary>
    /// Process pending invoices and attempt collection
    /// Runs daily
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task ProcessPendingInvoicesAsync()
    {
        _logger.LogInformation("Starting pending invoice processing");

        var baseDomain = await GetBaseDomainAsync();
        var pendingInvoices = await _dbContext.Invoices
            .Include(i => i.Subscription)
                .ThenInclude(s => s!.Organization)
            .Where(i => i.Status == InvoiceStatus.Open &&
                        i.DueDate <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var invoice in pendingInvoices)
        {
            try
            {
                // Attempt to collect payment
                var result = await _billingService.CollectPaymentAsync(invoice.Id);

                if (result.Success)
                {
                    _logger.LogInformation("Successfully collected payment for invoice {InvoiceId}", invoice.Id);
                }
                else
                {
                    // Payment failed - update subscription status
                    if (invoice.Subscription != null)
                    {
                        invoice.Subscription.Status = SubscriptionStatus.PastDue;
                        _dbContext.Subscriptions.Update(invoice.Subscription);

                        // Send payment failed notification
                        if (invoice.Subscription.Organization != null)
                        {
                            await _notificationService.SendEmailAsync(
                                invoice.Subscription.Organization.Id,
                                invoice.Subscription.Organization.EffectiveBillingEmail,
                                "Payment failed for your Q-Mgr subscription",
                                GetPaymentFailedEmailBody(
                                    invoice.Subscription.Organization.Name,
                                    invoice.Total,
                                    invoice.Subscription.Organization.Slug,
                                    baseDomain),
                                true);
                        }
                    }

                    _logger.LogWarning("Payment collection failed for invoice {InvoiceId}", invoice.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing invoice {InvoiceId}", invoice.Id);
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Completed pending invoice processing. Processed: {Count}", pendingInvoices.Count);
    }

    /// <summary>
    /// Suspend accounts with overdue payments (after grace period)
    /// Runs daily
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task SuspendOverdueAccountsAsync()
    {
        _logger.LogInformation("Starting overdue accounts suspension check");

        var baseDomain = await GetBaseDomainAsync();
        var gracePeriodDays = 7;
        var cutoffDate = DateTime.UtcNow.AddDays(-gracePeriodDays);

        var overdueSubscriptions = await _dbContext.Subscriptions
            .Include(s => s.Organization)
            .Where(s => s.Status == SubscriptionStatus.PastDue &&
                        s.UpdatedAt <= cutoffDate)
            .ToListAsync();

        foreach (var subscription in overdueSubscriptions)
        {
            try
            {
                subscription.Status = SubscriptionStatus.Suspended;

                if (subscription.Organization != null)
                {
                    subscription.Organization.Status = TenantStatus.Suspended;
                    _dbContext.Organizations.Update(subscription.Organization);

                    // Send suspension notification
                    await _notificationService.SendEmailAsync(
                        subscription.Organization.Id,
                        subscription.Organization.EffectiveBillingEmail,
                        "Your Q-Mgr account has been suspended",
                        GetAccountSuspendedEmailBody(
                            subscription.Organization.Name,
                            subscription.Organization.Slug,
                            baseDomain),
                        true);

                    _logger.LogWarning(
                        "Suspended organization {OrganizationId} due to overdue payment",
                        subscription.OrganizationId);
                }

                _dbContext.Subscriptions.Update(subscription);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error suspending subscription {SubscriptionId}",
                    subscription.Id);
            }
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Completed overdue accounts check. Suspended: {Count}", overdueSubscriptions.Count);
    }

    /// <summary>
    /// Clean up old verification tokens and temporary data
    /// Runs daily
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task CleanupExpiredDataAsync()
    {
        _logger.LogInformation("Starting expired data cleanup");

        // Clean up old notifications (older than 90 days)
        await _notificationService.CleanupOldNotificationsAsync(90);

        // Clean up deleted organizations (older than 30 days)
        var deletionCutoff = DateTime.UtcNow.AddDays(-30);
        var deletedOrgs = await _dbContext.Organizations
            .Where(o => o.Status == TenantStatus.Deleted &&
                        o.UpdatedAt <= deletionCutoff)
            .ToListAsync();

        foreach (var org in deletedOrgs)
        {
            // Permanently remove (or archive to cold storage)
            _dbContext.Organizations.Remove(org);
            _logger.LogInformation("Permanently removed deleted organization {OrganizationId}", org.Id);
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Completed expired data cleanup");
    }

    /// <summary>
    /// Reset monthly usage counters on the 1st of each month
    /// Runs on the 1st of each month at midnight
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task ResetMonthlyUsageCountersAsync()
    {
        _logger.LogInformation("Starting monthly usage counter reset");

        var activeOrgs = await _dbContext.Organizations
            .Where(o => o.Status == TenantStatus.Active || o.Status == TenantStatus.Trialing)
            .Select(o => o.Id)
            .ToListAsync();

        foreach (var orgId in activeOrgs)
        {
            try
            {
                await _usageTrackingService.ResetMonthlyCountersAsync(orgId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset usage counters for organization {OrganizationId}", orgId);
            }
        }

        _logger.LogInformation("Completed monthly usage counter reset for {Count} organizations", activeOrgs.Count);
    }

    #region Email Templates

    private static string GetTrialExpiringEmailBody(string orgName, int daysLeft, string slug, string baseDomain)
    {
        return $@"
<!DOCTYPE html>
<html>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #2563eb;'>Your trial is ending soon</h1>
        <p>Hi,</p>
        <p>Your Q-Mgr trial for <strong>{orgName}</strong> will expire in <strong>{daysLeft} days</strong>.</p>
        <p>To continue using Q-Mgr without interruption, please subscribe to a plan:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='https://{slug}.{baseDomain}/billing/plans' style='background-color: #2563eb; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px;'>Choose a Plan</a>
        </div>
        <p>If you have any questions, our support team is here to help.</p>
        <p>Best regards,<br>The Q-Mgr Team</p>
    </div>
</body>
</html>";
    }

    private static string GetTrialExpiredEmailBody(string orgName, string slug, string baseDomain)
    {
        return $@"
<!DOCTYPE html>
<html>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #dc2626;'>Your trial has ended</h1>
        <p>Hi,</p>
        <p>Your Q-Mgr trial for <strong>{orgName}</strong> has expired.</p>
        <p>Your account has been temporarily suspended. To restore access, please subscribe to a plan:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='https://{slug}.{baseDomain}/billing/plans' style='background-color: #2563eb; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px;'>Subscribe Now</a>
        </div>
        <p>Your data is safe and will be available once you subscribe.</p>
        <p>Best regards,<br>The Q-Mgr Team</p>
    </div>
</body>
</html>";
    }

    private static string GetPaymentFailedEmailBody(string orgName, decimal amount, string slug, string baseDomain)
    {
        return $@"
<!DOCTYPE html>
<html>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #dc2626;'>Payment failed</h1>
        <p>Hi,</p>
        <p>We were unable to process your payment of <strong>${amount:F2}</strong> for <strong>{orgName}</strong>.</p>
        <p>Please update your payment method to avoid service interruption:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='https://{slug}.{baseDomain}/billing/payment' style='background-color: #2563eb; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px;'>Update Payment Method</a>
        </div>
        <p>If you believe this is an error, please contact our support team.</p>
        <p>Best regards,<br>The Q-Mgr Team</p>
    </div>
</body>
</html>";
    }

    private static string GetAccountSuspendedEmailBody(string orgName, string slug, string baseDomain)
    {
        return $@"
<!DOCTYPE html>
<html>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #dc2626;'>Account suspended</h1>
        <p>Hi,</p>
        <p>Your Q-Mgr account for <strong>{orgName}</strong> has been suspended due to payment issues.</p>
        <p>To restore your account, please update your payment method and clear any outstanding balance:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='https://{slug}.{baseDomain}/billing/payment' style='background-color: #2563eb; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px;'>Restore Account</a>
        </div>
        <p>Your data is being preserved and will be available once payment is received.</p>
        <p>Best regards,<br>The Q-Mgr Team</p>
    </div>
</body>
</html>";
    }

    #endregion

    #region Helper Methods

    private async Task SendUsageWarningAsync(
        Domain.Entities.Organization.Organization org,
        string resourceType,
        int current,
        int limit)
    {
        var percentage = (int)((double)current / limit * 100);
        var baseDomain = await GetBaseDomainAsync();

        await _notificationService.SendEmailAsync(
            org.Id,
            org.EffectiveBillingEmail,
            $"Usage warning: {percentage}% of {resourceType} limit used",
            $@"
<!DOCTYPE html>
<html>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #f59e0b;'>Usage Warning</h1>
        <p>Hi,</p>
        <p>Your organization <strong>{org.Name}</strong> has used <strong>{percentage}%</strong> of your monthly {resourceType} limit.</p>
        <p>Current usage: <strong>{current:N0}</strong> / <strong>{limit:N0}</strong></p>
        <p>To avoid service interruption, consider upgrading your plan:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='https://{org.Slug}.{baseDomain}/billing/plans' style='background-color: #2563eb; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px;'>Upgrade Plan</a>
        </div>
        <p>Best regards,<br>The Q-Mgr Team</p>
    </div>
</body>
</html>",
            true);

        _logger.LogInformation(
            "Sent usage warning to {OrganizationId}: {ResourceType} at {Percentage}%",
            org.Id, resourceType, percentage);
    }

    private async Task SendLimitExceededAsync(
        Domain.Entities.Organization.Organization org,
        string resourceType,
        int current,
        int limit)
    {
        var baseDomain = await GetBaseDomainAsync();

        await _notificationService.SendEmailAsync(
            org.Id,
            org.EffectiveBillingEmail,
            $"Limit exceeded: {resourceType}",
            $@"
<!DOCTYPE html>
<html>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #dc2626;'>Limit Exceeded</h1>
        <p>Hi,</p>
        <p>Your organization <strong>{org.Name}</strong> has exceeded your monthly {resourceType} limit.</p>
        <p>Current usage: <strong>{current:N0}</strong> / <strong>{limit:N0}</strong></p>
        <p>Some features may be restricted until your usage resets next month or you upgrade your plan:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='https://{org.Slug}.{baseDomain}/billing/plans' style='background-color: #dc2626; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px;'>Upgrade Now</a>
        </div>
        <p>Best regards,<br>The Q-Mgr Team</p>
    </div>
</body>
</html>",
            true);

        _logger.LogWarning(
            "Limit exceeded for {OrganizationId}: {ResourceType} - {Current}/{Limit}",
            org.Id, resourceType, current, limit);
    }

    #endregion
}

/// <summary>
/// Static class for registering recurring Hangfire jobs
/// </summary>
public static class BillingJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Check expiring trials - Daily at 9 AM UTC
        RecurringJob.AddOrUpdate<BillingJobs>(
            "check-expiring-trials",
            job => job.CheckExpiringTrialsAsync(),
            "0 9 * * *");

        // Aggregate usage metrics - Every hour
        RecurringJob.AddOrUpdate<BillingJobs>(
            "aggregate-usage-metrics",
            job => job.AggregateUsageMetricsAsync(),
            Cron.Hourly);

        // Check usage limits - Daily at 10 AM UTC
        RecurringJob.AddOrUpdate<BillingJobs>(
            "check-usage-limits",
            job => job.CheckUsageLimitsAsync(),
            "0 10 * * *");

        // Generate monthly invoices - 1st of each month at midnight
        RecurringJob.AddOrUpdate<BillingJobs>(
            "generate-monthly-invoices",
            job => job.GenerateMonthlyInvoicesAsync(),
            "0 0 1 * *");

        // Process pending invoices - Daily at 6 AM UTC
        RecurringJob.AddOrUpdate<BillingJobs>(
            "process-pending-invoices",
            job => job.ProcessPendingInvoicesAsync(),
            "0 6 * * *");

        // Suspend overdue accounts - Daily at 7 AM UTC
        RecurringJob.AddOrUpdate<BillingJobs>(
            "suspend-overdue-accounts",
            job => job.SuspendOverdueAccountsAsync(),
            "0 7 * * *");

        // Cleanup expired data - Daily at 2 AM UTC
        RecurringJob.AddOrUpdate<BillingJobs>(
            "cleanup-expired-data",
            job => job.CleanupExpiredDataAsync(),
            "0 2 * * *");

        // Reset monthly usage counters - 1st of each month at midnight
        RecurringJob.AddOrUpdate<BillingJobs>(
            "reset-monthly-usage",
            job => job.ResetMonthlyUsageCountersAsync(),
            "0 0 1 * *");
    }
}
