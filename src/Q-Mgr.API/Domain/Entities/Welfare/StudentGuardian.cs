using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace QMgr.Domain.Entities.Welfare;

/// <summary>
/// Links a Student to a person authorized to visit them — that person IS a VisitorProfile, not a
/// separately-stored name/phone/email. Reusing VisitorProfile (rather than duplicating identity
/// fields here) means a guardian is automatically found by the same returning-visitor search used
/// everywhere else, gets the same watchlist/history tracking, and a bulk roster import that
/// re-uploads the same guardian for two siblings correctly recognizes them as one person instead
/// of creating two profiles — the exact matching problem VisitorProfile/VisitorMatching already
/// solve, not a new one to solve twice.
/// </summary>
public class StudentGuardian : BaseEntity
{
    public Guid StudentId { get; set; }
    public Guid VisitorProfileId { get; set; }

    // Free text on purpose (not an enum) — schools use inconsistent vocabulary ("Guardian",
    // "Auntie", "Sponsor") and forcing a fixed list would just push real answers into "Other".
    public string Relationship { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The per-child restriction the organization-wide watchlist cannot express.
    /// <c>VisitorProfile.IsWatchlisted</c> bars a person from the whole site; a custody order
    /// routinely bars contact with one child while leaving a sibling unaffected. Because this is
    /// a property of the RELATIONSHIP rather than of the person, it belongs here on the link and
    /// nowhere else. Surfaced at gate search, where the decision is actually made.
    /// </summary>
    public GuardianContactRestriction ContactRestriction { get; set; } = GuardianContactRestriction.None;

    /// <summary>
    /// Why the restriction exists — "court order dated…", the thing a matron needs when a father
    /// argues at the gate. Confidential tier: it names a third party and a legal circumstance, so
    /// it is stripped for callers without <c>StudentsViewConfidential</c> while the restriction
    /// itself stays visible to everyone who has to enforce it.
    /// </summary>
    [MaxLength(1000)]
    public string? RestrictionReason { get; set; }

    /// <summary>Who may consent to medical treatment, a trip, or an exclusion decision.</summary>
    public bool? HasLegalCustody { get; set; }

    /// <summary>Who to ring first. The roster had no ordering at all before this.</summary>
    public bool IsPrimaryContact { get; set; }

    /// <summary>Second, third, fourth. A guardian who cannot be reached is the most common failure in a real emergency.</summary>
    public int? ContactPriority { get; set; }

    /// <summary>Distinguishes the aunt the child lives with from the father who pays the fees.</summary>
    public bool? LivesWithStudent { get; set; }

    public virtual Student? Student { get; set; }
    public virtual VisitorProfile? VisitorProfile { get; set; }
}
