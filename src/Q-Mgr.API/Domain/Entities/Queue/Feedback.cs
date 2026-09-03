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

    /// <summary>
    /// Net Promoter Score answer, 0-10. Null when the customer skipped the NPS step — it is
    /// always optional, and a skipped NPS must stay distinguishable from a 0 ("not at all
    /// likely"), which is why this is nullable rather than defaulting to 0.
    /// </summary>
    public int? NpsScore { get; set; }

    /// <summary>
    /// The customer's answers to the branch's configured survey questions, as a JSON array of
    /// { QuestionId, QuestionText, QuestionType, Answer }. Stored inline on the feedback row
    /// rather than in an answers table on purpose: an answer has no lifecycle of its own and is
    /// only ever read back with its parent feedback, so a join table would buy nothing and cost
    /// every future query/report an extra join (see CLAUDE.md "enhance before you add").
    /// The question text/type are snapshotted per answer so editing or deleting a question
    /// definition later cannot rewrite or orphan history.
    /// </summary>
    public string? ResponsesJson { get; set; }

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
