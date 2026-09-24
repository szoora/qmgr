using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;
using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Identity;

public class User : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// NULL when this person has no email address, which on a Ugandan school roll is most of the
    /// staff — 133 of 184 on the first real list this product imported. They sign in with
    /// <see cref="Username"/> (the login accepts either), receive their first password on a printed
    /// slip or by SMS, and are identified by <see cref="EmployeeNumber"/> where a duplicate check
    /// needs a key. Null rather than an empty string on purpose: two unique indexes sit on the
    /// address, and PostgreSQL treats NULLs as distinct while it would refuse a second "".
    /// See docs/plans/STAFF_WITHOUT_EMAIL.md.
    /// </summary>
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Phone { get; set; }

    /// <summary>
    /// Canonical form of <see cref="Email"/>, used to stop one mailbox opening unlimited trials:
    /// provider aliasing is folded so "j.o.h.n+2@gmail.com" and "john@gmail.com" share a value. A
    /// unique index sits on this, so the exact-match check on Email is no longer the only guard.
    /// Populated by RegistrationIdentity.NormalizeEmail; never shown to the user, who keeps seeing
    /// whatever they typed.
    /// </summary>
    public string? NormalizedEmail { get; set; }

    /// <summary>Digits-only form of <see cref="Phone"/>, with the local trunk prefix folded to a
    /// country code so "0753404044" and "+256753404044" compare equal.</summary>
    public string? NormalizedPhone { get; set; }

    /// <summary>
    /// When this number passed an SMS one-time code. A verified phone is the strongest identity
    /// signal available here: a SIM costs money and is registered against an ID, whereas an email
    /// address is free and unlimited. Null means unverified, and unverified numbers only ever
    /// contribute to a risk score rather than blocking anyone.
    /// </summary>
    public DateTime? PhoneVerifiedAt { get; set; }
    public string? EmployeeNumber { get; set; }

    // ---------------------------------------------------------------------------------------
    // Staff contact detail. Added for the class-teacher card — "who do I call about this child,
    // right now" — where Email/Phone/EmployeeNumber above were not quite enough. Columns on the
    // User row rather than a TeacherContact side table: these are attributes of a person who
    // already has a row, and a separate table would duplicate identity and immediately drift
    // (this codebase's recurring failure). All nullable; nothing existing is affected.
    // ---------------------------------------------------------------------------------------

    /// <summary>A second number — the staff-room landline, or a personal mobile for out-of-hours.</summary>
    public string? AlternatePhone { get; set; }

    /// <summary>Where to physically find them: "Staff room B", "Block C, office 4".</summary>
    public string? OfficeLocation { get; set; }

    /// <summary>Free text, e.g. "Head of Year 4" or "Senior Teacher". Distinct from the RBAC role, which is about permissions, not about what the school calls them.</summary>
    public string? JobTitle { get; set; }

    // ---------------------------------------------------------------------------------------
    // Staff structure (Staff Performance Monitor, 2026-09-16). Two columns on the row that already
    // exists rather than membership tables: a teacher of Maths and Physics is in two departments
    // and nothing is stored per membership beyond the id; a line manager is one scalar. Changes
    // to either are logged in ActivityEvent, which carries the history. A membership-history
    // table is the upgrade path if "who was in which department when" is ever needed.
    // ---------------------------------------------------------------------------------------

    /// <summary>The departments this person belongs to (Department.Id). Native uuid[] column.</summary>
    public Guid[]? DepartmentIds { get; set; }

    /// <summary>Who appraises and is told about this person. Drives StaffDataScope.DirectReports.</summary>
    public Guid? LineManagerUserId { get; set; }

    // ---- Staff record (2026-09-18) --------------------------------------------------------------
    // A staff member IS a user in this product — StaffLookups.BranchStaff is the only definition,
    // and there is no separate staff table. So the staff record widens this row rather than adding
    // one, per the standing "enhance an existing table before adding a new one" constraint. Every
    // field is NULLABLE: a bank running the queue module alone fills none of them, and a school
    // fills as many as its MoES return needs.
    //
    // DELIBERATELY NOT HERE, and it should stay that way: salary, bank or mobile-money details,
    // NSSF contributions, marital status, religion, tribe, and anything medical. This is a
    // front-office platform, not a payroll or HR system; collecting those would widen the blast
    // radius of a breach for data no feature reads. The same data-minimisation stance the welfare
    // plan takes when it declines to store HIV status or pregnancy.

    /// <summary>Date of appointment. Null means "not recorded", never "started today".</summary>
    public DateOnly? EmploymentStartDate { get; set; }

    /// <summary>
    /// Date the person left. A leaver is RECORDED, not erased: clearing IsActive would drop them
    /// out of the directory and out of every historical register, appraisal and duty report they
    /// legitimately appear on. StaffEmployment.StatusOf reads this with the start date.
    /// </summary>
    public DateOnly? EmploymentEndDate { get; set; }

    /// <summary>How they are engaged, by NAME from the school's own list (<c>StaffPerformancePolicyDto.EmploymentTypes</c>,
    /// read through <c>EmploymentTypes.Resolve</c>). An enum until 2026-09-23; the migration wrote each stored value
    /// as its old name.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(60)]
    public string? EmploymentType { get; set; }

    /// <summary>Highest qualification as the school records it, e.g. "Dip.Ed", "BSc Ed", "MEd".</summary>
    [MaxLength(120)]
    public string? Qualification { get; set; }

    /// <summary>
    /// MoES teacher registration / licence number. Free text because the format has changed over
    /// the years and an old certificate must still be recordable.
    /// </summary>
    [MaxLength(60)]
    public string? TeachingRegistrationNumber { get; set; }

    /// <summary>Used for the MoES staff return and for retirement-age planning. Not shown on the portal.</summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>The MoES staff return counts men and women separately. Nullable — see PersonSex.</summary>
    public PersonSex? Sex { get; set; }

    /// <summary>
    /// National Identification Number. Stored because a school's own staff file carries it and an
    /// inspection asks for it; never used as a login identifier or a lookup key.
    /// </summary>
    [MaxLength(40)]
    public string? NationalId { get; set; }

    /// <summary>Who to call about this member of staff, not about a student.</summary>
    [MaxLength(120)]
    public string? EmergencyContactName { get; set; }

    [MaxLength(40)]
    public string? EmergencyContactPhone { get; set; }

    /// <summary>
    /// Per-user notification channel overrides, as JSON. Null means "follow the organization
    /// default" (<c>NotificationSettings</c>), which is the state every existing row is in.
    ///
    /// A jsonb column rather than a table because the matrix is small, fixed, and only ever read
    /// one user at a time — the fan-out already has its recipient list before it asks about
    /// preferences, so this is never queried ACROSS users and never needs an index. Same shape as
    /// the existing <c>Branch.Settings</c> blob. Read and written only through
    /// <c>NotificationPreferenceResolver</c>; do not parse it anywhere else.
    /// </summary>
    public string? NotificationPreferences { get; set; }

    /// <summary>
    /// Reference to the user's role (database-backed RBAC)
    /// </summary>
    public Guid RoleId { get; set; }

    public Guid? AssignedBranchId { get; set; }
    public Guid? AssignedCounterId { get; set; }

    public DateTime? LastLogin { get; set; }

    /// <summary>
    /// The BROWSER's refresh token — one per user, raw, replaced on each use. Deliberately left
    /// exactly as it was when the mobile shell arrived: <see cref="UserDeviceSession"/> is the
    /// per-device store and moving the web onto it has a blast radius across every signed-in tab.
    /// See that class for why the two coexist.
    /// </summary>
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }

    /// <summary>
    /// The credential version, and the kill switch behind every signed-in device.
    ///
    /// <para>Rolled whenever this account's credentials change — a self-service reset, an
    /// administrator issuing a temporary password, a deactivation. Every
    /// <see cref="UserDeviceSession"/> keeps a copy of the value it was issued under, so rolling
    /// this signs out every handset on its next refresh without finding and deleting rows. Q-Mgr
    /// is not on ASP.NET Identity and had no security stamp, which is why this exists as a cheap
    /// nullable column rather than a second table.</para>
    ///
    /// <para><b>Null means "never rolled"</b> and matches a session issued before this column
    /// existed, so adding it did not sign anybody out. <c>CredentialStamps.Roll</c> is the one
    /// place that writes it — a second writer that forgets is a password change that leaves the
    /// phone signed in.</para>
    /// </summary>
    public string? CredentialStamp { get; set; }

    /// <summary>Consecutive failed login attempts since the last successful login or lockout reset.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>Account locked out until this time (null = not locked out). Enforced in AuthController.Login.</summary>
    public DateTime? LockoutEnd { get; set; }

    /// <summary>Self-service password reset token (same raw-random-value convention as RefreshToken, not hashed). Null once used or expired.</summary>
    public string? PasswordResetToken { get; set; }

    /// <summary>Expiry for PasswordResetToken. AuthController.ResetPassword rejects the token past this time.</summary>
    public DateTime? PasswordResetTokenExpiry { get; set; }

    // ---------------------------------------------------------------------------------------
    // Staff onboarding (duty rota plan §12, 2026-09-17). Columns on the row that already exists:
    // each is a state of this person's account, not a thing of its own.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The password on file is a temporary one — issued by a temporary-password import or an
    /// administrator's reset. Sign-in with it yields a password-change-only token and nothing else
    /// loads until the person chooses their own. Cleared by the change.
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>When the temporary password stops working (72 hours after issue). Past it, sign-in is refused with a message naming the administrator.</summary>
    public DateTime? TemporaryPasswordExpiresAt { get; set; }

    /// <summary>
    /// Set when this account is a self-registration waiting for an administrator (plan §12.4). Such
    /// an account is inactive, holds the viewer role as a placeholder, and cannot sign in. Cleared on
    /// approval. A request unanswered past the policy's expiry reads as expired.
    /// </summary>
    public DateTime? PendingApprovalAt { get; set; }

    /// <summary>
    /// When an administrator rejected this join request. The row is kept so the queue can show the
    /// outcome and the address cannot immediately re-apply, then deleted by the onboarding purge
    /// after the policy window (DPPA s.3: no applicant data without purpose).
    /// </summary>
    public DateTime? JoinRequestRejectedAt { get; set; }

    /// <summary>
    /// The person's profile photograph (onboarding checklist, plan §12.3). A stored-upload link like
    /// every other photo column: gated per file (UploadOwnerKind.StaffPhoto), signed when a DTO carries
    /// it, stripped of its token when a client sends it back.
    /// </summary>
    public string? PhotoUrl { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the secret in the person's private calendar feed link (plan TERM_PROGRAMME_CALENDAR_AND_GATES §9).
    /// The secret itself is shown once; replacing the link replaces the hash and the old link stops at once. Null = no feed.
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public string? CalendarFeedTokenHash { get; set; }

    public DateTime? CalendarFeedCreatedAt { get; set; }

    #region Navigation Properties

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Role Role { get; set; } = null!;
    public virtual Organization.Branch? AssignedBranch { get; set; }
    public virtual Queue.Counter? AssignedCounter { get; set; }
    public virtual ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();

    #endregion

    /// <summary>
    /// The name as this person's organisation writes it — given name first, or surname first, by the
    /// school's own choice (PersonNames, 2026-09-23). Not mapped; readable in a final projection only.
    /// </summary>
    public string FullName => QMgr.API.Application.Services.PersonNames.Display(OrganizationId, FirstName, LastName);
}
