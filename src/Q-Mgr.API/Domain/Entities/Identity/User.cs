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
    public string? EmployeeNumber { get; set; }

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

    #region Navigation Properties

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Role Role { get; set; } = null!;
    public virtual Organization.Branch? AssignedBranch { get; set; }
    public virtual Queue.Counter? AssignedCounter { get; set; }
    public virtual ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();

    #endregion

    public string FullName => $"{FirstName} {LastName}".Trim();
}
