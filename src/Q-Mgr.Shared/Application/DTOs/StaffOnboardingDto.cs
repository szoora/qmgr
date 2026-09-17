namespace QMgr.Application.DTOs;

// =====================================================================================================
// Staff onboarding (duty rota plan §12, 2026-09-17): temporary passwords with a forced change, the join
// link and its approval queue, the onboarding status page, and the first-sign-in checklist.
// =====================================================================================================

/// <summary>How the people in a staff import get their first way in (plan §12.2).</summary>
public enum StaffImportDeliveryMode
{
    /// <summary>A single-use link by email, valid 7 days; they choose their own password. The existing flow.</summary>
    Invitation = 0,
    /// <summary>A unique temporary password per person, shown once as printable slips. Expires in 72 hours.</summary>
    Slips = 1,
    /// <summary>The same temporary password, sent to the person's own phone by SMS.</summary>
    Sms = 2
}

/// <summary>
/// The tenant's onboarding settings, stored in <c>Organization.Settings["StaffOnboarding"]</c> and read
/// through <c>IStaffOnboardingPolicyService</c> only. The join code itself is never stored — only its
/// SHA-256 hash, like a share link's slug.
/// </summary>
public record StaffOnboardingPolicyDto
{
    /// <summary>Staff sign-up through the join link is switched on.</summary>
    public bool JoinEnabled { get; set; }
    /// <summary>Lower-case hex SHA-256 of the current join code. Null until a link is issued.</summary>
    public string? JoinCodeHash { get; set; }
    public DateTime? JoinCodeIssuedAt { get; set; }
    /// <summary>When the current link stops working. Null means it runs until rotated.</summary>
    public DateTime? JoinCodeExpiresAt { get; set; }
    /// <summary>When set, only addresses at these domains may apply ("school.ac.ug").</summary>
    public List<string> AllowedEmailDomains { get; set; } = new();
    /// <summary>The branch applicants join when the organization has more than one and they do not pick.</summary>
    public Guid? DefaultBranchId { get; set; }
    /// <summary>Always true in v1: nobody joins without an administrator.</summary>
    public bool RequireApproval { get; set; } = true;
    /// <summary>A request unanswered this long reads as expired (default 14 days).</summary>
    public int RequestExpiryDays { get; set; } = 14;
    /// <summary>Rejected and expired requests are deleted this long after (default 30 days).</summary>
    public int PurgeAfterDays { get; set; } = 30;
    /// <summary>The tenant's acceptable-use and data-protection notice (a StaffNotice requiring acknowledgement), shown in the first-sign-in checklist.</summary>
    public Guid? AcceptableUseNoticeId { get; set; }
}

/// <summary>What an administrator sees and edits; the hash never leaves the server.</summary>
public record StaffOnboardingSettingsDto
{
    public bool JoinEnabled { get; set; }
    public bool HasJoinLink { get; set; }
    public DateTime? JoinCodeIssuedAt { get; set; }
    public DateTime? JoinCodeExpiresAt { get; set; }
    public List<string> AllowedEmailDomains { get; set; } = new();
    public Guid? DefaultBranchId { get; set; }
    public int RequestExpiryDays { get; set; } = 14;
    public int PurgeAfterDays { get; set; } = 30;
    public Guid? AcceptableUseNoticeId { get; set; }
    public string? AcceptableUseNoticeTitle { get; set; }
    public int PendingRequests { get; set; }
}

public record SaveStaffOnboardingSettingsRequest
{
    public bool JoinEnabled { get; set; }
    public List<string> AllowedEmailDomains { get; set; } = new();
    public Guid? DefaultBranchId { get; set; }
    /// <summary>Days the current link stays valid from now; null keeps the link's expiry as it is, 0 removes it.</summary>
    public int? JoinLinkValidDays { get; set; }
    public int RequestExpiryDays { get; set; } = 14;
    public Guid? AcceptableUseNoticeId { get; set; }
}

