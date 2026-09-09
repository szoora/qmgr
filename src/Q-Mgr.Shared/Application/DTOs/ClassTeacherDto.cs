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
}
