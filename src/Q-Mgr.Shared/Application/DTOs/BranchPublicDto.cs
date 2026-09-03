namespace QMgr.Application.DTOs;

/// <summary>
/// Minimal, safe-to-expose-publicly identity of a branch, served anonymously by
/// <c>GET api/v1/branches/{branchId}/public</c> to the unauthenticated kiosk /
/// customer-display / signage / feedback pages so they can (a) show the real branch
/// and organization name in their header and (b) tell a valid branch link apart from
/// a stale or mistyped one (404). Deliberately nothing else from Branch (address,
/// timezone, counter counts) — those belong to the authenticated BranchDto.
///
/// Lives in Q-Mgr.Shared (like OrganizationBrandingDto) so Q-Mgr.API and Q-Mgr.Web
/// reference one type rather than each keeping a drift-prone copy.
/// </summary>
public record BranchPublicDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string OrganizationName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}
