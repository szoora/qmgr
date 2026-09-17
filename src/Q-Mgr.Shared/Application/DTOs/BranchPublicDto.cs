using QMgr.Domain.Enums;

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

    /// <summary>
    /// Whether this organization holds Engagement &amp; Communications, so an unauthenticated
    /// display knows to render the advertising zone and the banner at all.
    /// </summary>
    /// <remarks>
    /// The customer display belongs to Core Queue Management, but the content inside it — playlists,
    /// campaigns, the banner — belongs to Engagement. Without this the display asked for that
    /// content unconditionally and a queue-only organization made four refused requests on every
    /// screen load, forever. A display screen is not signed in, so it cannot ask
    /// <c>modules/mine</c>; it already fetches this record, and "does this organization run
    /// signage" is not sensitive.
    /// </remarks>
    public bool HasEngagementContent { get; init; }

    /// <summary>
    /// The organization's industry, which picks the kiosk's welcome wording, icon and accent.
    /// Set by an administrator in Organization settings. Public for the same reason the name is:
    /// the kiosk shows it to anybody standing in front of it.
    /// </summary>
    public IndustryType Industry { get; init; } = IndustryType.Service;
}
