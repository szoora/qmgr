using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

/// <summary>
/// A live class-teacher assignment, with the teacher's contact detail folded in so the roster row
/// and the student profile can answer "who do I call about this child" without a second request.
///
/// Lives in Q-Mgr.Shared from its first commit rather than in Q-Mgr.API with a hand-copied twin in
/// Q-Mgr.Web/Services — the drift this codebase has hit four times (OrganizationBrandingDto,
/// ContentDto, NotificationDto, UserInfo) costs nothing to avoid on a type that does not exist yet.
/// </summary>
public record ClassTeacherDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string ClassName { get; init; } = string.Empty;

    public Guid UserId { get; init; }
    public ClassTeacherRole Role { get; init; }

    /// <summary>For a subject-teacher assignment: the subject and its planned periods a week.</summary>
    public Guid? SubjectId { get; init; }
    public string? SubjectName { get; init; }
    public string? SubjectCode { get; init; }
    public int? PeriodsPerWeek { get; init; }

    // --- The teacher's contact card. Read from the User row; never stored twice. ---
    public string FullName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? AlternatePhone { get; init; }
    public string? OfficeLocation { get; init; }
    public string? JobTitle { get; init; }
    public string? EmployeeNumber { get; init; }

    /// <summary>The teacher's RBAC role code — so the assignment screen can warn when somebody has been given a class but not the Class Teacher role (they would be notified but see the whole school, or nothing).</summary>
    public string RoleCode { get; init; } = string.Empty;

    /// <summary>False when the user account has been deactivated. The assignment stays visible either way — a class whose teacher's account is off needs to look wrong, not look empty.</summary>
    public bool UserIsActive { get; init; }

    public DateTime AssignedAt { get; init; }
    public string AssignedByName { get; init; } = "Unknown";

    /// <summary>Null while live. An ended assignment is only ever returned by the history endpoint.</summary>
    public DateTime? EndedAt { get; init; }
    public string? EndedByName { get; init; }
    public string? EndReason { get; init; }

    /// <summary>How many active students currently sit in this class. Zero on a live assignment is worth surfacing — usually a class-name typo on the roster.</summary>
    public int StudentCount { get; init; }
}

/// <summary>
/// One class and everybody attached to it. The shape the management page renders a row from.
/// </summary>
public record ClassTeacherCoverageDto
{
    public string ClassName { get; init; } = string.Empty;
    public string? Color { get; init; }
    public int StudentCount { get; init; }

    /// <summary>Null when nobody holds the class — the warning state the coverage view exists to surface.</summary>
    public ClassTeacherDto? ClassTeacher { get; init; }

    public List<ClassTeacherDto> Assistants { get; init; } = new();

    /// <summary>The class's level ("S2" for S2A), from the class vocabulary. Groups streams.</summary>
    public string? Level { get; init; }

    /// <summary>Subject → teacher for this class (duty rota plan §5.2), subject order.</summary>
    public List<ClassTeacherDto> SubjectTeachers { get; init; } = new();
}

/// <summary>
/// A class missing subjects its level teaches (plan §5.2 coverage). There is no stored curriculum per class, so
/// the curriculum of a class is what is taught in any stream of its level: S2B with no Physics teacher while S2A
/// has one is the silent gap — the timetable would never place S2B Physics and nobody would notice.
/// </summary>
public record ClassSubjectGapDto
{
    public string ClassName { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public List<string> MissingSubjects { get; init; } = new();
}

/// <summary>
/// The coverage report. Every field here is a way this feature silently fails, made visible:
/// a class with no teacher gets no alerts, a teacher with no class sees nothing at all, and a
/// student whose free-text ClassName matches no configured class is invisible to their own class
/// teacher — the last being safeguarding-relevant rather than cosmetic.
/// </summary>
public record ClassTeacherCoverageReportDto
{
    public List<ClassTeacherCoverageDto> Classes { get; init; } = new();

    /// <summary>Configured classes with no live class teacher and no assistant.</summary>
    public List<string> ClassesWithNoTeacher { get; init; } = new();

    /// <summary>Users holding a class-scoped role but no live assignment. They currently see nothing at all.</summary>
    public List<ClassTeacherOrphanUserDto> ScopedUsersWithNoClass { get; init; } = new();

    /// <summary>Class names held by active students that match no entry in the branch's class vocabulary.</summary>
    public List<ClassTeacherUnknownClassDto> UnknownStudentClasses { get; init; } = new();

