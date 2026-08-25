using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Queue;

public class Feedback : BaseEntity
{
    public Guid BranchId { get; set; }
    public Guid? TokenId { get; set; }
    public Guid? ServiceTypeId { get; set; }
    public Guid? CounterId { get; set; }
    public Guid? ServedByUserId { get; set; }

    // Unique code for offsite feedback link
    public string FeedbackCode { get; set; } = string.Empty;

    // Rating (1-5 stars)
    public int Rating { get; set; }

    // Feedback details
    public string? Comment { get; set; }
    public FeedbackCategory Category { get; set; } = FeedbackCategory.General;
    public FeedbackSource Source { get; set; } = FeedbackSource.Kiosk;

    // Customer info (optional)
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }

    // Token display number for reference
    public string? TokenDisplayNumber { get; set; }

    // Timestamps
    public DateTime? ServiceDate { get; set; }

    // Response from staff/management
    public string? Response { get; set; }
    public DateTime? RespondedAt { get; set; }
    public Guid? RespondedByUserId { get; set; }

    // Navigation properties
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Token? Token { get; set; }
    public virtual ServiceType? ServiceType { get; set; }
    public virtual Counter? Counter { get; set; }
}
