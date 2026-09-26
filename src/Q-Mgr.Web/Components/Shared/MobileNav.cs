using QMgr.Domain.Constants;
using QMgr.Web.Services;

namespace QMgr.Web.Components.Shared;

/// <summary>
/// WHICH FOUR DESTINATIONS A PERSON GETS ON A PHONE, and everything else behind More.
///
/// <para>This is the ONE home for that decision, beside <see cref="HubTabs"/> and for the same
/// reason: a gate that decides what somebody sees is the worst possible place for a second copy
/// that can drift. The bar, the More sheet and the e2e suite all read this.</para>
///
/// <para><b>Why a bar at all.</b> Under 768px every one of this app's 47 destinations sits behind
/// the hamburger, and hiding the main navigation cuts discoverability almost in half (Nielsen
/// Norman Group, <i>Hamburger Menus and Hidden Navigation Hurt UX Metrics</i>). Four slots plus
/// More is inside Material 3's three-to-five, and keeps Apple's warning about over-using a More tab
/// intact: More is the overflow, never where a role's daily task lives.</para>
///
/// <para><b>It is built from PERMISSIONS, not from the role code.</b> A Director of Studies also
/// teaches; a manager also covers the front desk. Reading the role would give each person one job.
/// Modules gate first, exactly as the sidebar does, so a bank never sees a welfare slot.</para>
/// </summary>
public static class MobileNav
{
    /// <summary>The bar never renders above this width; the sidebar is the navigation there.</summary>
    public const int BreakpointPx = 768;

    /// <summary>Four chosen slots plus More. Material 3: three to five, no more.</summary>
    public const int ChosenSlots = 4;

    /// <summary>
    /// One destination. <paramref name="Permission"/> and <paramref name="Module"/> are both
    /// optional: a slot with neither is open to anybody signed in.
    /// </summary>
    /// <param name="Key">Stable id, used by the e2e suite and for the active test.</param>
    /// <param name="Label">One or two words. The bar truncates, so a long label is a design error.</param>
    public sealed record Slot(
        string Key,
        string Label,
        string Icon,
        string Href,
        string? Permission = null,
        string? Module = null,
        // A rule that is not one permission — the Settings hub, which is the OR of its sections.
        // Takes the same two predicates as everything else here; see NavGates.
        Func<Func<string, bool>, Func<string, bool>, bool>? Gate = null);

    /// <summary>A named run of destinations in the More sheet, in sidebar order.</summary>
    public sealed record Group(string Title, IReadOnlyList<Slot> Items);

    // ---- The candidates, most specific first ---------------------------------------------------
    // ORDER IS THE POLICY. SlotsFor walks this list and takes the first four the caller can reach,
    // so a teacher gets My School Day before Dashboard and a front-desk user gets Queue before it.
    // Notifications is pinned separately below and is never taken from here.

    private static readonly Slot MySchoolDay = new("my-day", "My Day", "calendar-day", "/my-day", null, ModuleCodes.StudentWelfare);
    private static readonly Slot Portal = new("portal", "Workspace", "person-workspace", "/portal", null, ModuleCodes.StudentWelfare);
    private static readonly Slot Queue = new("queue", "Queue", "list-ol", "/queue/board", Permissions.QueueView, ModuleCodes.CoreQueue);
    private static readonly Slot Visitors = new("visitors", "Visitors", "person-badge", "/admin/visitors", Permissions.VisitorsView, ModuleCodes.VisitorManagement);
    private static readonly Slot Students = new("students", "Students", "people-fill", "/admin/students/roster", Permissions.StudentsView, ModuleCodes.StudentWelfare);
    private static readonly Slot Welfare = new("welfare", "Welfare", "clipboard2-pulse", "/admin/welfare-my-actions", Permissions.WelfareView, ModuleCodes.StudentWelfare);
    private static readonly Slot Duties = new("duties", "Duties", "calendar-check", "/admin/staff/duties", Permissions.StaffDutiesManage, ModuleCodes.StudentWelfare);
    private static readonly Slot Timetable = new("timetable", "Timetable", "grid-3x3-gap", "/admin/timetable", Permissions.TimetableManage, ModuleCodes.StudentWelfare);
    private static readonly Slot Staff = new("staff", "Staff", "person-lines-fill", "/admin/staff", Permissions.StaffRecordsView, ModuleCodes.StudentWelfare);
    private static readonly Slot Dashboard = new("dashboard", "Home", "house", "/", Permissions.DashboardView);
    /// <summary>The school calendar: base product, open to everybody signed in (TERM_PROGRAMME_CALENDAR_AND_GATES D8). A candidate since 2026-09-26, ahead of Home.</summary>
    private static readonly Slot Calendar = new("calendar", "Calendar", "calendar3", "/calendar");

