namespace QMgr.Application.DTOs;

/// <summary>
/// A person's own account, as <c>GET/PUT api/v1/profile</c> answer it. In Shared since 2026-09-25:
/// the account page kept its own private copy of this shape, the DTO-duplication pattern this
/// codebase keeps finding.
/// </summary>
public record ProfileDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public DateTime? PhoneVerifiedAt { get; init; }
    public string? PhotoUrl { get; init; }
    public string? EmployeeNumber { get; init; }
    public string Role { get; init; } = string.Empty;
    public Guid? AssignedBranchId { get; init; }
    public string? AssignedBranchName { get; init; }
    public DateTime? LastLogin { get; init; }
    public DateTime CreatedAt { get; init; }
}
