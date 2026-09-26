using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// One person's termly (or annual roll-up) appraisal: targets → self-assessment → appraiser →
/// moderation → signed, the cycle the MoES 2020 Performance Management Guidelines and Kenya's TPAD
/// both run. A workflow with stages, two authors, sign-off timestamps and a frozen score snapshot;
/// no existing row can carry it.
///
/// THE SCORE IS EVIDENCE, THE RATING IS A DECISION. <see cref="ComputedScore"/> and
/// <see cref="ComputedBreakdownJson"/> are frozen at Signed so a later record correction cannot
/// silently change a signed rating; <see cref="FinalRating"/> is set by a person, with the score,
/// the self-assessment and the evidence in view. Confidential by nature: visible to the subject,
/// the appraiser, the moderator and staff.confidential.view holders.
/// </summary>
public class StaffAppraisal : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid SubjectUserId { get; set; }

    /// <summary>"2026-T3", or "2026" for the annual roll-up.</summary>
    [MaxLength(20)]
    public string PeriodKey { get; set; } = string.Empty;

    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    public Guid AppraiserUserId { get; set; }
    public Guid? ModeratorUserId { get; set; }

    public AppraisalStage Stage { get; set; } = AppraisalStage.Open;

    /// <summary>JSON list of AppraisalTargetDto: the performance plan agreed at Open.</summary>
    public string? TargetsJson { get; set; }

    /// <summary>Frozen at Signed. Null while open (the live score is computed on read).</summary>
    public decimal? ComputedScore { get; set; }
    public string? ComputedBreakdownJson { get; set; }

    /// <summary>JSON list of ParameterRatingDto.</summary>
    public string? SelfRatingsJson { get; set; }
    public int? SelfRating { get; set; }
    [MaxLength(4000)] public string? SelfComments { get; set; }

    public int? AppraiserRating { get; set; }
    [MaxLength(4000)] public string? AppraiserComments { get; set; }

    /// <summary>1–5 on the tenant's band names (MoES's Excellent … Poor by default).</summary>
    public int? FinalRating { get; set; }

    [MaxLength(2000)] public string? Strengths { get; set; }
    [MaxLength(2000)] public string? DevelopmentAreas { get; set; }

    /// <summary>JSON list of SupportPlanItemDto (MoES Annex 12: gap → support option → by when).</summary>
    public string? SupportPlanJson { get; set; }

    /// <summary>JSON list of AppraisalTargetDto for the next period.</summary>
    public string? NextTargetsJson { get; set; }

    public DateTime? SelfSubmittedAt { get; set; }
    public DateTime? AppraiserSubmittedAt { get; set; }
    /// <summary>Who actually wrote the review — the appraiser, or an approver standing in. Sign-off is never by them
    /// (DutySeparation, 2026-09-26). Null on reviews written before that; the appraiser is assumed.</summary>
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ModeratedAt { get; set; }
    [MaxLength(1000)] public string? ModerationReason { get; set; }
    public DateTime? SignedAt { get; set; }
    public Guid? SignedByUserId { get; set; }

    [MaxLength(2000)] public string? AppealNote { get; set; }

    /// <summary>Stage reminders, one per row, with the 24h re-notify window.</summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>The signed PDF, published to the Library.</summary>
    public Guid? ReportMediaContentId { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Identity.User? Subject { get; set; }
    public virtual Identity.User? Appraiser { get; set; }
}