    /// <summary>Always slot four. See <see cref="SlotsFor"/> for why it is pinned.</summary>
    public static readonly Slot Notifications = new("notifications", "Alerts", "bell", "/notifications");

    /// <summary>Always slot five. Opens the sheet rather than a route.</summary>
    public static readonly Slot More = new("more", "More", "list", "#more");

    private static readonly IReadOnlyList<Slot> Candidates = new[]
    {
        // A person's own day comes before anything organisation-wide: a phone is opened to find out
        // what is on now, not to read a dashboard of figures.
        MySchoolDay,
        Portal,         // the person's own hub sits beside their own day — the sidebar puts them side by side too
        Queue,          // the front desk's day IS the queue
        Students,
        Welfare,
        Timetable,
        Duties,
        Staff,
        Visitors,
        // The school calendar before Home (user, 2026-09-26): for anybody holding My Workspace, Home repeats it, and the
        // calendar is what a phone is opened to check. Home stays the fallback for a role that reaches nothing above.
        Calendar,
        Dashboard,    // the fallback, and the only slot a viewer reaches
    };

    /// <summary>
    /// The bar for this caller: at most four from <see cref="Candidates"/>, then Notifications,
    /// then More.
    ///
    /// <para><b>Notifications is pinned at four for everybody.</b> It is the one destination every
    /// seeded role holds, it carries the unread badge, and it is the thing a person opens the app
    /// <i>because of</i> — so the badge is visible without opening anything.</para>
    ///
    /// <para><b>A slot whose permission or module is absent is not rendered.</b> Three plus More is
    /// a legitimate bar; a greyed slot that refuses on press is not.</para>
    /// </summary>
    public static IReadOnlyList<Slot> SlotsFor(
        Func<string, bool> hasPermission,
        Func<string, bool> hasModule)
    {
        var chosen = new List<Slot>(ChosenSlots + 2);

        foreach (var slot in Candidates)
        {
            if (chosen.Count >= ChosenSlots) break;
            if (Allows(slot, hasPermission, hasModule)) chosen.Add(slot);
        }

        chosen.Add(Notifications);
        chosen.Add(More);
        return chosen;
    }

