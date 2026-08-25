using QMgr.Domain.Entities.Billing;

namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// Service for tracking organization usage metrics
/// </summary>
public interface IUsageTrackingService
{
    #region Increment Operations

    /// <summary>
    /// Increment token creation count
    /// </summary>
    Task IncrementTokensCreatedAsync(Guid organizationId, int count = 1);

    /// <summary>
    /// Increment tokens served count
    /// </summary>
    Task IncrementTokensServedAsync(Guid organizationId, int count = 1);

    /// <summary>
    /// Increment API call count
    /// </summary>
    Task IncrementApiCallsAsync(Guid organizationId, int count = 1);

    /// <summary>
    /// Increment webhook delivery count
    /// </summary>
    Task IncrementWebhookDeliveriesAsync(Guid organizationId, int count = 1);

    /// <summary>
    /// Increment SMS sent count
    /// </summary>
    Task IncrementSmsSentAsync(Guid organizationId, int count = 1);

    /// <summary>
    /// Increment email sent count
    /// </summary>
    Task IncrementEmailsSentAsync(Guid organizationId, int count = 1);

    /// <summary>
    /// Increment push notifications sent count
    /// </summary>
    Task IncrementPushNotificationsSentAsync(Guid organizationId, int count = 1);

    /// <summary>
    /// Increment display view count
    /// </summary>
    Task IncrementDisplayViewsAsync(Guid organizationId, int count = 1);

    /// <summary>
    /// Update storage usage
    /// </summary>
    Task UpdateStorageUsageAsync(Guid organizationId, long bytesUsed);

    #endregion

    #region Snapshot Operations

    /// <summary>
    /// Update active users count (snapshot)
    /// </summary>
    Task UpdateActiveUsersAsync(Guid organizationId, int count);

    /// <summary>
    /// Update active branches count (snapshot)
    /// </summary>
    Task UpdateActiveBranchesAsync(Guid organizationId, int count);

    /// <summary>
    /// Update active counters count (snapshot)
    /// </summary>
    Task UpdateActiveCountersAsync(Guid organizationId, int count);

    #endregion

    #region Query Operations

    /// <summary>
    /// Get current month's usage record
    /// </summary>
    Task<UsageRecord> GetCurrentUsageAsync(Guid organizationId);

    /// <summary>
    /// Get usage record for a specific month
    /// </summary>
    Task<UsageRecord?> GetUsageAsync(Guid organizationId, int year, int month);

    /// <summary>
    /// Get usage history for an organization
    /// </summary>
    Task<IEnumerable<UsageRecord>> GetUsageHistoryAsync(
        Guid organizationId,
        int monthsBack = 12);

    /// <summary>
    /// Get usage summary across all organizations (admin)
    /// </summary>
    Task<UsageSummary> GetGlobalUsageSummaryAsync(int year, int month);

    #endregion

    #region Limit Checks

    /// <summary>
    /// Check if organization is within token limit
    /// </summary>
    Task<bool> IsWithinTokenLimitAsync(Guid organizationId);

    /// <summary>
    /// Check if organization is within API call limit
    /// </summary>
    Task<bool> IsWithinApiLimitAsync(Guid organizationId);

    /// <summary>
    /// Check if organization is within storage limit
    /// </summary>
    Task<bool> IsWithinStorageLimitAsync(Guid organizationId);

    /// <summary>
    /// Get percentage of limit used
    /// </summary>
    Task<UsageLimitStatus> GetLimitStatusAsync(Guid organizationId, string limitType);

    #endregion

    #region Ad Tracking

    /// <summary>
    /// Track an ad impression
    /// </summary>
    Task TrackAdImpressionAsync(
        Guid organizationId,
        Guid branchId,
        Guid? displayId,
        string adSlot,
        string adProvider,
        string? campaignId = null);

    /// <summary>
    /// Track an ad click
    /// </summary>
    Task TrackAdClickAsync(
        Guid organizationId,
        Guid branchId,
        string adSlot,
        string? campaignId = null);

    /// <summary>
    /// Get ad impression stats for an organization
    /// </summary>
    Task<AdImpressionStats> GetAdStatsAsync(Guid organizationId, DateTime startDate, DateTime endDate);

    #endregion

    #region Finalization

    /// <summary>
    /// Finalize a month's usage record (end of billing period)
    /// </summary>
    Task FinalizeMonthAsync(Guid organizationId, int year, int month);

    /// <summary>
    /// Calculate and record peak usage metrics
    /// </summary>
    Task RecordPeakUsageAsync(Guid organizationId);

    #endregion

    #region Aggregation

    /// <summary>
    /// Aggregate and persist usage metrics from cache to database
    /// </summary>
    Task AggregateUsageAsync(Guid organizationId);

    /// <summary>
    /// Reset monthly usage counters for an organization
    /// </summary>
    Task ResetMonthlyCountersAsync(Guid organizationId);

    #endregion
}

/// <summary>
/// Usage limit status
/// </summary>
public record UsageLimitStatus(
    string LimitType,
    int CurrentUsage,
    int MaxAllowed,
    double PercentageUsed,
    bool IsApproachingLimit,
    bool IsExceeded);

/// <summary>
/// Global usage summary
/// </summary>
public record UsageSummary(
    int Year,
    int Month,
    int TotalOrganizations,
    long TotalTokensCreated,
    long TotalApiCalls,
    long TotalStorageBytes,
    int TotalAdImpressions);

/// <summary>
/// Ad impression statistics
/// </summary>
public record AdImpressionStats(
    int TotalImpressions,
    int TotalClicks,
    double ClickThroughRate,
    decimal EstimatedRevenue,
    Dictionary<string, int> ImpressionsBySlot,
    Dictionary<string, int> ImpressionsByDay);
