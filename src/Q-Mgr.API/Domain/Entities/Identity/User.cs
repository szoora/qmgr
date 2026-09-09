using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Identity;

public class User : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
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
    public string NormalizedEmail { get; set; } = string.Empty;

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
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }

    /// <summary>Consecutive failed login attempts since the last successful login or lockout reset.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>Account locked out until this time (null = not locked out). Enforced in AuthController.Login.</summary>
    public DateTime? LockoutEnd { get; set; }

    /// <summary>Self-service password reset token (same raw-random-value convention as RefreshToken, not hashed). Null once used or expired.</summary>
    public string? PasswordResetToken { get; set; }

    /// <summary>Expiry for PasswordResetToken. AuthController.ResetPassword rejects the token past this time.</summary>
    public DateTime? PasswordResetTokenExpiry { get; set; }

    #region Navigation Properties

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Role Role { get; set; } = null!;
    public virtual Organization.Branch? AssignedBranch { get; set; }
    public virtual Queue.Counter? AssignedCounter { get; set; }
    public virtual ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();

    #endregion

    public string FullName => $"{FirstName} {LastName}".Trim();
}
