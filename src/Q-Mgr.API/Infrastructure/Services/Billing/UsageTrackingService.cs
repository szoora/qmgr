using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Billing;
using QMgr.Infrastructure.Data;
using System.Text.Json;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// Service for tracking organization usage metrics
/// Uses distributed cache for high-frequency counters, flushed to DB periodically
/// </summary>
public class UsageTrackingService : IUsageTrackingService
{
    private readonly QMgrDbContext _dbContext;
    private readonly IDistributedCache _cache;
    private readonly ILogger<UsageTrackingService> _logger;
    private const string CachePrefix = "usage:";
    private const int CacheExpirationMinutes = 60;

    public UsageTrackingService(
        QMgrDbContext dbContext,
        IDistributedCache cache,
        ILogger<UsageTrackingService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    #region Increment Operations

    public async Task IncrementTokensCreatedAsync(Guid organizationId, int count = 1)
    {
        await IncrementCounterAsync(organizationId, "tokens_created", count);
    }

    public async Task IncrementTokensServedAsync(Guid organizationId, int count = 1)
    {
        await IncrementCounterAsync(organizationId, "tokens_served", count);
    }

    public async Task IncrementApiCallsAsync(Guid organizationId, int count = 1)
    {
        await IncrementCounterAsync(organizationId, "api_calls", count);
    }

    public async Task IncrementWebhookDeliveriesAsync(Guid organizationId, int count = 1)
    {
        await IncrementCounterAsync(organizationId, "webhook_deliveries", count);
    }

    public async Task IncrementSmsSentAsync(Guid organizationId, int count = 1)
    {
        await IncrementCounterAsync(organizationId, "sms_sent", count);
    }

    public async Task IncrementEmailsSentAsync(Guid organizationId, int count = 1)
    {
        await IncrementCounterAsync(organizationId, "emails_sent", count);
    }

    public async Task IncrementPushNotificationsSentAsync(Guid organizationId, int count = 1)
    {
        await IncrementCounterAsync(organizationId, "push_notifications", count);
    }

    public async Task IncrementDisplayViewsAsync(Guid organizationId, int count = 1)
    {
        await IncrementCounterAsync(organizationId, "display_views", count);
    }

    public async Task UpdateStorageUsageAsync(Guid organizationId, long bytesUsed)
    {
        var record = await GetOrCreateCurrentRecordAsync(organizationId);
        record.StorageUsedBytes = bytesUsed;
        record.LastUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    #endregion

    #region Snapshot Operations

    public async Task UpdateActiveUsersAsync(Guid organizationId, int count)
    {
        var record = await GetOrCreateCurrentRecordAsync(organizationId);
        record.ActiveUsers = count;
        record.LastUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateActiveBranchesAsync(Guid organizationId, int count)
    {
        var record = await GetOrCreateCurrentRecordAsync(organizationId);
        record.ActiveBranches = count;
        record.LastUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateActiveCountersAsync(Guid organizationId, int count)
    {
        var record = await GetOrCreateCurrentRecordAsync(organizationId);
        record.ActiveCounters = count;
        record.LastUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    #endregion

    #region Query Operations

    public async Task<UsageRecord> GetCurrentUsageAsync(Guid organizationId)
    {
        var record = await GetOrCreateCurrentRecordAsync(organizationId);

        // Flush any cached counters to the record
        await FlushCachedCountersAsync(organizationId, record);

        return record;
    }

    public async Task<UsageRecord?> GetUsageAsync(Guid organizationId, int year, int month)
    {
        return await _dbContext.UsageRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.OrganizationId == organizationId &&
                r.Year == year &&
                r.Month == month);
    }

    public async Task<IEnumerable<UsageRecord>> GetUsageHistoryAsync(Guid organizationId, int monthsBack = 12)
    {
        var cutoffDate = DateTime.UtcNow.AddMonths(-monthsBack);

        return await _dbContext.UsageRecords
            .AsNoTracking()
            .Where(r => r.OrganizationId == organizationId)
            .Where(r => r.Year > cutoffDate.Year ||
                       (r.Year == cutoffDate.Year && r.Month >= cutoffDate.Month))
            .OrderByDescending(r => r.Year)
            .ThenByDescending(r => r.Month)
            .ToListAsync();
    }

    public async Task<UsageSummary> GetGlobalUsageSummaryAsync(int year, int month)
    {
        var records = await _dbContext.UsageRecords
            .AsNoTracking()
            .Where(r => r.Year == year && r.Month == month)
            .ToListAsync();

        return new UsageSummary(
            year,
            month,
            records.Count,
            records.Sum(r => r.TokensCreated),
            records.Sum(r => r.ApiCalls),
            records.Sum(r => r.StorageUsedBytes),
            records.Sum(r => r.AdImpressions));
    }

    #endregion

    #region Limit Checks

    public async Task<bool> IsWithinTokenLimitAsync(Guid organizationId)
    {
        var status = await GetLimitStatusAsync(organizationId, "tokens");
        return !status.IsExceeded;
    }

    public async Task<bool> IsWithinApiLimitAsync(Guid organizationId)
    {
        var status = await GetLimitStatusAsync(organizationId, "api_calls");
        return !status.IsExceeded;
    }

    public async Task<bool> IsWithinStorageLimitAsync(Guid organizationId)
    {
        var status = await GetLimitStatusAsync(organizationId, "storage");
        return !status.IsExceeded;
    }

    public async Task<UsageLimitStatus> GetLimitStatusAsync(Guid organizationId, string limitType)
    {
        var usage = await GetCurrentUsageAsync(organizationId);
        var normalizedType = limitType.ToLowerInvariant();

        // Count-based limits (branches/users/displays) are enforced against a live
        // count, not the monthly UsageRecord snapshot fields (ActiveBranches/ActiveUsers):
        // nothing in the codebase ever calls UpdateActiveBranchesAsync/UpdateActiveUsersAsync,
        // so those snapshot fields always read back 0 and would silently defeat the limit
        // check. A live count has no such staleness risk and needs no call-site upkeep.
        var current = normalizedType switch
        {
            "branches" => await _dbContext.Branches.CountAsync(b => b.OrganizationId == organizationId),
            "users" => await _dbContext.Users.CountAsync(u => u.OrganizationId == organizationId),
            "displays" => await _dbContext.Displays.CountAsync(d => d.Branch != null && d.Branch.OrganizationId == organizationId),
            "tokens" => usage.TokensCreated,
            "api_calls" => usage.ApiCalls,
            "storage" => (int)(usage.StorageUsedBytes / 1024 / 1024),
            _ => 0
        };

        // Get organization's subscription limits
        var subscription = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId &&
                                      s.Status == Domain.Enums.SubscriptionStatus.Active);

        if (subscription == null)
        {
            // No subscription - use free tier defaults
            return GetFreeTierLimitStatus(normalizedType, current);
        }

        var plan = subscription.Plan;
        var max = normalizedType switch
        {
            "tokens" => subscription.MaxTokensOverride ?? plan.MaxTokensPerMonth,
            "api_calls" => subscription.MaxApiCallsOverride ?? plan.MaxApiCallsPerMonth,
            "storage" => subscription.MaxStorageOverride ?? plan.MaxStorageMb,
            "users" => subscription.MaxUsersOverride ?? plan.MaxUsersPerBranch,
            "branches" => subscription.MaxBranchesOverride ?? plan.MaxBranches,
            "displays" => subscription.MaxDisplaysOverride ?? plan.MaxDisplays,
            _ => int.MaxValue
        };

        var percentage = max > 0 ? (double)current / max * 100 : 0;

        return new UsageLimitStatus(
            normalizedType,
            current,
            max,
            percentage,
            percentage >= 80,
            percentage >= 100);
    }

    private static UsageLimitStatus GetFreeTierLimitStatus(string limitType, int current)
    {
        var max = limitType switch
        {
            "tokens" => 100,
            "api_calls" => 0, // No API access on free tier
            "storage" => 100,
            "users" => 2,
            "branches" => 1,
            "displays" => 1,
            _ => 1
        };

        var percentage = max > 0 ? (double)current / max * 100 : 100;

        return new UsageLimitStatus(
            limitType,
            current,
            max,
            percentage,
            percentage >= 80,
            percentage >= 100);
    }

    #endregion

    #region Ad Tracking

    public async Task TrackAdImpressionAsync(
        Guid organizationId,
        Guid branchId,
        Guid? displayId,
        string adSlot,
        string adProvider,
        string? campaignId = null)
    {
        var today = DateTime.UtcNow.Date;

        var impression = await _dbContext.AdImpressions
            .FirstOrDefaultAsync(i =>
                i.OrganizationId == organizationId &&
                i.BranchId == branchId &&
                i.DisplayId == displayId &&
                i.AdSlot == adSlot &&
                i.Date == today);

        if (impression == null)
        {
            impression = new AdImpression
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                DisplayId = displayId,
                AdSlot = adSlot,
                AdProvider = adProvider,
                CampaignId = campaignId,
                Date = today,
                ImpressionCount = 1,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.AdImpressions.Add(impression);
        }
        else
        {
            impression.ImpressionCount++;
            impression.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        // Also update the usage record
        await IncrementCounterAsync(organizationId, "ad_impressions", 1);
    }

    public async Task TrackAdClickAsync(
        Guid organizationId,
        Guid branchId,
        string adSlot,
        string? campaignId = null)
    {
        var today = DateTime.UtcNow.Date;

        var impression = await _dbContext.AdImpressions
            .FirstOrDefaultAsync(i =>
                i.OrganizationId == organizationId &&
                i.BranchId == branchId &&
                i.AdSlot == adSlot &&
                i.Date == today);

        if (impression != null)
        {
            impression.ClickCount++;
            impression.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }
    }

    public async Task<AdImpressionStats> GetAdStatsAsync(Guid organizationId, DateTime startDate, DateTime endDate)
    {
        var impressions = await _dbContext.AdImpressions
            .AsNoTracking()
            .Where(i => i.OrganizationId == organizationId &&
                       i.Date >= startDate.Date &&
                       i.Date <= endDate.Date)
            .ToListAsync();

        var totalImpressions = impressions.Sum(i => i.ImpressionCount);
        var totalClicks = impressions.Sum(i => i.ClickCount);
        var ctr = totalImpressions > 0 ? (double)totalClicks / totalImpressions * 100 : 0;

        var bySlot = impressions
            .GroupBy(i => i.AdSlot)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.ImpressionCount));

        var byDay = impressions
            .GroupBy(i => i.Date.ToString("yyyy-MM-dd"))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.ImpressionCount));

        return new AdImpressionStats(
            totalImpressions,
            totalClicks,
            ctr,
            impressions.Sum(i => i.EstimatedRevenue),
            bySlot,
            byDay);
    }