    /// <summary>Classes with no subject teacher for a subject taught elsewhere in their level.</summary>
    public List<ClassSubjectGapDto> SubjectGaps { get; init; } = new();

    /// <summary>Subject-teacher assignments with no lesson in the published timetable (empty until a timetable is published).</summary>
    public List<ClassTeacherDto> SubjectTeachersWithNoLessons { get; init; } = new();

    /// <summary>Assignments whose planned periods a week differ from the periods the published timetable places.</summary>
    public List<PlannedPeriodsMismatchDto> PlannedPeriodMismatches { get; init; } = new();
}

public record PlannedPeriodsMismatchDto
{
    public ClassTeacherDto Assignment { get; init; } = new();
    public int Planned { get; init; }
    public int Timetabled { get; init; }
}

public record ClassTeacherOrphanUserDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

public record ClassTeacherUnknownClassDto
{
    public string ClassName { get; init; } = string.Empty;
    public int StudentCount { get; init; }
}

/// <summary>
/// Mutable (not init-only) — bound directly as a Blazor form model via @bind, same reasoning as
/// CreateWelfareRecordRequest and the visitor request records.
/// </summary>
public record AssignClassTeacherRequest
{
    [Required(ErrorMessage = "Pick a class")]
    [MaxLength(100)]
    public string ClassName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Pick a member of staff")]
    public Guid UserId { get; set; }

    public ClassTeacherRole Role { get; set; } = ClassTeacherRole.ClassTeacher;
}

public record EndClassTeacherRequest
{
    [MaxLength(500, ErrorMessage = "Keep the reason under 500 characters")]
    public string? Reason { get; set; }
}

/// <summary>
/// Patch body for a staff member's contact card. Every field is optional and null clears it —
/// this is a full replace of the contact block, not a merge, so the form can blank a field.
///
/// <para>Used by two endpoints, deliberately the same record: the class-teacher contact card
/// (<c>ClassTeachersController.UpdateContact</c>) and, since 2026-09-19, a member of staff
/// maintaining their OWN detail (<c>ProfileController.UpdateMyContact</c> since 2026-09-25; it was on the
/// portal, which a tenant without Welfare &amp; Performance does not have).
/// It is CONTACT ONLY and must stay that way — the portal endpoint cannot be persuaded to write an
/// employment field because there is nowhere in this record to put one. Employment terms,
/// qualification, registration number and national ID are the school's auditable record and go
/// through <see cref="UpdateStaffProfileRequest"/>, which needs staff.structure.manage.</para>
///
/// <para><c>JobTitle</c> is the exception and the self-service endpoint IGNORES it: what the school calls
/// somebody is the school's decision, not theirs.</para>
/// </summary>
public record UpdateStaffContactRequest
{
    [MaxLength(30, ErrorMessage = "Phone number cannot exceed 30 characters")]
    public string? Phone { get; set; }

    [MaxLength(30, ErrorMessage = "Alternate phone cannot exceed 30 characters")]
    public string? AlternatePhone { get; set; }

    [MaxLength(200, ErrorMessage = "Office location cannot exceed 200 characters")]
    public string? OfficeLocation { get; set; }

    [MaxLength(120, ErrorMessage = "Job title cannot exceed 120 characters")]
    public string? JobTitle { get; set; }

    /// <summary>Who to call about this member of staff, not about a student.</summary>
    [MaxLength(120, ErrorMessage = "Emergency contact name cannot exceed 120 characters")]
    public string? EmergencyContactName { get; set; }

    [MaxLength(30, ErrorMessage = "Emergency contact number cannot exceed 30 characters")]
    public string? EmergencyContactPhone { get; set; }
}

/// <summary>
/// A person's OWN contact detail, as <c>GET/PUT api/v1/profile/contact</c> answer it (2026-09-25).
/// The one home for self-maintained contact: My file and, for a tenant without My Workspace, the
/// account page both use it, so a phone number is edited in exactly one place.
/// </summary>
public record StaffContactDto
{
    public string? Phone { get; init; }
    /// <summary>When the phone was confirmed by a code; null once the number changes.</summary>
    public DateTime? PhoneVerifiedAt { get; init; }
    public string? AlternatePhone { get; init; }
    public string? OfficeLocation { get; init; }
    public string? EmergencyContactName { get; init; }
    public string? EmergencyContactPhone { get; init; }
}
