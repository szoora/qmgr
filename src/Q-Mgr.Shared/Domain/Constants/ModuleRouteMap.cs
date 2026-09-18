namespace QMgr.Domain.Constants;

/// <summary>
/// The single declaration of which routes belong to which purchasable module, for both the API and
/// the Blazor app.
/// <para>
/// Before this existed, module gating was a per-controller <c>[RequireModule]</c> attribute and a
/// per-page <c>HasModule(...)</c> call, so coverage depended on remembering to add both. It was
/// missed in exactly the places you would expect: the whole Administration nav group and the
/// dashboard kept offering Counters, Service Types, Kiosk and Printer setup, plus "Open Kiosk" and
/// "Counter Terminal" shortcuts, to organizations that had never bought Core Queue Management —
/// and typing the URL directly reached the page.
/// </para>
/// <para>
/// Everything is declared here once and read by three enforcement points: the API's
/// <c>ModuleAccessMiddleware</c> (so a new endpoint under a mapped route is gated even if nobody
/// adds the attribute), the Blazor layout's navigation guard (so a pasted URL is refused, not just
/// hidden), and the sidebar itself (so what is shown and what is reachable can never disagree —
/// they read the same table).
/// </para>
/// <para>
/// Anything not listed here is deliberately available to every tenant regardless of modules, and is
/// governed by RBAC permissions alone: the dashboard shell, Branches, Users &amp; Roles, Industry and
/// Branding settings, Notifications, System Settings, Billing, Profile and Docs. Adding a route
/// here makes it a paid feature; leaving it out keeps it part of the base product.
/// </para>
/// </summary>
public static class ModuleRouteMap
{
    /// <summary>
    /// Blazor page paths, matched as prefixes against the browser path (leading slash, lower case).
    /// Order matters: the first match wins, so a more specific path must precede a broader one.
    /// </summary>
    public static readonly IReadOnlyList<(string Path, string Module)> WebRoutes = new[]
    {
        // ---- Core Queue Management ----
        ("/queue/board", ModuleCodes.CoreQueue),
        ("/queue/counter", ModuleCodes.CoreQueue),
        ("/queue/kiosk", ModuleCodes.CoreQueue),
        ("/queue/display", ModuleCodes.CoreQueue),
        ("/kiosk", ModuleCodes.CoreQueue),
        ("/display/signage", ModuleCodes.EngagementCommunications),
        ("/display", ModuleCodes.CoreQueue),
        ("/join", ModuleCodes.CoreQueue),
        ("/ticket", ModuleCodes.CoreQueue),
        ("/book", ModuleCodes.CoreQueue),
        ("/admin/appointments", ModuleCodes.CoreQueue),
        ("/admin/counters", ModuleCodes.CoreQueue),
        ("/admin/service-types", ModuleCodes.CoreQueue),
        ("/admin/printer-settings", ModuleCodes.CoreQueue),
        ("/admin/kiosk-settings", ModuleCodes.CoreQueue),
        ("/admin/customer-links", ModuleCodes.CoreQueue),
        ("/reports/queue", ModuleCodes.CoreQueue),
        ("/reports/counters", ModuleCodes.CoreQueue),

        // ---- Engagement & Communications ----
        ("/content", ModuleCodes.EngagementCommunications),
        ("/admin/marketing", ModuleCodes.EngagementCommunications),
        ("/admin/feedback", ModuleCodes.EngagementCommunications),
        ("/reports/feedback", ModuleCodes.EngagementCommunications),

        // ---- Visitor Management ----
        // Matching is per whole segment, so "/admin/welfare" would not cover "/admin/welfare-reports".
        // That strictness is deliberate (it stops "/admin/visitors" swallowing an unrelated
        // "/admin/visitors-something"), which means each hyphenated route is listed on its own.
        // The roster moved to /admin/students/roster when Student Welfare and Visitor Management
        // were fully separated; the old path is still routable so existing links keep working, and
        // both map to Student Welfare. The old one is listed before "/admin/visitors" so it wins
        // the match rather than being claimed by Visitor Management.
        ("/admin/visitors/roster", ModuleCodes.StudentWelfare),
        ("/admin/students/roster", ModuleCodes.StudentWelfare),
        ("/admin/visitors", ModuleCodes.VisitorManagement),
        ("/reports/visitors", ModuleCodes.VisitorManagement),

        // ---- Student Welfare ----
        ("/admin/students", ModuleCodes.StudentWelfare),
        ("/admin/welfare-categories", ModuleCodes.StudentWelfare),
        ("/admin/welfare-my-actions", ModuleCodes.StudentWelfare),
        ("/admin/welfare-reports", ModuleCodes.StudentWelfare),

        // Industry type is read by exactly two things: the kiosk, which themes itself and writes
        // its welcome copy from it, and tenant provisioning, which seeds default service types
        // from it. Both are Core Queue. The page says as much in its own subtitle ("customize the
        // kiosk experience") and its main action opens /queue/kiosk — a route this same table
        // already refuses without the module, so the page was offering a button that bounced.
        ("/admin/industry", ModuleCodes.CoreQueue),

        // ---- Staff Performance (part of Student Welfare since 2026-09-17) ----
        // The portal is every staff member's own page and the admin pages all live under one
        // prefix. /notifications is deliberately NOT listed: the notification centre is base
        // product, it merely gains staff event keys when the module is on.
        ("/portal", ModuleCodes.StudentWelfare),
        ("/admin/staff", ModuleCodes.StudentWelfare),
        // The timetable (duty rota plan §6): API under api/v1/branches/{b}/timetable.
        ("/admin/timetable", ModuleCodes.StudentWelfare),
        // My Day (duty rota plan §7.2): API under api/v1/branches/{b}/staff/lessons.
        ("/my-day", ModuleCodes.StudentWelfare),
        // Staff onboarding (duty rota plan §12, 2026-09-17): Users & Roles itself stays base product; the
        // join requests and onboarding pages beneath it belong to the module their API belongs to.
        ("/admin/users/requests", ModuleCodes.StudentWelfare),
        ("/admin/users/onboarding", ModuleCodes.StudentWelfare),

        // ---- Integrations & API Access ----
        ("/admin/api-clients", ModuleCodes.IntegrationsApi),
        ("/admin/integrations", ModuleCodes.IntegrationsApi),

        // Declared last on purpose: matching is first-hit and by segment prefix, so the specific
        // /reports/visitors and /reports/feedback entries above must win before this catches the
        // remaining /reports pages, which are all queue and counter metrics.
        ("/reports", ModuleCodes.CoreQueue),
    };