    #endregion

    #region Finalization

    public async Task FinalizeMonthAsync(Guid organizationId, int year, int month)
    {
        var record = await _dbContext.UsageRecords
            .FirstOrDefaultAsync(r =>
                r.OrganizationId == organizationId &&
                r.Year == year &&
                r.Month == month);

        if (record != null && record.FinalizedAt == null)
        {
            // Flush any remaining cached counters
            await FlushCachedCountersAsync(organizationId, record);

            record.FinalizedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Finalized usage record for organization {OrganizationId}, {Year}/{Month}",
                organizationId, year, month);
        }
    }

    public async Task RecordPeakUsageAsync(Guid organizationId)
    {
        // This can be called periodically to record snapshot metrics
        var activeUsers = await _dbContext.Users
            .CountAsync(u => u.OrganizationId == organizationId && u.IsActive);

        var activeBranches = await _dbContext.Branches
            .CountAsync(b => b.OrganizationId == organizationId && b.IsActive);

        var activeCounters = await _dbContext.Counters
            .Include(c => c.Branch)
            .CountAsync(c => c.Branch.OrganizationId == organizationId && c.IsActive);

        await UpdateActiveUsersAsync(organizationId, activeUsers);
        await UpdateActiveBranchesAsync(organizationId, activeBranches);
        await UpdateActiveCountersAsync(organizationId, activeCounters);
    }

    #endregion

    #region Aggregation

    public async Task AggregateUsageAsync(Guid organizationId)
    {
        // Flush all cached counters to the database
        var record = await GetOrCreateCurrentRecordAsync(organizationId);
        await FlushCachedCountersAsync(organizationId, record);

        // Record peak usage
        await RecordPeakUsageAsync(organizationId);

        _logger.LogDebug("Aggregated usage for organization {OrganizationId}", organizationId);
    }

    public async Task ResetMonthlyCountersAsync(Guid organizationId)
    {
        var now = DateTime.UtcNow;
        var year = now.Year;
        var month = now.Month;

        // Finalize the previous month if not already done
        var prevYear = month == 1 ? year - 1 : year;
        var prevMonth = month == 1 ? 12 : month - 1;
        await FinalizeMonthAsync(organizationId, prevYear, prevMonth);

        // Clear cached counters for the new month
        var counters = new[] { "tokens", "api_calls", "sms", "emails", "webhooks", "push", "display_views" };
        foreach (var counter in counters)
        {
            var cacheKey = $"{CachePrefix}{organizationId}:{now:yyyy-MM}:{counter}";
            await _cache.RemoveAsync(cacheKey);
        }

        // Create new usage record for this month
        await GetOrCreateCurrentRecordAsync(organizationId);

        _logger.LogInformation(
            "Reset monthly counters for organization {OrganizationId} for {Year}/{Month}",
            organizationId, year, month);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Returns this month's usage row for the organization, creating it if it doesn't exist yet.
    /// <para>
    /// Two failure modes this has to survive, both seen live in the daily usage-limits job:
    /// a concurrent writer (a web request tracking usage while the Hangfire job runs) can insert
    /// the same (OrganizationId, Year, Month) between our read and our write, which the unique
    /// index rejects; and — worse — a failed insert leaves the entity tracked as Added, so every
    /// later <c>SaveChangesAsync</c> on the same shared scoped context re-attempts it and throws
    /// again. Since <c>BillingJobs.CheckUsageLimitsAsync</c> loops over every organization on one
    /// context and swallows per-organization exceptions, a single collision silently poisoned the
    /// rest of the run: the job reported success having done nothing for any subsequent tenant.
    /// So: check the change tracker first, and on a unique violation detach the doomed entity and
    /// re-read the row the other writer committed.
    /// </para>
    /// </summary>
    private async Task<UsageRecord> GetOrCreateCurrentRecordAsync(Guid organizationId)
    {
        var now = DateTime.UtcNow;
        var year = now.Year;
        var month = now.Month;

        // A row this unit of work already added but hasn't saved is invisible to a database query.
        var local = _dbContext.UsageRecords.Local.FirstOrDefault(r =>
            r.OrganizationId == organizationId && r.Year == year && r.Month == month);
        if (local != null)
        {
            return local;
        }

        var record = await _dbContext.UsageRecords
            .FirstOrDefaultAsync(r =>
                r.OrganizationId == organizationId &&
                r.Year == year &&
                r.Month == month);

        if (record != null)
        {
            return record;
        }

        record = new UsageRecord
        {
            OrganizationId = organizationId,
            Year = year,
            Month = month,
            LastUpdatedAt = now,
            CreatedAt = now
        };
        _dbContext.UsageRecords.Add(record);

        try
        {
            await _dbContext.SaveChangesAsync();
            return record;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _dbContext.Entry(record).State = EntityState.Detached;

            var existing = await _dbContext.UsageRecords
                .FirstOrDefaultAsync(r =>
                    r.OrganizationId == organizationId &&
                    r.Year == year &&
                    r.Month == month);

            if (existing == null)
            {
                // The index rejected the insert but nothing is there to read back — a different
                // constraint than the one we're handling for. Don't paper over it.
                throw;
            }

            _logger.LogDebug(
                "Usage record for organization {OrganizationId} {Year}-{Month} was created concurrently; using the committed row",
                organizationId, year, month);

            return existing;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private async Task IncrementCounterAsync(Guid organizationId, string counterName, int count)
    {
        var cacheKey = $"{CachePrefix}{organizationId}:{DateTime.UtcNow:yyyy-MM}:{counterName}";

        try
        {
            // Try to increment in cache
            var cached = await _cache.GetStringAsync(cacheKey);
            var currentValue = cached != null ? int.Parse(cached) : 0;
            var newValue = currentValue + count;

            await _cache.SetStringAsync(
                cacheKey,
                newValue.ToString(),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheExpirationMinutes)
                });

            // Periodically flush to database (every 100 increments or if first write)
            if (newValue % 100 == 0 || cached == null)
            {
                await FlushCounterToDbAsync(organizationId, counterName, newValue);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache increment failed, falling back to direct DB update");
            // Fallback to direct DB update
            await DirectIncrementAsync(organizationId, counterName, count);
        }
    }

    private async Task FlushCounterToDbAsync(Guid organizationId, string counterName, int value)
    {
        var record = await GetOrCreateCurrentRecordAsync(organizationId);

        switch (counterName)
        {
            case "tokens_created":
                record.TokensCreated = value;
                break;
            case "tokens_served":
                record.TokensServed = value;
                break;
            case "api_calls":
                record.ApiCalls = value;
                break;
            case "webhook_deliveries":
                record.WebhookDeliveries = value;
                break;
            case "sms_sent":
                record.SmsMessagesSent = value;
                break;
            case "emails_sent":
                record.EmailsSent = value;
                break;
            case "push_notifications":
                record.PushNotificationsSent = value;
                break;
            case "display_views":
                record.DisplayViews = value;
                break;
            case "ad_impressions":
                record.AdImpressions = value;
                break;
        }

        record.LastUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    private async Task DirectIncrementAsync(Guid organizationId, string counterName, int count)
    {
        var record = await GetOrCreateCurrentRecordAsync(organizationId);

        switch (counterName)
        {
            case "tokens_created":
                record.TokensCreated += count;
                break;
            case "tokens_served":
                record.TokensServed += count;
                break;
            case "api_calls":
                record.ApiCalls += count;
                break;
            case "webhook_deliveries":
                record.WebhookDeliveries += count;
                break;
            case "sms_sent":
                record.SmsMessagesSent += count;
                break;
            case "emails_sent":
                record.EmailsSent += count;
                break;
            case "push_notifications":
                record.PushNotificationsSent += count;
                break;
            case "display_views":
                record.DisplayViews += count;
                break;
            case "ad_impressions":
                record.AdImpressions += count;
                break;
        }

        record.LastUpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    private async Task FlushCachedCountersAsync(Guid organizationId, UsageRecord record)
    {
        var counters = new[] { "tokens_created", "tokens_served", "api_calls", "webhook_deliveries",
                              "sms_sent", "emails_sent", "push_notifications", "display_views", "ad_impressions" };

        foreach (var counter in counters)
        {
            var cacheKey = $"{CachePrefix}{organizationId}:{record.Year}-{record.Month:D2}:{counter}";
            try
            {
                var cached = await _cache.GetStringAsync(cacheKey);
                if (cached != null && int.TryParse(cached, out var value))
                {
                    switch (counter)
                    {
                        case "tokens_created":
                            record.TokensCreated = Math.Max(record.TokensCreated, value);
                            break;
                        case "tokens_served":
                            record.TokensServed = Math.Max(record.TokensServed, value);
                            break;
                        case "api_calls":
                            record.ApiCalls = Math.Max(record.ApiCalls, value);
                            break;
                        case "webhook_deliveries":
                            record.WebhookDeliveries = Math.Max(record.WebhookDeliveries, value);
                            break;
                        case "sms_sent":
                            record.SmsMessagesSent = Math.Max(record.SmsMessagesSent, value);
                            break;
                        case "emails_sent":
                            record.EmailsSent = Math.Max(record.EmailsSent, value);
                            break;
                        case "push_notifications":
                            record.PushNotificationsSent = Math.Max(record.PushNotificationsSent, value);
                            break;
                        case "display_views":
                            record.DisplayViews = Math.Max(record.DisplayViews, value);
                            break;
                        case "ad_impressions":
                            record.AdImpressions = Math.Max(record.AdImpressions, value);
                            break;
                    }
                }
            }
            catch
            {
                // Ignore cache read errors
            }
        }

        record.LastUpdatedAt = DateTime.UtcNow;
    }

    #endregion
}
