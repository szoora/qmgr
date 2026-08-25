namespace QMgr.Application.DTOs;

public record UserInfo
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? FullName { get; init; }
    public Guid RoleId { get; init; }
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public string? RoleColor { get; init; }
    public Guid OrganizationId { get; init; }
    public string? OrganizationName { get; init; }
    public Guid? BranchId { get; init; }
    public List<string> Permissions { get; init; } = new();
}
