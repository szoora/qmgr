using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// A department a member of staff belongs to and a head of department heads. A TABLE rather than
/// a JSON vocabulary entry, by the vocabulary DTO's own rule (RosterDto.cs): it is foreign-keyed
/// by users (<c>User.DepartmentIds</c>), reported on directly, and a head's assignment target — a
/// JSON entry can be none of those. Org-scoped with an optional branch, like a service type.
///
/// Membership is <c>User.DepartmentIds uuid[]</c>, not a join table: a teacher of Maths and
/// Physics is in two departments and nothing is stored per membership beyond the id. Headship is
/// two nullable columns here. A membership-history table is the upgrade path if "who was in which
/// department when" is ever needed; the appraisal snapshot records the appraiser anyway, and
/// ActivityEvent records every change.
/// </summary>
public class Department : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }

    /// <summary>Null = organization-wide (a multi-campus tenant with one Maths department).</summary>
    public Guid? BranchId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Short stable code ("MATH"), used by the staff import to place people.</summary>
    [MaxLength(20)]
    public string Code { get; set; } = string.Empty;

    public Guid? HeadUserId { get; set; }
    public Guid? DeputyHeadUserId { get; set; }

    public int SortOrder { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Identity.User? Head { get; set; }
    public virtual Identity.User? DeputyHead { get; set; }
}