    /// <summary>
    /// Pages that earn their place through any one of several modules, rather than exactly one.
    /// Checked before <see cref="WebRoutes"/>, and satisfied when the tenant holds at least one.
    /// <para>
    /// This exists for a real shape the single-module table cannot express. Branding Settings
    /// configures only public-facing surfaces — its own cards say so, one per card: the display
    /// theme covers "your public customer-display and kiosk screens", the logo is "shown on public
    /// kiosk and display screens", the colors are "applied as CSS accent colors on kiosk and
    /// display screens", and the banner runs on "Customer Display and Full-Screen Signage". None
    /// of it touches the admin shell. Customer Display, the kiosk, Join and Ticket belong to Core
    /// Queue; Signage and the feedback pages belong to Engagement. So the page is worth opening
    /// with either module and worth nothing with neither — which is what a welfare-only school
    /// was being shown.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<(string Path, string[] Modules)> WebRoutesAnyOf = new[]
    {
        ("/admin/branding-settings", new[] { ModuleCodes.CoreQueue, ModuleCodes.EngagementCommunications }),
    };

    /// <summary>
    /// API route templates (as ASP.NET Core reports them on the matched endpoint, no leading
    /// slash), matched as prefixes with route parameters left in their <c>{name}</c> form so the
    /// entries read like the controller attributes they mirror. First match wins.
    /// </summary>
    public static readonly IReadOnlyList<(string Template, string Module)> ApiRoutes = new[]
    {
        // ---- Core Queue Management ----
        ("api/v1/branches/{branchId}/tokens", ModuleCodes.CoreQueue),
        ("api/v1/branches/{branchId}/queue", ModuleCodes.CoreQueue),
        ("api/v1/branches/{branchId}/counters", ModuleCodes.CoreQueue),
        ("api/v1/branches/{branchId}/service-types", ModuleCodes.CoreQueue),
        ("api/v1/branches/{branchId}/kiosk-settings", ModuleCodes.CoreQueue),
        ("api/v1/branches/{branchId}/printer-settings", ModuleCodes.CoreQueue),
        // The feedback report belongs to Engagement, and ReportsController says so on the endpoints
        // themselves. It must be listed before the broad "reports" entry or the middleware — which
        // matches by path and runs before the attribute — claims it for Core Queue and refuses an
        // Engagement-only tenant access to their own feedback export. Found by the module-coupling
        // scan on 2026-09-04, confirmed as a live 403.
        ("api/v1/branches/{branchId}/reports/feedback", ModuleCodes.EngagementCommunications),
        ("api/v1/branches/{branchId}/reports", ModuleCodes.CoreQueue),
        ("api/v1/branches/{branchId}/appointments", ModuleCodes.CoreQueue),
        ("api/v1/appointments", ModuleCodes.CoreQueue),
        ("api/v1/counters", ModuleCodes.CoreQueue),
        ("api/v1/printers", ModuleCodes.CoreQueue),

        // ---- Engagement & Communications ----
        ("api/v1/branches/{branchId}/playlists", ModuleCodes.EngagementCommunications),
        ("api/v1/branches/{branchId}/displays", ModuleCodes.EngagementCommunications),
        ("api/v1/branches/{branchId}/campaigns", ModuleCodes.EngagementCommunications),
        ("api/v1/branches/{branchId}/display-banner", ModuleCodes.EngagementCommunications),
        ("api/v1/branches/{branchId}/feedback", ModuleCodes.EngagementCommunications),
        ("api/v1/playlists", ModuleCodes.EngagementCommunications),
        ("api/v1/displays", ModuleCodes.EngagementCommunications),
        ("api/v1/media", ModuleCodes.EngagementCommunications),
        // Narrower than the OrganizationsController routes that share this prefix, and matched per
        // segment, so organization branding and settings stay ungated.
        ("api/v1/organizations/{organizationId}/media", ModuleCodes.EngagementCommunications),
        ("api/v1/campaigns", ModuleCodes.EngagementCommunications),
        ("api/v1/marketing/broadcasts", ModuleCodes.EngagementCommunications),
        ("api/v1/marketing/contacts", ModuleCodes.EngagementCommunications),
        // Note: api/v1/marketing/unsubscribe is deliberately absent. An unsubscribe link has to keep
        // working after a tenant drops the module, or previously-sent mail becomes a dead end.
        ("api/v1/spotify", ModuleCodes.EngagementCommunications),

        // ---- Visitor Management ----
        ("api/v1/branches/{branchId}/visitors", ModuleCodes.VisitorManagement),
        ("api/v1/branches/{branchId}/visitor-passes", ModuleCodes.VisitorManagement),

        // ---- Student Welfare ----
        ("api/v1/branches/{branchId}/students", ModuleCodes.StudentWelfare),
        ("api/v1/branches/{branchId}/welfare", ModuleCodes.StudentWelfare),
        ("api/v1/branches/{branchId}/welfare-records", ModuleCodes.StudentWelfare),

        // ---- Staff Performance (part of Student Welfare) ----
        ("api/v1/branches/{branchId}/staff", ModuleCodes.StudentWelfare),
        ("api/v1/staff", ModuleCodes.StudentWelfare),
        ("api/v1/staff-onboarding", ModuleCodes.StudentWelfare),

        // ---- Integrations & API Access ----
        ("api/v1/api-clients", ModuleCodes.IntegrationsApi),
        ("api/v1/webhooks", ModuleCodes.IntegrationsApi),
    };

