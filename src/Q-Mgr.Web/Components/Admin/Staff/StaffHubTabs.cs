using QMgr.Web.Components.Shared.UI;
using QMgr.Web.Services;

namespace QMgr.Web.Components.Admin.Staff;

/// <summary>
/// Which tabs a Staff Performance hub shows, and whether the caller may open the hub at all.
///
/// The hubs were built on 2026-09-18 by folding sixteen sidebar entries into six (the user's
/// direction: "related components can be grouped together... remove the duplication"). Each of the
/// folded pages carried its OWN permission gate and redirected to /unauthorized when it was not
/// met, which was correct while each had its own route and its own nav entry — the entry was simply
/// absent for someone who could not open it.
///
/// Inside a hub that stops being true twice over, so this is the one home for both halves:
///
///   * A tab whose section would refuse the caller must not be RENDERED. Otherwise the person
///     clicks a tab and is thrown out of the page they were already allowed to be on. That is the
///     same rule the handover states for the Import History button on Welfare Reports — an absent
///     control reads as an absent permission, a control that errors reads as a broken product.
///   * The HUB's own gate is the OR of its sections, not the permission of whichever section
///     happens to be first. Gating the Records hub on staff.records.view alone would take Reports
///     away from someone who holds staff.reports.view and nothing else — a permission they had
///     before the merge, silently lost to a layout change.
///
/// A section with no permission of its own passes an empty <c>anyOf</c> and is always shown.
/// </summary>
public static class StaffHubTabs
{
    /// <summary>A hub tab and the permissions that let its section open — any one of them is enough.</summary>
    public sealed record Section(QTab Tab, params string[] AnyOf);

    /// <summary>
    /// The sections this caller may open, in the order given. An empty result means the caller may
    /// not open the hub at all, and the hub should send them to /unauthorized rather than render
    /// an empty tab strip.
    /// </summary>
    public static async Task<List<QTab>> VisibleAsync(IPermissionService permissions, params Section[] sections)
    {
        var visible = new List<QTab>();
        foreach (var section in sections)
        {
            if (section.AnyOf.Length == 0) { visible.Add(section.Tab); continue; }

            foreach (var permission in section.AnyOf)
            {
                if (!await permissions.HasPermissionAsync(permission)) continue;
                visible.Add(section.Tab);
                break;
            }
        }
        return visible;
    }
}
