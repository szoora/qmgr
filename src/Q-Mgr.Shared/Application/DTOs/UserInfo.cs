namespace QMgr.Application.DTOs;

public record UserInfo
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? FullName { get; init; }

    /// <summary>
    /// What a list of people is sorted on — the name in the organisation's chosen SORT order, which
    /// may differ from how it is shown (PeopleNameSettingsDto.SortOrder). Built on the server by
    /// PersonNames.SortKey; a list sorts on this and falls back to the full name when it is absent.
    /// </summary>
    public string? SortName { get; init; }

    public Guid RoleId { get; init; }
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public string? RoleColor { get; init; }
    public Guid OrganizationId { get; init; }
    public string? OrganizationName { get; init; }
    public Guid? BranchId { get; init; }
    public List<string> Permissions { get; init; } = new();

    /// <summary>
    /// The person's own photograph, already signed. A stored-upload link, so the token in it dies
    /// after an hour — QAvatar falls back to their initials when the image will not load, and the
    /// header re-reads this whenever AuthService raises CurrentUserChanged.
    /// </summary>
    public string? PhotoUrl { get; init; }

    /// <summary>
    /// The person signed in with a temporary password: the token they hold can only change it. The
    /// Web sends them to Set your password and loads nothing else (plan §12.3).
    /// </summary>
    public bool MustChangePassword { get; init; }
}
