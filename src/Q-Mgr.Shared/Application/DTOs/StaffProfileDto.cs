namespace QMgr.Application.DTOs;

using QMgr.Domain.Enums;

/// <summary>
/// A member of staff's own record — the fields a school's staff file carries and its MoES return
/// asks for, plus the contact detail somebody needs to reach them.
///
/// <para><b>Why this exists (2026-09-19).</b> Every field below has been on the <c>User</c> row
/// since 2026-09-18 and the bulk staff import has been writing them since — but there was no
/// per-person read endpoint, no write endpoint and no UI anywhere in the product. A typo in an
/// imported sheet was permanent, and a school entering its staff by hand could record none of it.
/// Reported as "we implemented other basic information relating to the staff member, but the
/// feature does not surface in the ui, there is no way of updating the staff member data".</para>
///
/// <para><b>The split matters and is enforced server-side, not in markup.</b> Two groups of field:
/// the person's own CONTACT detail, which they may maintain themselves from their portal because
/// they are the only one who knows when it changes; and the school's EMPLOYMENT record, which is
/// part of an auditable return and only a holder of <c>staff.structure.manage</c> may touch.
/// Letting somebody edit their own start date or teaching registration number would make the staff
/// return unauditable. See <see cref="UpdateStaffContactRequest"/> for the self-service half.</para>
///
/// <para><b>Deliberately absent, and it should stay that way</b> (the note on <c>User</c> says the
/// same): salary, bank or mobile-money details, NSSF, marital status, religion, tribe, anything
/// medical. This is a front-office platform, not a payroll or HR system.</para>
/// </summary>
public record StaffProfileDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public bool IsActive { get; init; }

    // ---- Contact: the person's own, editable from their portal ----------------------------
    public string? Phone { get; init; }
    public string? AlternatePhone { get; init; }
    public string? OfficeLocation { get; init; }
    public string? EmergencyContactName { get; init; }
    public string? EmergencyContactPhone { get; init; }

    // ---- Employment: the school's record, admin only --------------------------------------
    public string? JobTitle { get; init; }
    public string? EmployeeNumber { get; init; }

    /// <summary>
    /// Teaching or support. READ-ONLY and absent from every write request below, because it is not
    /// stored: <c>IStaffPerformancePolicyService.GroupFor</c> derives it from the person's role.
    /// Changing it means changing their role, which is <c>RoleAssignmentGuard</c>'s business.
    /// </summary>
    public StaffGroup StaffGroup { get; init; }

    public DateOnly? EmploymentStartDate { get; init; }
    public DateOnly? EmploymentEndDate { get; init; }
    public StaffEmploymentType? EmploymentType { get; init; }
    public string? Qualification { get; init; }
    public string? TeachingRegistrationNumber { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public PersonSex? Sex { get; init; }
    public string? NationalId { get; init; }

    // ---- Structure, read-only here (StaffStructureController owns writing it) ---------------
    public List<string> DepartmentNames { get; init; } = new();
    public string? LineManagerName { get; init; }

    /// <summary>
    /// True when the caller is reading their OWN profile from the portal. The employment half is
    /// still returned — a person may read their own file, and s.24 subject access assumes it — but
    /// the client uses this to render it read-only rather than as fields.
    /// </summary>
    public bool IsSelf { get; init; }
}

/// <summary>
/// The administrator's write. Every field is optional and null means "clear it": the form always
/// sends the whole record, so a null is a deliberate blank rather than an omission.
/// </summary>
public record UpdateStaffProfileRequest
{
    public string? Phone { get; set; }
    public string? AlternatePhone { get; set; }
    public string? OfficeLocation { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }

    public string? JobTitle { get; set; }
    public string? EmployeeNumber { get; set; }
    public DateOnly? EmploymentStartDate { get; set; }
    public DateOnly? EmploymentEndDate { get; set; }
    public StaffEmploymentType? EmploymentType { get; set; }
    public string? Qualification { get; set; }
    public string? TeachingRegistrationNumber { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public PersonSex? Sex { get; set; }
    public string? NationalId { get; set; }
}

// The self-service write reuses UpdateStaffContactRequest, which already exists in
// ClassTeacherDto.cs for the class-teacher contact card and carries exactly the right shape:
// contact only, no employment field, so the endpoint cannot be persuaded to write one because it
// has nowhere to put it. A second record of the same name is how this codebase's most-repeated
// bug starts, so the two emergency-contact fields were added to that one instead.

/// <summary>
/// Creating a member of staff from the Staff Directory, without being sent to Users &amp; Roles.
/// It is the same user-creation path underneath — <c>RoleAssignmentGuard</c>, the temporary
/// password and the onboarding flow all still apply — with the staff fields the directory cares
/// about filled in at the same time.
/// </summary>
public record CreateStaffMemberRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public Guid RoleId { get; set; }
    public string? JobTitle { get; set; }
    public List<Guid> DepartmentIds { get; set; } = new();
    public Guid? LineManagerUserId { get; set; }
}

/// <summary>What the Add-staff dialog needs back: who was created, and the password to hand over.</summary>
public record CreateStaffMemberResult
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// The temporary password, shown once. Null when the account was created without one (an
    /// invitation was emailed instead). The person must change it at first sign-in — a password is
    /// temporary after an import or an administrator's creation, which is the plan's §12.3 rule.
    /// </summary>
    public string? TemporaryPassword { get; init; }
}
