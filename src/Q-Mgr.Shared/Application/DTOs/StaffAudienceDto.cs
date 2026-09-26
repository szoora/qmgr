using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// The staff audience — who among staff something is FOR (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING §5.1,
// 2026-09-26). A school event carries one; the programme import reads one out of a document's words; the
// picker edits one. StaffAudienceRule is the ONE test of membership, run by the API (who is told, who sees
// it) and by the Web (the live "42 people" line), so the number a keeper reads before saving is the number
// that is told.
// =====================================================================================================

/// <summary>
/// All staff, or the UNION of any staff groups, roles, departments and named people. Empty lists with
/// <see cref="AllStaff"/> false is an audience of nobody, which the editor refuses.
/// </summary>
public record StaffAudienceDto
{
    public bool AllStaff { get; set; } = true;
    /// <summary>Staff group names from the school's own list, compared with <c>StaffGroups.Key</c>.</summary>
    public List<string> StaffGroups { get; set; } = new();
    public List<string> RoleCodes { get; set; } = new();
    public List<Guid> DepartmentIds { get; set; } = new();
    public List<Guid> UserIds { get; set; } = new();

    public static StaffAudienceDto Everyone() => new() { AllStaff = true };

    public bool IsEmpty => !AllStaff && StaffGroups.Count == 0 && RoleCodes.Count == 0 && DepartmentIds.Count == 0 && UserIds.Count == 0;
}

/// <summary>One member of staff as an audience test needs them: their resolved group, their role, their departments.</summary>
public record AudienceMemberDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    /// <summary>What a list of people sorts on (the school's name order).</summary>
    public string? SortName { get; init; }
    public string? RoleCode { get; init; }
    public string? RoleName { get; init; }
    /// <summary>ALREADY RESOLVED by the server's <c>GroupFor</c>, fallback included — the client never re-derives it.</summary>
    public string? StaffGroup { get; init; }
    public List<Guid> DepartmentIds { get; init; } = new();
    /// <summary>The branch they are assigned to; null = every branch (how an organization-wide account is stored).</summary>
    public Guid? BranchId { get; init; }
}

public record AudienceRoleDto(string Code, string Name);

public record AudienceDepartmentDto(Guid Id, string Name, Guid? HeadUserId);

/// <summary>
/// What the audience picker offers. Served by the CALENDAR (base product), so a school without Welfare &amp;
/// Performance can still say "Teaching staff" or "the administrators": staff groups resolve on the server
/// without the module, through the policy's defaults.
/// </summary>
public record AudienceOptionsDto
{
    public List<string> StaffGroups { get; init; } = new();
    public List<AudienceRoleDto> Roles { get; init; } = new();
    public List<AudienceDepartmentDto> Departments { get; init; } = new();
    public List<AudienceMemberDto> People { get; init; } = new();
    /// <summary>People holding a class-teacher (pastoral) post today — what "class teachers" means in a document.</summary>
    public List<Guid> ClassTeacherUserIds { get; init; } = new();
}

/// <summary>THE membership test. Pure; the API and the Web both run it.</summary>
public static class StaffAudienceRule
{
    public static bool Includes(StaffAudienceDto audience, Guid userId, string? staffGroup, string? roleCode, IReadOnlyCollection<Guid>? departmentIds)
    {
        if (audience.AllStaff) return true;
        if (userId != Guid.Empty && audience.UserIds.Contains(userId)) return true;
        if (roleCode != null && audience.RoleCodes.Any(r => string.Equals(r, roleCode, StringComparison.OrdinalIgnoreCase))) return true;
        if (departmentIds is { Count: > 0 } && audience.DepartmentIds.Any(departmentIds.Contains)) return true;
        if (!string.IsNullOrWhiteSpace(staffGroup) && audience.StaffGroups.Any(g => Domain.Enums.StaffGroups.Key(g) == Domain.Enums.StaffGroups.Key(staffGroup)))
            return true;
        return false;
    }

    public static bool Includes(StaffAudienceDto audience, AudienceMemberDto m)
        => Includes(audience, m.UserId, m.StaffGroup, m.RoleCode, m.DepartmentIds);

    /// <summary>The people of <paramref name="people"/> the audience reaches.</summary>
    public static List<AudienceMemberDto> Members(StaffAudienceDto audience, IEnumerable<AudienceMemberDto> people)
        => people.Where(p => Includes(audience, p)).ToList();

    /// <summary>"Teaching staff, Administrator, Maths, 2 people" — the audience in words, for a row and a detail view.</summary>
    public static string Describe(StaffAudienceDto audience, Func<string, string>? roleName = null, Func<Guid, string?>? departmentName = null,
        Func<Guid, string?>? personName = null)
    {
        if (audience.AllStaff) return Domain.Enums.StaffGroups.AllLabel;
        var parts = new List<string>();
        parts.AddRange(audience.StaffGroups);
        parts.AddRange(audience.RoleCodes.Select(r => roleName?.Invoke(r) ?? r));
        parts.AddRange(audience.DepartmentIds.Select(d => departmentName?.Invoke(d)).Where(n => !string.IsNullOrWhiteSpace(n))!);
        var named = audience.UserIds.Select(u => personName?.Invoke(u)).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (named.Count is > 0 and <= 3) parts.AddRange(named!);
        else if (named.Count > 3) parts.Add($"{named.Count} people");
        else if (audience.UserIds.Count > 0) parts.Add(audience.UserIds.Count == 1 ? "1 person" : $"{audience.UserIds.Count} people");
        return parts.Count == 0 ? "Nobody" : string.Join(", ", parts);
    }
}
