using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Visitor;

/// <summary>
/// A student on the visiting-day roster — the person being visited. Branch-scoped like Visitor
/// itself (a school with multiple campuses keeps each campus's roll separate). This is
/// deliberately NOT a general "person" record the way VisitorProfile is; a student is never
/// checked in themselves, they're the reason someone else is.
/// </summary>
public class Student : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    public string FullName { get; set; } = string.Empty;

    // The school's own admission/roll number — the natural key an external School Management
    // Information System (SMIS) uses to identify a student, and what a bulk roster import
    // upserts on (see StudentsController.BulkImport) so re-syncing the same roster twice doesn't
    // create duplicates. Unique per organization when present — see StudentConfiguration.
    public string? StudentCode { get; set; }

    public string? ClassName { get; set; }

    public bool IsActive { get; set; } = true;

    // Data-processing consent (the guardian/student agreeing to the school holding this welfare
    // and visiting-day data about them) — recorded here on the Student row rather than in a
    // separate consent-log table, since a single "currently given / not given, by whom, when,
    // with what caveat" is all anyone has asked for. Withdrawing consent clears all three.
    public DateTime? DataConsentGivenAt { get; set; }
    public Guid? DataConsentRecordedByUserId { get; set; }

    [MaxLength(500)]
    public string? DataConsentNotes { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ICollection<StudentGuardian> Guardians { get; set; } = new List<StudentGuardian>();
}
