namespace QMgr.Application.DTOs;

using QMgr.Domain.Enums;

/// <summary>
/// One row of <c>GET api/v1/roles</c>: what a role is called, what it costs the tenant in seats,
/// and the two scope axes it carries.
///
/// <para><b>Moved to Q-Mgr.Shared on 2026-09-19</b>, which is this codebase's single-source-of-truth
/// location for a DTO that crosses the API/Web boundary. It lived in <c>RolesController.cs</c> and
/// the Web maintained TWO independent copies of the same wire shape — <c>UsersSetup</c>'s
/// <c>RoleApiDto</c> and <c>StaffNotices</c>'s four-field one — which is exactly the drift risk
/// CLAUDE.md records as this project's most-repeated bug (<c>OrganizationBrandingDto</c>,
/// <c>ContentDto</c>, <c>NotificationDto</c>, <c>UserInfo</c>, <c>SubscriptionPlan</c>). Adding a
/// third copy for the Add-staff dialog is what prompted the move.</para>
///
/// <para>There is no auto-mapper in this project, so a field added here and forgotten on a copy is
/// a silent runtime null rather than a compile error. One record is the only defence.</para>
/// </summary>
public record RoleListDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Color { get; init; }
    public string? Icon { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; }
    public int UserCount { get; init; }
    public int PermissionCount { get; init; }

    /// <summary>Which STUDENTS this role's holders may see. <see cref="IStudentScopeService"/> is its only reader.</summary>
    public RoleDataScope DataScope { get; init; }

    /// <summary>Which other STAFF this role's holders may see. <c>IStaffScopeService</c> is its only reader.</summary>
    public StaffDataScope StaffScope { get; init; }

    /// <summary>
    /// Which staff GROUP this role's holders belong to (teaching, support, or a group the school
    /// added). Null falls back to the tenant's first. Read by parameters and notices that apply to
    /// some staff rather than all — see <c>StaffGroups</c>.
    /// </summary>
    public string? StaffGroup { get; init; }
}
