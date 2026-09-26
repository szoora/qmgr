using QMgr.Domain.Constants;
using QMgr.Web.Components.Shared.UI;
using QMgr.Web.Services;

namespace QMgr.Web.Components.Shared;

/// <summary>
/// "May this person see a link to X?" for the destinations that more than one place links to —
/// the sidebar, the user menu, the phone's More sheet, Home's quick actions and the account page.
/// A link is shown exactly when its page would open, and every place that draws one asks here.
///
/// <para>WHY THERE IS ONE (2026-09-25). The sidebar, the user menu and the phone sheet each decided
/// "may this person see Settings" on their own and gave three different answers: the sidebar used
/// <c>settings.view || notifications.view</c> (so every teacher saw it), the phone sheet used
/// <c>settings.view</c>, and the user menu and the account page used nothing at all. Billing was
/// gated in the sidebar and ungated on the phone. Each predicate below takes the two SYNCHRONOUS
/// predicates <c>MainLayout</c> already builds for the phone bar, so this stays a pure decision.</para>
/// </summary>
public static class NavGates
{
    /// <summary>
    /// The Settings hub's sections. The hub renders them; everything that links to the hub asks
    /// <see cref="SettingsHub"/>, which is the OR of these — the same rule <see cref="HubTabs"/>
    /// applies on the page. Only people who can CHANGE something here see it (decision D1,
    /// 2026-09-25): a read-only view of the school's messaging and integration configuration is
    /// not something a head of department, a teacher or a governor needs.
    /// </summary>
    public static readonly HubTabs.Section[] SettingsSections =
    {
        new(new QTab("general", "General", "sliders"), Permissions.SettingsEdit),
        new(new QTab("notifications", "Notifications", "bell-fill"), Permissions.NotificationsManage),
        new HubTabs.Section(new QTab("industry", "Industry", "buildings"), Permissions.SettingsEdit)
            .RequiringModule(ModuleCodes.CoreQueue),
        new HubTabs.Section(new QTab("integrations", "Integrations", "plug"), Permissions.SettingsEdit)
            .RequiringModule(ModuleCodes.IntegrationsApi),
    };

    public static bool SettingsHub(Func<string, bool> has, Func<string, bool> hasModule)
        => HubTabs.AnyVisible(SettingsSections, has, hasModule);

    /// <summary>Every Billing tab but Modules needs billing.view, and Modules is where a module gate sends people — not somewhere to link to.</summary>
    public static bool Billing(Func<string, bool> has) => has(Permissions.BillingView);

    /// <summary>A kiosk issues tickets; without tokens.create every press is refused.</summary>
    public static bool Kiosk(Func<string, bool> has, Func<string, bool> hasModule)
        => hasModule(ModuleCodes.CoreQueue) && has(Permissions.TokensCreate);

    public static bool CounterTerminal(Func<string, bool> has, Func<string, bool> hasModule)
        => hasModule(ModuleCodes.CoreQueue) && has(Permissions.QueueManage);

    /// <summary>The page requires queue.view; the sidebar used to offer it to a queue.manage holder too.</summary>
    public static bool Appointments(Func<string, bool> has, Func<string, bool> hasModule)
        => hasModule(ModuleCodes.CoreQueue) && has(Permissions.QueueView);

    /// <summary>A public screen: anybody who can see the queue may open it.</summary>
    public static bool CustomerDisplay(Func<string, bool> has, Func<string, bool> hasModule)
        => hasModule(ModuleCodes.CoreQueue) && (has(Permissions.QueueView) || has(Permissions.QueueManage));

    /// <summary>The overview is queue reporting: ModuleRouteMap sends /reports to Core Queue, so the link needs the module
    /// as the page does (found 2026-09-26 on a tenant with Core Queue cancelled — the link bounced to Billing).</summary>
    public static bool Reports(Func<string, bool> has, Func<string, bool> hasModule)
        => hasModule(ModuleCodes.CoreQueue) && has(Permissions.ReportsView);

    /// <summary>
    /// The Import inbox (calendar-audiences plan E11): anybody who approves any kind of section a document can be routed
    /// to — the calendar, meetings and rota, the timetable, the staff list, the roll. A person who approves nothing has
    /// nothing waiting there and no reason to send a document.
    /// </summary>
    public static bool ImportInbox(Func<string, bool> has)
        => has(Permissions.CalendarManage) || has(Permissions.StaffDutiesManage) || has(Permissions.TimetableManage)
           || has(Permissions.StaffStructureManage) || has(Permissions.StudentsManage);
}
