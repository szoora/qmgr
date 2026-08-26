using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Visitor;

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

    public virtual Student? Student { get; set; }
    public virtual VisitorProfile? VisitorProfile { get; set; }
}
