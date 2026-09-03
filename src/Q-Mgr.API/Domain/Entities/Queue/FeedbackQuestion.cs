using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Queue;

/// <summary>
/// One admin-configured survey question shown on the public feedback form after the core 1-5
/// service rating.
///
/// WHY THIS IS A NEW TABLE (CLAUDE.md's standing "widen an existing table before adding one"
/// rule): a question definition is not an attribute of anything that already has a row. It has
/// its own independent lifecycle — created, reordered, edited, deactivated, and deleted without
/// any feedback existing — and there are 0..N of them per branch, which no column on Branch or
/// ServiceType can represent. The alternative considered and rejected was a JSON column on
/// Branch holding the question list; that would make "reorder one question", "deactivate one
/// question", and any per-question analytics a read-modify-write of the whole branch row, with
/// no FK to ServiceType and no way to index or query a single question. The *answers*, by
/// contrast, genuinely are an attribute of the feedback they belong to, so they are stored as
/// Feedback.ResponsesJson rather than in a second new table — this is the only new table here.
/// </summary>
public class FeedbackQuestion : BaseAuditableEntity
{
    /// <summary>
    /// Owning organization. Always set, including for branch-scoped questions — it is the
    /// tenant boundary this entity is filtered on (there is no global query filter for it).
    /// </summary>
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// Branch this question belongs to. Null means "every branch in the organization", the same
    /// org-wide-default shape used elsewhere in this codebase.
    /// </summary>
    public Guid? BranchId { get; set; }

    [MaxLength(300)]
    public string QuestionText { get; set; } = string.Empty;

    public FeedbackQuestionType QuestionType { get; set; } = FeedbackQuestionType.Rating;

    /// <summary>
    /// JSON array of choice labels, e.g. ["Yes, easily","Somewhat","No"]. Only meaningful for
    /// <see cref="FeedbackQuestionType.SingleChoice"/>; null for every other type. A JSON column
    /// rather than an options table for the same reason as the answers above — options are only
    /// ever read back with their question and carry no per-option metadata.
    /// </summary>
    [MaxLength(2000)]
    public string? OptionsJson { get; set; }

    /// <summary>
    /// When set, the question is only asked to customers whose token used this service type.
    /// Null = asked for every service type.
    /// </summary>
    public Guid? ServiceTypeId { get; set; }

    /// <summary>Ascending order the questions are rendered in. Ties broken by CreatedAt.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>When true the public form will not submit without an answer to this question.</summary>
    public bool IsRequired { get; set; }

    // IsActive comes from BaseEntity — an inactive question is retained (so historical answers
    // still make sense) but never rendered on the public form.

    // Navigation properties
    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ServiceType? ServiceType { get; set; }
}
