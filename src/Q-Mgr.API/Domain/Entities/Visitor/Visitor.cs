using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Visitor;

/// <summary>
/// A single visit — one row per visit, referencing the persistent <see cref="VisitorProfile"/>
/// of the person visiting. Identity fields (name/phone/email/ID/photo) live on the profile, not
/// here — a visit only carries what's specific to this particular trip.
/// Branch-scoped like Token/Feedback/Counter (no global EF query filter — every controller
/// action reaching one by branchId must call VerifyBranchOwnership explicitly).
/// </summary>
public class Visitor : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid VisitorProfileId { get; set; }

    // Unique per-visit badge/reference code, e.g. "V-20260825-0001"
    public string BadgeCode { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    // Vehicle used on THIS visit specifically (not on the profile — the same person can arrive
    // in different vehicles on different trips), for parking/security logging.
    public string? VehiclePlate { get; set; }

    // Who they're visiting — captured as a name so a visit record still reads correctly
    // even if the host user is later deleted; HostUserId is the notification target.
    public Guid? HostUserId { get; set; }
    public string HostName { get; set; } = string.Empty;

    // Set when this visit came from the roster-driven "visiting day" flow rather than a plain
    // walk-in — the student being visited, denormalized to a name for the same reason HostName
    // is: the visit record should still read correctly if the student is later removed from the
    // roster (graduated, transferred) rather than going blank.
    public Guid? StudentId { get; set; }
    public string? StudentName { get; set; }

    public VisitorStatus Status { get; set; } = VisitorStatus.PreRegistered;

    public DateTime? ScheduledAt { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }

    public string? Notes { get; set; }

    // Set only when the branch requires visitor consent (Branch.Settings, "VisitorConsent" key)
    // and the visitor accepted it at check-in — the timestamp itself is the record that consent
    // was actually given, not just that it was required.
    public DateTime? ConsentGivenAt { get; set; }

    // Set only when this visit was checked in/out via a group VisitorPass rather than
    // individually — the pass's headcount is what's actually being enforced.
    public Guid? VisitorPassId { get; set; }

    // Soft delete: visitor logs are themselves a compliance/security artifact (evacuation
    // rosters, incident investigation) — a hard delete would destroy the very record that
    // justified logging the visit. Deleting hides the row from normal views but keeps it,
    // and who/why/when must always be captured together (DeletedByUserId non-null implies
    // DeletedAt and DeletionReason are also set).
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    // Navigation properties
    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Identity.User? HostUser { get; set; }
    public virtual VisitorProfile? VisitorProfile { get; set; }
    public virtual VisitorPass? VisitorPass { get; set; }
    public virtual Student? Student { get; set; }
}
