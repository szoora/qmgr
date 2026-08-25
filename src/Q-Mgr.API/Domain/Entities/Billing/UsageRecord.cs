using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Billing;

/// <summary>
/// Tracks monthly usage metrics for billing and limit enforcement
/// </summary>
public class UsageRecord : BaseEntity
{
    /// <summary>Organization this usage belongs to</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Year of the usage period</summary>
    public int Year { get; set; }

    /// <summary>Month of the usage period (1-12)</summary>
    public int Month { get; set; }

    #region Token Usage

    /// <summary>Total tokens created this period</summary>
    public int TokensCreated { get; set; }

    /// <summary>Total tokens served this period</summary>
    public int TokensServed { get; set; }

    /// <summary>Total tokens cancelled this period</summary>
    public int TokensCancelled { get; set; }

    #endregion

    #region API Usage

    /// <summary>Total API calls this period</summary>
    public int ApiCalls { get; set; }

    /// <summary>Total webhook deliveries this period</summary>
    public int WebhookDeliveries { get; set; }

    #endregion

    #region Resource Usage

    /// <summary>Number of active users this period</summary>
    public int ActiveUsers { get; set; }

    /// <summary>Number of active branches this period</summary>
    public int ActiveBranches { get; set; }

    /// <summary>Number of active counters this period</summary>
    public int ActiveCounters { get; set; }

    /// <summary>Storage used in bytes</summary>
    public long StorageUsedBytes { get; set; }

    #endregion

    #region Notification Usage

    /// <summary>SMS messages sent this period</summary>
    public int SmsMessagesSent { get; set; }

    /// <summary>Emails sent this period</summary>
    public int EmailsSent { get; set; }

    /// <summary>Push notifications sent this period</summary>
    public int PushNotificationsSent { get; set; }

    #endregion

    #region Display Usage (for ad tracking)

    /// <summary>Total display views (for ad impressions)</summary>
    public int DisplayViews { get; set; }

    /// <summary>Total ad impressions served</summary>
    public int AdImpressions { get; set; }

    #endregion

    #region Timestamps

    /// <summary>Last time this record was updated</summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this record was finalized (end of period)</summary>
    public DateTime? FinalizedAt { get; set; }

    #endregion

    #region Navigation

    /// <summary>The organization</summary>
    public virtual Organization.Organization? Organization { get; set; }

    #endregion

    #region Helper Properties

    /// <summary>Get the period as a date (first day of month)</summary>
    public DateTime PeriodDate => new DateTime(Year, Month, 1);

    /// <summary>Period key for indexing (e.g., "2024-01")</summary>
    public string PeriodKey => $"{Year:D4}-{Month:D2}";

    /// <summary>Storage used in MB</summary>
    public double StorageUsedMb => StorageUsedBytes / (1024.0 * 1024.0);

    #endregion
}
