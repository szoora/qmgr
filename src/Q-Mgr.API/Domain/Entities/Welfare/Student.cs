using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Welfare;

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

    // ---------------------------------------------------------------------------------------
    // Welfare background. Every one of these is nullable on purpose: the roster was built for
    // visiting-day check-in and existing rows have none of it, so a school that only ever
    // supplies name/code/class keeps working untouched and the bulk-import upsert path is
    // unaffected. Added as columns on this row rather than a "student profile" side table —
    // these are attributes of a student that already has a row, which is the cheap shape.
    // ---------------------------------------------------------------------------------------

    /// <summary>Age-appropriate response, and the difference between a 13- and a 17-year-old in the same incident. Also the clock the safeguarding retention job runs against — without it there is nothing to measure "until the subject's 25th birthday" from.</summary>
    public DateOnly? DateOfBirth { get; set; }

    public StudentSex? Sex { get; set; }

    /// <summary>A child three weeks into a new school is a different risk profile from one in their fourth year.</summary>
    public DateOnly? AdmissionDate { get; set; }

    /// <summary>Day / Boarder / Weekly — decides who is responsible overnight and whether "sent home" is even possible.</summary>
    public StudentResidency? Residency { get; set; }

    /// <summary>The pastoral unit. The house parent or matron is the first responder, and house is the natural grouping for every welfare report.</summary>
    [MaxLength(100)]
    public string? House { get; set; }

    /// <summary>Finer than house — where bullying and theft patterns actually cluster.</summary>
    [MaxLength(100)]
    public string? DormitoryOrStream { get; set; }

    [MaxLength(500)]
    public string? PhotoUrl { get; set; }

    // --- Home and family context ---

    /// <summary>The country the district belongs to. Stored beside it because a bare district name is ambiguous across borders — Busia is both a Ugandan district and a Kenyan county.</summary>
    [MaxLength(100)]
    public string? HomeCountry { get; set; }

    /// <summary>Distance from home for a boarder: how quickly a guardian can actually arrive in a crisis.</summary>
    [MaxLength(120)]
    public string? HomeDistrict { get; set; }

    [MaxLength(500)]
    public string? HomeAddress { get; set; }

    public StudentLivesWith? LivesWith { get; set; }

    /// <summary>Which language to hold a difficult conversation in, and whether the guardian can read the letter you send.</summary>
    [MaxLength(80)]
    public string? HomeLanguage { get; set; }

    [MaxLength(80)]
    public string? Religion { get; set; }

    // --- Health: a welfare summary for staff, deliberately NOT a clinical record. Four fields,
    // and it must never grow into a sick-bay log — that belongs in a system with its own access
    // control. Special-category data: gated behind the Pastoral visibility tier.

    [MaxLength(1000)]
    public string? MedicalConditions { get; set; }

    [MaxLength(1000)]
    public string? Allergies { get; set; }

    [MaxLength(1000)]
    public string? RegularMedication { get; set; }

    /// <summary>The field that most often converts a punishment into a support — the behaviour may be an unmet learning need or a communication difficulty.</summary>
    [MaxLength(1000)]
    public string? DisabilityOrLearningNeed { get; set; }

    // --- Continuity and welfare economics ---

    /// <summary>Not a finance field. Arrears predict absence, withdrawal and shame-driven behaviour.</summary>
    public StudentFeesStatus? FeesStatus { get; set; }

    [MaxLength(200)]
    public string? SponsorName { get; set; }

    public StudentTransportMode? TransportMode { get; set; }

    /// <summary>Mid-year transfers and repeated moves are a documented vulnerability marker.</summary>
    [MaxLength(200)]
    public string? PreviousSchool { get; set; }

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
    public virtual ICollection<StudentFlag> Flags { get; set; } = new List<StudentFlag>();
}
