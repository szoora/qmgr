using QMgr.Web.Components.Shared.UI;
using QMgr.Web.Services;

namespace QMgr.Web.Components.Shared;

/// <summary>
/// Which tabs a hub shows, and whether the caller may open the hub at all.
///
/// The hubs began on 2026-09-18 by folding sixteen Staff Performance sidebar entries into six (the
/// user's direction: "related components can be grouped together... remove the duplication"), and
/// the Communication and Administration groups followed on 2026-09-19. Each of the folded pages
/// carried its OWN permission gate and redirected to /unauthorized when it was not met, which was
/// correct while each had its own route and its own nav entry — the entry was simply absent for
/// someone who could not open it.
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
///
/// <para>This was <c>StaffHubTabs</c> in <c>Components/Admin/Staff</c> until 2026-09-19. It moved
/// here rather than being copied when the second and third groups needed it: a second copy of a
/// rule that then drifts from the first is this codebase's most-repeated bug, and a gate deciding
/// who sees what is the worst place to have one.</para>
/// </summary>
public static class HubTabs
{
    /// <summary>
    /// A hub tab, the permissions that let its section open — any one of them is enough — and,
    /// optionally, the modules the tenant must hold for it to exist at all.
    /// </summary>
    public sealed record Section(QTab Tab, IReadOnlyList<string> AnyOf, IReadOnlyList<string>? AnyModule = null)
    {
        public Section(QTab tab, params string[] anyOf) : this(tab, (IReadOnlyList<string>)anyOf, null) { }

        /// <summary>
        /// The tenant must hold one of these modules. It exists because the Administration hubs
        /// cross module boundaries — Branches is base product while Counters and Service Types are
        /// Core Queue, Users &amp; Roles is base product while Join Requests and Onboarding belong
        /// to Welfare &amp; Performance. Each of those was its own route with its own
        /// <c>ModuleRouteMap</c> entry; folded into a hub, one route cannot carry several module
        /// requirements, so the requirement moves here, next to the permission it sits beside.
        /// </summary>
        public Section RequiringModule(params string[] modules) => this with { AnyModule = modules };
    }

    /// <summary>
    /// The sections this caller may open, in the order given. An empty result means the caller may
    /// not open the hub at all, and the hub should send them to /unauthorized rather than render
    /// an empty tab strip.
    /// </summary>
    public static Task<List<QTab>> VisibleAsync(IPermissionService permissions, params Section[] sections)
        => VisibleAsync(permissions, null, sections);

    /// <summary>
    /// The same, checking each section's module against what the tenant holds.
    ///
    /// <para><b>A failed module load shows everything.</b> If <paramref name="modules"/> is null,
    /// or its load did not succeed, the module half is skipped entirely — an API blip is not an
    /// absent module, and hiding half a hub's tabs over one is worse than briefly offering a tab
    /// whose section will explain itself. That is the same fail-open every page-level module
    /// redirect in this app already uses (<c>ModuleState.LoadSucceeded</c>).</para>
    /// </summary>
    public static async Task<List<QTab>> VisibleAsync(
        IPermissionService permissions, IModuleStateService? modules, params Section[] sections)
    {
        var checkModules = modules is { LoadSucceeded: true };

        var visible = new List<QTab>();
        foreach (var section in sections)
        {
            if (checkModules && section.AnyModule is { Count: > 0 } needed
                && !needed.Any(m => modules!.ActiveModuleCodes.Contains(m)))
                continue;

            if (section.AnyOf.Count == 0) { visible.Add(section.Tab); continue; }

            foreach (var permission in section.AnyOf)
            {
                if (!await permissions.HasPermissionAsync(permission)) continue;
                visible.Add(section.Tab);
                break;
            }
        }
        return visible;
    }

    /// <summary>
    /// Whether ANY section would be visible, from two synchronous predicates — for the places that
    /// link to a hub rather than render it (the sidebar, the user menu, the phone sheet; see
    /// <see cref="NavGates"/>). The same rule <see cref="VisibleAsync(IPermissionService, IModuleStateService?, Section[])"/>
    /// applies on the page, so a link is shown exactly when the hub would open.
    /// </summary>
    public static bool AnyVisible(IEnumerable<Section> sections, Func<string, bool> has, Func<string, bool> hasModule)
        => sections.Any(s =>
            (s.AnyModule is not { Count: > 0 } needed || needed.Any(hasModule))
            && (s.AnyOf.Count == 0 || s.AnyOf.Any(has)));

    /// <summary>
    /// The tab a hub should open on: the one <c>?tab=</c> asks for when the caller may see it,
    /// otherwise the first visible one. The HUB owns that query key and nothing inside it may bind
    /// the same one — <c>TeachingReports</c> had to move its own sub-view selector to <c>?view=</c>
    /// for exactly this reason, and two hubs shipped for a day not reading it at all, so every
    /// deep link landed on the default tab in silence.
    /// </summary>
    public static string Resolve(IReadOnlyList<QTab> visible, string? wanted)
    {
        if (visible.Count == 0) return string.Empty;
        var key = wanted?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(key) && visible.Any(t => t.Key == key) ? key : visible[0].Key;
    }

    /// <summary>
    /// Follow a <c>?tab=</c> that changed after the hub was initialised. Call it from
    /// <c>OnParametersSet</c>, passing the hub's own fields:
    /// <code>HubTabs.FollowQuery(tabs, TabParam, ref appliedTab, ref hubTab);</code>
    ///
    /// <para><b>Why a hub needs this at all.</b> A link from one tab of a hub to another — Join
    /// Requests offering "Join link and onboarding", Schedules offering "Playlists" — is a
    /// navigation to the SAME route with a different query. Blazor does not re-run
    /// <c>OnInitializedAsync</c> for that, so a hub that reads the query only there changes its
    /// URL and not its screen. Every such link used to be a separate route, which is why the
    /// problem is new.</para>
    ///
    /// <para><b>Why it compares against <paramref name="applied"/> and not against
    /// <paramref name="active"/>.</b> Clicking a tab moves <paramref name="active"/> without
    /// touching the query. Comparing to the active tab would make the next render snap the reader
    /// back to whatever the URL still said. This only acts when the QUERY itself changed.</para>
    /// </summary>
    /// <returns>True when the active tab was changed.</returns>
    public static bool FollowQuery(IReadOnlyList<QTab> tabs, string? tabParam, ref string? applied, ref string active)
    {
        if (tabs.Count == 0 || tabParam == applied) return false;
        applied = tabParam;

        var next = Resolve(tabs, tabParam);
        if (next == active) return false;
        active = next;
        return true;
    }
}