    /// <summary>
    /// The modules a Blazor page path requires — empty when the page is part of the base product,
    /// one entry for the ordinary case, and more than one when holding <em>any</em> of them is
    /// enough. Callers should treat a non-empty result as "at least one of these".
    /// </summary>
    public static IReadOnlyList<string> RequiredModulesForPage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();

        var normalized = Normalize(path);

        // Any-of first: these are specific pages, and a broader single-module prefix must not
        // claim one of them by matching earlier.
        foreach (var (route, modules) in WebRoutesAnyOf)
        {
            if (IsSegmentPrefix(normalized, route)) return modules;
        }

        foreach (var (route, module) in WebRoutes)
        {
            if (IsSegmentPrefix(normalized, route)) return new[] { module };
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// The single module a page requires, or null when it is base product or is satisfied by any
    /// one of several. Prefer <see cref="RequiredModulesForPage"/>; this remains for callers that
    /// genuinely want one name, such as a message naming the module to buy.
    /// </summary>
    public static string? RequiredModuleForPage(string? path)
    {
        var modules = RequiredModulesForPage(path);
        return modules.Count == 1 ? modules[0] : null;
    }

    /// <summary>
    /// The module an API route template requires, or null when the endpoint is part of the base
    /// product. Pass the matched endpoint's raw route template.
    /// </summary>
    public static string? RequiredModuleForApiRoute(string? routeTemplate)
    {
        if (string.IsNullOrWhiteSpace(routeTemplate)) return null;

        var normalized = "/" + Normalize(routeTemplate).TrimStart('/');
        foreach (var (template, module) in ApiRoutes)
        {
            if (IsSegmentPrefix(normalized, "/" + template)) return module;
        }
        return null;
    }

    /// <summary>
    /// Strips the query string and any route-parameter constraint (<c>{id:guid}</c> becomes
    /// <c>{id}</c>) and lower-cases, so a template and a live path compare the same way.
    /// </summary>
    private static string Normalize(string value)
    {
        var q = value.IndexOf('?');
        if (q >= 0) value = value[..q];

        if (value.Contains(':') && value.Contains('{'))
        {
            var chars = new System.Text.StringBuilder(value.Length);
            var skipping = false;
            foreach (var c in value)
            {
                if (c == '{') { skipping = false; chars.Append(c); continue; }
                if (c == ':') { skipping = true; continue; }
                if (c == '}') { skipping = false; chars.Append(c); continue; }
                if (!skipping) chars.Append(c);
            }
            value = chars.ToString();
        }

        return value.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="candidate"/> equals <paramref name="prefix"/> or continues it at a
    /// segment boundary. Compared per segment so "/admin/visitors" cannot match "/admin/visitors-x",
    /// and so a route parameter in either side matches any single real segment.
    /// </summary>
    private static bool IsSegmentPrefix(string candidate, string prefix)
    {
        var a = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var b = prefix.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (b.Length > a.Length) return false;

        for (var i = 0; i < b.Length; i++)
        {
            var expected = b[i];
            if (expected.StartsWith('{') && expected.EndsWith('}')) continue; // any one segment
            var actual = a[i];
            if (actual.StartsWith('{') && actual.EndsWith('}')) continue;
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