    /// <summary>
    /// Everything the caller can reach that is NOT already a slot, grouped as the sidebar groups it.
    /// The sheet shows this; the bar's own four are left out so nothing appears twice.
    /// </summary>
    public static IReadOnlyList<Group> MoreFor(
        Func<string, bool> hasPermission,
        Func<string, bool> hasModule,
        IReadOnlyList<Slot> barSlots)
    {
        var onBar = barSlots.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var groups = new List<Group>
        {
            new("My work", new Slot[] { Portal, MySchoolDay, Calendar, Dashboard }),
            new("Queue", new Slot[]
            {
                Queue,
                new("counter", "Counter Terminal", "display", "/queue/counter", Permissions.QueueManage, ModuleCodes.CoreQueue),
                new("appointments", "Appointments", "calendar-check", "/admin/appointments", Permissions.QueueView, ModuleCodes.CoreQueue),
            }),
            new("Visitors", new Slot[]
            {
                Visitors,
                new("expected", "Expected Visitors", "calendar-check", "/admin/visitors/expected", Permissions.VisitorsView, ModuleCodes.VisitorManagement),
                new("evacuation", "Evacuation Roll-Call", "fire", "/admin/visitors/evacuation", Permissions.VisitorsView, ModuleCodes.VisitorManagement),
            }),
            new("Student welfare", new Slot[]
            {
                Students,
                new("class-teachers", "Class Teachers", "person-video3", "/admin/staff?tab=class-teachers", Permissions.ClassTeachersManage, ModuleCodes.StudentWelfare),
                Welfare,
                new("welfare-reports", "Welfare Reports", "bar-chart-line-fill", "/admin/welfare-reports", Permissions.WelfareReportsView, ModuleCodes.StudentWelfare),
            }),
            new("Staff", new Slot[]
            {
                Staff,
                new("staff-records", "Records", "journal-check", "/admin/staff/records", Permissions.StaffRecordsView, ModuleCodes.StudentWelfare),
                Duties,
                Timetable,
                new("appraisals", "Appraisals", "clipboard2-check", "/admin/staff/appraisals", Permissions.StaffAppraisalsConduct, ModuleCodes.StudentWelfare),
                new("staff-setup", "Setup", "sliders2", "/admin/staff/parameters", Permissions.StaffParametersManage, ModuleCodes.StudentWelfare),
            }),
            new("Communication", new Slot[]
            {
                new("library", "Library", "images", "/content/library", Permissions.ContentView, ModuleCodes.EngagementCommunications),
                new("signage", "Signage", "easel-fill", "/content/signage", Permissions.ContentView, ModuleCodes.EngagementCommunications),
                new("broadcasts", "Broadcasts", "megaphone-fill", "/admin/marketing", Permissions.MarketingView, ModuleCodes.EngagementCommunications),
                new("feedback", "Feedback", "chat-dots", "/admin/feedback", Permissions.FeedbackView, ModuleCodes.EngagementCommunications),
            }),
            new("Reports", new Slot[]
            {
                new("reports", "Overview", "bar-chart", "/reports", Gate: NavGates.Reports),
            }),
            new("Administration", new Slot[]
            {
                new("branches", "Branches", "building", "/admin/branches", Permissions.BranchesView),
                new("users", "Users & Roles", "people", "/admin/users", Permissions.UsersView),
                new("appearance", "Appearance", "palette", "/admin/appearance", Permissions.SettingsView),
                new("settings", "Settings", "gear", "/admin/settings", Gate: NavGates.SettingsHub),
                new("billing", "Billing", "credit-card", "/billing", Permissions.BillingView),
            }),
            new("You", new Slot[]
            {
                new("profile", "My Profile", "person", "/profile"),
                new("prefs", "Notification Preferences", "sliders", "/profile/notifications"),
            }),
        };

        return groups
            .Select(g => new Group(g.Title, g.Items
                .Where(s => !onBar.Contains(s.Key) && Allows(s, hasPermission, hasModule))
                .ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
    }

    private static bool Allows(Slot slot, Func<string, bool> hasPermission, Func<string, bool> hasModule)
    {
        // Module first, the same order the sidebar uses: a tenant without the module does not see
        // the destination whatever their permissions say.
        if (slot.Module != null && !hasModule(slot.Module)) return false;
        if (slot.Gate != null && !slot.Gate(hasPermission, hasModule)) return false;
        return slot.Permission == null || hasPermission(slot.Permission);
    }

    /// <summary>
    /// Whether <paramref name="slot"/> is the one the reader is on. The path is compared with the
    /// QUERY STRIPPED, because a hub's `?tab=` must keep its slot lit — the same bug
    /// <c>MainLayout.IsActive</c> already had to fix for the sidebar.
    /// </summary>
    public static bool IsActive(Slot slot, string path)
    {
        var p = Normalize(path);
        var h = Normalize(slot.Href);
        if (h.Length == 0) return p.Length == 0;
        return p == h || p.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? path)
    {
        var s = (path ?? string.Empty).Trim();
        var q = s.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) s = s[..q];
        return s.Trim('/');
    }
}
