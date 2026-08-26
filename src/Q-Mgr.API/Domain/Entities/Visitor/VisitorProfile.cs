using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Visitor;

/// <summary>
/// A real person, recognized across visits and across every branch of the organization —
/// the "returning visitor" record. One profile persists for as long as the person keeps
/// visiting; each individual visit is a separate <see cref="Visitor"/> row referencing it.
/// Identity fields (Email/Phone/IdNumber) are unique per organization when present — see
/// VisitorProfileConfiguration for the partial unique indexes enforcing that.
/// </summary>
public class VisitorProfile : BaseEntity
{
    public Guid OrganizationId { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string? IdNumber { get; set; }
    public string? PhotoUrl { get; set; }

    // Normalized forms of the three identity fields, kept in sync via VisitorMatching.Normalize*
    // whenever Phone/Email/IdNumber are set — case/formatting-insensitive matching and the
    // partial unique indexes (VisitorProfileConfiguration) both key off these, not the raw
    // display values, so "Jane@X.com" and "jane@x.com" are recognized as the same person.
    public string? NormalizedPhone { get; set; }
    public string? NormalizedEmail { get; set; }
    public string? NormalizedIdNumber { get; set; }

    // The watchlist is a property of the PERSON, not a single visit — flagging someone
    // follows them to every branch and every future visit.
    public bool IsWatchlisted { get; set; }
    public string? WatchlistReason { get; set; }

    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual ICollection<Visitor> Visits { get; set; } = new List<Visitor>();
}
