using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// A subject taught in the school (duty rota plan §3.1, §5.2) — Mathematics, Physics. A real table
/// because it is referenced by id from subject-teacher assignments, timetable lessons and lesson duties,
/// and reported on directly; the vocabulary DTO's own rule for "a real table". Belongs to a department,
/// so a head sees the teaching load of their subjects. Retired, never deleted (<c>Restrict</c> on delete).
/// </summary>
public class Subject : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Short and permanent ("MATH"): the staff import's <c>Teaches</c> column names subjects by it.</summary>
    [MaxLength(20)]
    public string Code { get; set; } = string.Empty;

    public Guid? DepartmentId { get; set; }

    [MaxLength(9)]
    public string? Color { get; set; }

    public int SortOrder { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Department? Department { get; set; }
}
