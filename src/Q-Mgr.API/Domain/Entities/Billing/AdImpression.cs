using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Billing;

/// <summary>
/// Tracks ad impressions on free tier displays for monetization
/// </summary>
public class AdImpression : BaseEntity
{
    /// <summary>Organization showing the ad</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Branch where ad was shown</summary>
    public Guid BranchId { get; set; }

    /// <summary>Display device showing the ad</summary>
    public Guid? DisplayId { get; set; }

    #region Ad Details

    /// <summary>Ad slot identifier (e.g., "queue_board_banner", "kiosk_footer", "display_sidebar")</summary>
    public string AdSlot { get; set; } = string.Empty;

    /// <summary>Ad provider (e.g., "internal", "google_adsense", "custom")</summary>
    public string AdProvider { get; set; } = "internal";

    /// <summary>Campaign ID for internal ads</summary>
    public string? CampaignId { get; set; }

    /// <summary>Ad creative ID</summary>
    public string? CreativeId { get; set; }

    /// <summary>Ad unit ID (for external providers)</summary>
    public string? AdUnitId { get; set; }

    #endregion

    #region Metrics

    /// <summary>Number of impressions</summary>
    public int ImpressionCount { get; set; } = 1;

    /// <summary>Number of clicks (if tracked)</summary>
    public int ClickCount { get; set; }

    /// <summary>Estimated revenue from impressions</summary>
    public decimal EstimatedRevenue { get; set; }

    /// <summary>Revenue currency</summary>
    public string Currency { get; set; } = "USD";

    #endregion

    #region Time Period

    /// <summary>Date of the impressions (aggregated daily)</summary>
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;

    /// <summary>Hour of day (0-23) for hourly granularity</summary>
    public int? Hour { get; set; }

    #endregion

    #region Audience Data

    /// <summary>Estimated viewer count</summary>
    public int? EstimatedViewers { get; set; }

    /// <summary>Average dwell time in seconds</summary>
    public int? AvgDwellTimeSeconds { get; set; }

    #endregion

    #region Navigation

    /// <summary>The organization</summary>
    public virtual Organization.Organization? Organization { get; set; }

    /// <summary>The branch</summary>
    public virtual Organization.Branch? Branch { get; set; }

    /// <summary>The display device</summary>
    public virtual Content.Display? Display { get; set; }

    #endregion
}