/// <summary>Returned once, when a link is issued or rotated. The code cannot be shown again.</summary>
public record JoinLinkIssuedDto
{
    public string Code { get; init; } = string.Empty;
    /// <summary>Path only ("/join/{code}"); the page adds its own origin.</summary>
    public string Path { get; init; } = string.Empty;
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>The public join page's view of a valid link. Nothing about existing accounts.</summary>
public record JoinLinkInfoDto
{
    public string OrganizationName { get; init; } = string.Empty;
    public List<JoinBranchOptionDto> Branches { get; init; } = new();
    public List<string> AllowedEmailDomains { get; init; } = new();
    public int PasswordMinimumLength { get; init; }
}

public record JoinBranchOptionDto(Guid Id, string Name);

public record JoinEmailCodeRequest
{
    public string Email { get; set; } = string.Empty;
}

public record JoinApplicationRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    /// <summary>The six-digit code emailed to <see cref="Email"/>.</summary>
    public string EmailCode { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? JobTitle { get; set; }
    public Guid? BranchId { get; set; }
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public enum JoinRequestState { Pending = 0, Expired = 1, Rejected = 2 }

public record JoinRequestDto
{
    public Guid UserId { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? EmployeeNumber { get; init; }
    public string? JobTitle { get; init; }
    public Guid? BranchId { get; init; }
    public string? BranchName { get; init; }
    public DateTime RequestedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? RejectedAt { get; init; }
    public JoinRequestState State { get; init; }
    /// <summary>Why this might be somebody already on the system. For the approver only; the applicant is never told.</summary>
    public List<JoinDuplicateSignalDto> DuplicateSignals { get; init; } = new();
}

public record JoinDuplicateSignalDto(string Kind, string Detail, Guid? ExistingUserId);

public record ApproveJoinRequest
{
    public Guid RoleId { get; set; }
    public Guid? BranchId { get; set; }
    public List<Guid> DepartmentIds { get; set; } = new();
    public Guid? LineManagerUserId { get; set; }
    /// <summary>Classes this person is class teacher of ("S2A"). Applied through the class-teacher assignments.</summary>
    public List<string> ClassTeacherOf { get; set; } = new();
    /// <summary>Subjects taught, "MATH:S2A,S2B; PHY:S3A". Applied once subjects exist (plan §5).</summary>
    public string? Teaches { get; set; }
}

public record RejectJoinRequest
{
    public string? Reason { get; set; }
}

/// <summary>One person on the onboarding status page (plan §12.5).</summary>
public record OnboardingStatusRowDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? RoleName { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLogin { get; init; }
    public bool MustChangePassword { get; init; }
    public DateTime? TemporaryPasswordExpiresAt { get; init; }
    public bool TemporaryPasswordExpired { get; init; }
    public bool InvitationPending { get; init; }
    public bool AcknowledgedAcceptableUse { get; init; }
}

public record OnboardingStatusDto
{
    public List<OnboardingStatusRowDto> NotSignedIn { get; init; } = new();
    public List<OnboardingStatusRowDto> TemporaryPasswordExpired { get; init; } = new();
    public List<OnboardingStatusRowDto> NotAcknowledged { get; init; } = new();
    public Guid? AcceptableUseNoticeId { get; init; }
    public int PendingJoinRequests { get; init; }
}

public record ReissueAccessRequest
{
    public List<Guid> UserIds { get; set; } = new();
    public StaffImportDeliveryMode Mode { get; set; } = StaffImportDeliveryMode.Slips;
}

/// <summary>One printable slip. Returned once; the password is never stored readably and cannot be shown again.</summary>
public record TemporaryPasswordSlipDto
{
    public Guid? UserId { get; init; }
    public int? RowNumber { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string Email { get; init; } = string.Empty;
    public string TemporaryPassword { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
}

public record ReissueAccessResultDto
{
    public int Reissued { get; init; }
    public List<TemporaryPasswordSlipDto> Slips { get; init; } = new();
    public List<string> Messages { get; init; } = new();
}

/// <summary>What starting a staff import returns: the job, and — for slips and SMS — the temporary passwords, once.</summary>
public record StaffImportStartedDto
{
    public RosterImportJobDto Job { get; init; } = new();
    /// <summary>Keyed by row number. The page keeps them in memory, prints the created rows' slips once the job has finished, and they are never retrievable again.</summary>
    public List<TemporaryPasswordSlipDto> TemporaryPasswords { get; init; } = new();
}

/// <summary>The first-sign-in checklist on the portal (plan §12.3). Every item is derived from data; nothing is ticked by hand.</summary>
public record OnboardingChecklistDto
{
    public bool PhoneConfirmed { get; init; }
    public bool NotificationPreferencesReviewed { get; init; }
    public bool ProfilePhotoAdded { get; init; }
    public Guid? AcceptableUseNoticeId { get; init; }
    public bool AcceptableUseAcknowledged { get; init; }
    public bool IsComplete => PhoneConfirmed && NotificationPreferencesReviewed && ProfilePhotoAdded && (AcceptableUseNoticeId == null || AcceptableUseAcknowledged);
}

public record SetTemporaryPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}
