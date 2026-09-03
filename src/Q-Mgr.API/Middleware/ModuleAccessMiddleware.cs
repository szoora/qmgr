using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;

namespace QMgr.API.Middleware;

/// <summary>
/// Refuses any request whose matched route belongs to a module the caller's organization has not
/// bought. This is the backstop behind the per-action <c>[RequireModule]</c> attribute: the
/// attribute states intent on the controller where a reader will see it, while this middleware
/// enforces <see cref="ModuleRouteMap"/> for every endpoint under a mapped route, including ones
/// nobody remembered to decorate.
/// <para>
/// That gap was real. Roughly half the controllers carried no module attribute at all, so an
/// organization without Core Queue Management could still drive counters, service types, printing
/// and system settings by calling the API directly, even though the feature was something they had
/// never purchased.
/// </para>
/// <para>
/// Runs after authentication and tenant resolution so the organization is known, and skips
/// anonymous endpoints that are genuinely public (registration, login, health) — but NOT the
/// public kiosk and display endpoints, which are anonymous yet belong squarely to Core Queue
/// Management: an unpaid tenant's customer-facing screens should stop working, not keep serving.
/// </para>
/// </summary>
public class ModuleAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ModuleAccessMiddleware> _logger;

    public ModuleAccessMiddleware(RequestDelegate next, ILogger<ModuleAccessMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContextAccessor tenantAccessor,
        IModuleAccessService moduleAccessService)
    {
        var endpoint = context.GetEndpoint();
        var routeTemplate = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        var requiredModule = ModuleRouteMap.RequiredModuleForApiRoute(routeTemplate);

        if (requiredModule == null)
        {
            await _next(context);
            return;
        }

        var tenantContext = tenantAccessor.TenantContext;
        Guid organizationId;

        if (tenantContext is { IsResolved: true })
        {
            // SuperAdmin administers every tenant and is never limited by one tenant's purchases —
            // the same bypass [RequireModule], [RequirePermission] and the ownership checks all use.
            if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
            {
                await _next(context);
                return;
            }

            organizationId = tenantContext.OrganizationId;
        }
        else
        {
            // No resolved tenant: either an unauthenticated caller the auth layer will reject
            // anyway, or one of the genuinely anonymous kiosk/display endpoints. Those carry a
            // branch id in the route, which is the only tenant signal available — resolve the
            // owning organization from it so an unpaid tenant's customer-facing screens stop
            // serving too, rather than being the one way around the gate.
            var resolved = await ResolveOrganizationFromRouteAsync(context);
            if (resolved == null)
            {
                await _next(context);
                return;
            }

            organizationId = resolved.Value;
        }

        var isActive = await moduleAccessService.IsModuleActiveAsync(organizationId, requiredModule);
        if (isActive)
        {
            await _next(context);
            return;
        }

        _logger.LogInformation(
            "Blocked {Method} {Path} for organization {OrganizationId}: module {Module} is not active",
            context.Request.Method, context.Request.Path, organizationId, requiredModule);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "MODULE_NOT_PURCHASED",
            module = requiredModule,
            message = $"The '{requiredModule}' module is not active for your organization. Add it from Billing to access this feature.",
            purchaseUrl = "/billing/modules"
        });
    }

    /// <summary>
    /// Owning organization of the branch named in the route, or null when the route carries no
    /// usable branch id. Read directly rather than through a repository because this runs before
    /// the tenant context exists, so the usual organization-scoped filters have nothing to filter
    /// on. Failures return null and let the request continue to the endpoint's own checks.
    /// </summary>
    private async Task<Guid?> ResolveOrganizationFromRouteAsync(HttpContext context)
    {
        if (context.Request.RouteValues.TryGetValue("branchId", out var raw) &&
            Guid.TryParse(raw?.ToString(), out var branchId))
        {
            try
            {
                var db = context.RequestServices.GetRequiredService<QMgr.Infrastructure.Data.QMgrDbContext>();
                var orgId = await db.Branches
                    .Where(b => b.Id == branchId)
                    .Select(b => (Guid?)b.OrganizationId)
                    .FirstOrDefaultAsync();
                return orgId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve the organization for branch {BranchId}", branchId);
            }
        }

        return null;
    }
}

public static class ModuleAccessMiddlewareExtensions
{
    /// <summary>
    /// Register after UseAuthentication/UseAuthorization and after tenant resolution, so the
    /// organization is known by the time the gate runs.
    /// </summary>
    public static IApplicationBuilder UseModuleAccess(this IApplicationBuilder app)
        => app.UseMiddleware<ModuleAccessMiddleware>();
}
