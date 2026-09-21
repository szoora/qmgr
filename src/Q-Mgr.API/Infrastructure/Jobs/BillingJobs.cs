using QMgr.Domain.Constants;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Platform;
using QMgr.Infrastructure.Email;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services.Billing;

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
    private readonly IModuleAccessService _moduleAccessService;
    private readonly ILogger<BillingJobs> _logger;

    public BillingJobs(
        QMgrDbContext dbContext,
        IBillingService billingService,
        IUsageTrackingService usageTrackingService,
        ITenantProvisioningService provisioningService,
        INotificationService notificationService,
        IPlatformSettingsService platformSettingsService,
        IModuleAccessService moduleAccessService,
        ILogger<BillingJobs> logger)
    {
        _dbContext = dbContext;
        _billingService = billingService;
        _usageTrackingService = usageTrackingService;
        _provisioningService = provisioningService;
        _notificationService = notificationService;
        _platformSettingsService = platformSettingsService;
        _moduleAccessService = moduleAccessService;
        _logger = logger;
    }

    /// <summary>
    /// These billing emails previously hardcoded "https://{slug}.qmgr.app/..." links, independent
    /// of PlatformSettings.SaaS.BaseDomain — the same fact asserted in two places, and the one
    /// that actually drifted when the platform's real domain changed. Resolved once per job run
    /// (GetSettingsAsync is memory-cached) rather than baked into the template strings.
    /// </summary>
    private async Task<string> GetBaseUrlAsync()
    {
        // Single public host with path-based routing — there is no per-tenant subdomain, so the
        // old "https://{slug}.{baseDomain}/..." links resolved nowhere on the real deployment.
        return await _platformSettingsService.GetPublicWebBaseUrlAsync();
    }

    /// <summary>
    /// Check for trials expiring soon and send reminder emails
    /// Runs daily at 9 AM
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task CheckExpiringTrialsAsync()
    {
        _logger.LogInformation("Starting check for expiring trials");

        var baseUrl = await GetBaseUrlAsync();
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
                    GetTrialExpiringEmailBody(org.Name, daysLeft, baseUrl),
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
                        GetTrialExpiredEmailBody(org.Name, baseUrl),
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
    /// Same warn-then-lock shape as CheckExpiringTrialsAsync above, but for the modular
    /// subscription system's per-module trials (OrganizationModule.TrialEndsAt) instead of the
    /// legacy single Organization.TrialEndsAt. Deliberately does NOT attempt to auto-charge on
    /// expiry the way a saved-card gateway could — Mobile Money collection needs the customer to
    /// approve on their own phone in real time, so it can't be silently billed in the background.
    /// Flipping to PastDue is enough on its own: IModuleAccessService only treats Active/Trialing
    /// as "active," so the module locks out immediately without any separate gating change.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task CheckExpiringModuleTrialsAsync()
    {
        _logger.LogInformation("Starting check for expiring module trials");

        var baseUrl = await GetBaseUrlAsync();
        var now = DateTime.UtcNow;
        var warningThreshold = now.AddDays(3);

        var expiringSoon = await _dbContext.OrganizationModules
            .Include(om => om.Module)
            .Include(om => om.Organization)
            .Where(om => om.Status == OrganizationModuleStatus.Trialing &&
                         om.TrialEndsAt != null &&
                         om.TrialEndsAt <= warningThreshold &&
                         om.TrialEndsAt > now)
            .ToListAsync();

        var warned = 0;
        foreach (var om in expiringSoon)
        {
            try
            {
                var daysLeft = Math.Max(1, (om.TrialEndsAt!.Value - now).Days);
                var moduleName = om.Module?.Name ?? "a module";

                await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    OrganizationId = om.OrganizationId,
                    Title = $"{moduleName} trial ending soon",
                    Message = $"Your trial of {moduleName} ends in {daysLeft} day{(daysLeft == 1 ? "" : "s")}. Add a payment method from Billing to keep using it.",
                    Type = NotificationType.SystemAlert,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp,
                    ActionUrl = BillingLinks.Modules,
                    IconClass = "clock-history"
                });

                if (om.Organization != null)
                {
                    await _notificationService.SendEmailAsync(
                        om.OrganizationId,
                        om.Organization.EffectiveBillingEmail,
                        $"Your {moduleName} trial expires in {daysLeft} days",
                        GetModuleTrialExpiringEmailBody(om.Organization.Name, moduleName, daysLeft, baseUrl),
                        true);
                }

                warned++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send module-trial-expiring reminder for OrganizationModule {Id}", om.Id);
            }
        }

        var expired = await _dbContext.OrganizationModules
            .Include(om => om.Module)
            .Include(om => om.Organization)
            .Where(om => om.Status == OrganizationModuleStatus.Trialing &&
                         om.TrialEndsAt != null &&
                         om.TrialEndsAt <= now)
            .ToListAsync();

        var lockedOut = 0;
        foreach (var om in expired)
        {
            try
            {
                om.Status = OrganizationModuleStatus.PastDue;
                _dbContext.OrganizationModules.Update(om);

                var moduleName = om.Module?.Name ?? "a module";
                await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    OrganizationId = om.OrganizationId,
                    Title = $"{moduleName} trial has ended",
                    Message = $"Your trial of {moduleName} has ended and is now locked. Add it back any time from Billing.",
                    Type = NotificationType.SystemAlert,
                    Priority = NotificationPriority.High,
                    Channels = NotificationChannel.InApp,
                    ActionUrl = BillingLinks.Modules,
                    IconClass = "lock-fill"
                });

                if (om.Organization != null)
                {
                    await _notificationService.SendEmailAsync(
                        om.OrganizationId,
                        om.Organization.EffectiveBillingEmail,
                        $"Your {moduleName} trial has ended",
                        GetModuleTrialExpiredEmailBody(om.Organization.Name, moduleName, baseUrl),
                        true);
                }

                lockedOut++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to lock expired module trial for OrganizationModule {Id}", om.Id);
            }
        }

        if (lockedOut > 0)
        {
            await _dbContext.SaveChangesAsync();

            // Without this, IModuleAccessService's 5-minute IDistributedCache would keep serving
            // the pre-lock "active" answer to nav gating and [RequireModule] for up to 5 more
            // minutes after the DB row already says PastDue.
            foreach (var orgId in expired.Select(om => om.OrganizationId).Distinct())
            {
                await _moduleAccessService.InvalidateCacheAsync(orgId);
            }
        }

        _logger.LogInformation("Module trial expiry check: Warned {Warned}, Locked {Locked}", warned, lockedOut);
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
    /// Raises invoices for every organization with a module whose billing period has run out.
    /// </summary>
    /// <remarks>
    /// It used to walk subscriptions, which meant it billed a tier and never a module — so a
    /// customer who bought modules was charged once at purchase and never invoiced again. It now
    /// walks the module holdings themselves, and each organization gets one invoice covering
    /// everything of theirs that fell due.
    /// </remarks>
    [AutomaticRetry(Attempts = 3)]
    public async Task GenerateMonthlyInvoicesAsync()
    {
        _logger.LogInformation("Starting invoice generation");

        var asOf = DateTime.UtcNow;

        var organizationIds = await _dbContext.OrganizationModules
            .Where(om => om.Status == OrganizationModuleStatus.Active &&
                         om.CurrentPeriodEnd != null &&
                         om.CurrentPeriodEnd <= asOf)
            .Select(om => om.OrganizationId)
            .Distinct()
            .ToListAsync();

        var generated = 0;
        foreach (var organizationId in organizationIds)
        {
            try
            {
                var invoice = await _billingService.GenerateInvoiceForDueModulesAsync(organizationId, asOf);
                if (invoice == null) continue;

                generated++;
                _logger.LogInformation(
                    "Generated invoice {InvoiceNumber} for organization {OrganizationId}",
                    invoice.InvoiceNumber, organizationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to generate an invoice for organization {OrganizationId}",
                    organizationId);
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

        var baseUrl = await GetBaseUrlAsync();
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
                else if (result.ErrorCode == BillingService.AwaitingConfirmation)
                {
                    // A mobile money prompt is out and unanswered. Not a failed payment: marking the
                    // subscription past due and emailing "payment failed" here would tell a customer
                    // their payment failed while their phone is still asking them to approve it.
                    _logger.LogInformation("Invoice {InvoiceId} is waiting on a mobile money approval", invoice.Id);
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
                                    invoice.Total, baseUrl),
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

        var baseUrl = await GetBaseUrlAsync();
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
                            subscription.Organization.Name, baseUrl),
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

        // TENANT DELETION IS NOT DONE HERE ANY MORE, and what was here could never have worked.
        //
        // This block used to take every organization sitting in TenantStatus.Deleted for 30 days and
        // call Organizations.Remove(org). Two things were wrong with it:
        //
        //   1. NOTHING IN THE CODEBASE EVER SET TenantStatus.Deleted. It was read in four places —
        //      the status middleware, the platform analytics count, the registration guard, and
        //      right here — and assigned in none, so this loop had never had a row to work on and
        //      the whole 30-day grace period was hanging off a state the product could not reach.
        //   2. Had it ever found one, it would have thrown. The model has 36 foreign keys pointing
        //      at `organizations` with DeleteBehavior.Restrict; a bare Remove against those is a
        //      foreign-key violation, and because this all shares one SaveChangesAsync it would
        //      have taken the notification pruning and the usage resets down with it every night.
        //
        // Emptying a tenant out means files first, then Hangfire's own tables, then ~70 tables in
        // foreign-key order, then a completeness check read from information_schema. That is
        // ITenantPurgeService, driven by the clocks in ITenantLifecycleService and its own
        // "tenant-lifecycle-sweep" job. See TenantDataManifest for the classification it rests on.
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

    private static string GetTrialExpiringEmailBody(string orgName, int daysLeft, string baseUrl) =>
        EmailTemplates.Layout(
            "Your trial is ending soon",
            null,
            new[]
            {
                $"Your {EmailTemplates.AppName} trial for {EmailTemplates.B(orgName)} will expire in {EmailTemplates.B($"{daysLeft} day{(daysLeft == 1 ? "" : "s")}")}.",
                $"To continue using {EmailTemplates.AppName} without interruption, please subscribe to a plan.",
                "If you have any questions, our support team is here to help."
            },
            "Choose a Plan",
            EmailTemplates.Link(baseUrl, BillingLinks.Modules));

    private static string GetTrialExpiredEmailBody(string orgName, string baseUrl) =>
        EmailTemplates.Layout(
            "Your trial has ended",
            null,
            new[]
            {
                $"Your {EmailTemplates.AppName} trial for {EmailTemplates.B(orgName)} has expired.",
                "Your account has been temporarily suspended. To restore access, please subscribe to a plan.",
                "Your data is safe and will be available once you subscribe."
            },
            "Subscribe Now",
            EmailTemplates.Link(baseUrl, BillingLinks.Modules),
            tone: EmailTemplates.Tone.Warning);

    private static string GetModuleTrialExpiringEmailBody(string orgName, string moduleName, int daysLeft, string baseUrl) =>
        EmailTemplates.Layout(
            $"Your {moduleName} trial is ending soon",
            null,
            new[]
            {
                $"Your trial of {EmailTemplates.B(moduleName)} for {EmailTemplates.B(orgName)} ends in {EmailTemplates.B($"{daysLeft} day{(daysLeft == 1 ? "" : "s")}")}.",
                "To keep using it without interruption, add a payment method from Billing before the trial ends.",
                "Your other modules and data are unaffected."
            },
            "Manage Modules",
            EmailTemplates.Link(baseUrl, BillingLinks.Modules));

    private static string GetModuleTrialExpiredEmailBody(string orgName, string moduleName, string baseUrl) =>
        EmailTemplates.Layout(
            $"Your {moduleName} trial has ended",
            null,
            new[]
            {
                $"The trial of {EmailTemplates.B(moduleName)} for {EmailTemplates.B(orgName)} has ended and the module is now locked.",
                $"Your other modules keep working normally. Add {EmailTemplates.P(moduleName)} back any time from Billing.",
                "Your data for this module is safe and will be available again once you add it back."
            },
            "Manage Modules",
            EmailTemplates.Link(baseUrl, BillingLinks.Modules),
            tone: EmailTemplates.Tone.Warning);

    private static string GetPaymentFailedEmailBody(string orgName, decimal amount, string baseUrl) =>
        EmailTemplates.Layout(
            "Payment failed",
            null,
            new[]
            {
                $"We were unable to process your payment of {EmailTemplates.B($"${amount:F2}")} for {EmailTemplates.B(orgName)}.",
                "Please update your payment method to avoid service interruption.",
                "If you believe this is an error, please contact our support team."
            },
            "Update Payment Method",
            EmailTemplates.Link(baseUrl, BillingLinks.Payment),
            tone: EmailTemplates.Tone.Warning);

    private static string GetAccountSuspendedEmailBody(string orgName, string baseUrl) =>
        EmailTemplates.Layout(
            "Account suspended",
            null,
            new[]
            {
                $"Your {EmailTemplates.AppName} account for {EmailTemplates.B(orgName)} has been suspended due to payment issues.",
                "To restore your account, please update your payment method and clear any outstanding balance.",
                "Your data is being preserved and will be available once payment is received."
            },
            "Restore Account",
            EmailTemplates.Link(baseUrl, BillingLinks.Payment),
            tone: EmailTemplates.Tone.Warning);

    #endregion

    #region Helper Methods

    private async Task SendUsageWarningAsync(
        Domain.Entities.Organization.Organization org,
        string resourceType,
        int current,
        int limit)
    {
        var percentage = (int)((double)current / limit * 100);
        var baseUrl = await GetBaseUrlAsync();

        await _notificationService.SendEmailAsync(
            org.Id,
            org.EffectiveBillingEmail,
            $"Usage warning: {percentage}% of {resourceType} limit used",
            EmailTemplates.Layout(
                "Usage warning",
                null,
                new[]
                {
                    $"Your organization {EmailTemplates.B(org.Name)} has used {EmailTemplates.B($"{percentage}%")} of your monthly {EmailTemplates.P(resourceType)} limit.",
                    $"Current usage: {EmailTemplates.B(current.ToString("N0"))} / {EmailTemplates.B(limit.ToString("N0"))}",
                    "To avoid service interruption, consider upgrading your plan."
                },
                "Upgrade Plan",
                EmailTemplates.Link(baseUrl, BillingLinks.Modules)),
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
        var baseUrl = await GetBaseUrlAsync();

        await _notificationService.SendEmailAsync(
            org.Id,
            org.EffectiveBillingEmail,
            $"Limit exceeded: {resourceType}",
            EmailTemplates.Layout(
                "Limit exceeded",
                null,
                new[]
                {
                    $"Your organization {EmailTemplates.B(org.Name)} has exceeded your monthly {EmailTemplates.P(resourceType)} limit.",
                    $"Current usage: {EmailTemplates.B(current.ToString("N0"))} / {EmailTemplates.B(limit.ToString("N0"))}",
                    "Some features may be restricted until your usage resets next month or you upgrade your plan."
                },
                "Upgrade Now",
                EmailTemplates.Link(baseUrl, BillingLinks.Modules),
                tone: EmailTemplates.Tone.Warning),
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

        // Check expiring module trials (modular subscription system) - Daily at 9 AM UTC
        RecurringJob.AddOrUpdate<BillingJobs>(
            "check-expiring-module-trials",
            job => job.CheckExpiringModuleTrialsAsync(),
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

        // Invoice every module whose period has ended — DAILY at 01:00 UTC (2026-09-19). It ran only on
        // the 1st, so a module whose period ended on the 2nd was billed about four weeks late. The
        // method bills only what has fallen due and rolls each period forward, so running it daily
        // raises nothing twice. The old id is removed so the monthly schedule does not also fire.
        RecurringJob.RemoveIfExists("generate-monthly-invoices");
        RecurringJob.AddOrUpdate<BillingJobs>(
            "generate-due-invoices",
            job => job.GenerateMonthlyInvoicesAsync(),
            "0 1 * * *");

        // Ask the sacc.ug gateway about every payment still open — every five minutes. The signed
        // webhook normally settles a payment first; this is what settles it when a webhook is lost,
        // and what abandons one the gateway never received.
        RecurringJob.AddOrUpdate<PaymentReconciliationJob>(
            "reconcile-gateway-payments",
            job => job.RunAsync(),
            "*/5 * * * *");

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
