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

        // ---- Visitor & Safeguarding ----
        ("/admin/visitors", ModuleCodes.VisitorSafeguarding),
        ("/admin/students", ModuleCodes.VisitorSafeguarding),
        ("/admin/welfare", ModuleCodes.VisitorSafeguarding),
        ("/reports/visitors", ModuleCodes.VisitorSafeguarding),

        // ---- Integrations & API Access ----
        ("/admin/api-clients", ModuleCodes.IntegrationsApi),
        ("/admin/integrations", ModuleCodes.IntegrationsApi),
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
        ("api/v1/branches/{branchId}/reports", ModuleCodes.CoreQueue),
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

        // ---- Visitor & Safeguarding ----
        ("api/v1/branches/{branchId}/visitors", ModuleCodes.VisitorSafeguarding),
        ("api/v1/branches/{branchId}/visitor-passes", ModuleCodes.VisitorSafeguarding),
        ("api/v1/branches/{branchId}/students", ModuleCodes.VisitorSafeguarding),
        ("api/v1/branches/{branchId}/welfare", ModuleCodes.VisitorSafeguarding),
        ("api/v1/branches/{branchId}/welfare-records", ModuleCodes.VisitorSafeguarding),

        // ---- Integrations & API Access ----
        ("api/v1/api-clients", ModuleCodes.IntegrationsApi),
        ("api/v1/webhooks", ModuleCodes.IntegrationsApi),
    };

    /// <summary>
    /// The module a Blazor page path requires, or null when the page is part of the base product.
    /// </summary>
    public static string? RequiredModuleForPage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var normalized = Normalize(path);
        foreach (var (route, module) in WebRoutes)
        {
            if (IsSegmentPrefix(normalized, route)) return module;
        }
        return null;
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
