using QMgr.Application.Tenant;
using QMgr.Domain.Enums;
using System.Text.Json;

namespace QMgr.Middleware;

/// <summary>
/// Middleware that checks tenant status and blocks access for suspended/cancelled tenants.
/// Returns 403 Forbidden with appropriate message for inactive tenants.
/// </summary>
public class TenantStatusMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantStatusMiddleware> _logger;

    // Endpoints that should always be accessible regardless of tenant status.
    // "/api/v1/health" (HealthController, polled continuously by the Web app's
    // ConnectionMonitorService) was missing here — only the unrelated bare "/health" (ASP.NET's
    // built-in health check endpoint) was listed, matching neither. UsageLimitMiddleware already
    // allows both. Without it, a suspended tenant's every health poll 403'd, which
    // ConnectionMonitorService read as "server unreachable" rather than "account suspended" and
    // retried forever — the whole app got stuck on an infinite "Reconnecting... Attempt 3 of 3"
    // overlay instead of ever surfacing the real ACCOUNT_SUSPENDED reason. Found live by
    // suspending a test tenant and watching the UI hang.
    //
    private static readonly HashSet<string> AlwaysAllowedEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/auth/login",
        "/api/v1/auth/refresh",
        "/api/v1/billing",
        "/api/v1/register",
        "/api/v1/health",
        "/health",
        "/swagger",
        "/scalar"
    };

    // Exact matches only (not prefixes) — MainLayout (the admin shell every non-Kiosk page
    // renders inside: sidebar branch selector, notification bell) calls these on every page
    // load regardless of what page a suspended tenant is actually trying to reach. Without this,
    // TenantStatusMessageHandler on the Web side — which redirects to /account-status on ANY
    // ACCOUNT_SUSPENDED 403 — would catch these ambient chrome calls too: a suspended admin
    // clicking "Update Payment" would navigate to the real, working /billing/subscription page,
    // only to have MainLayout's own branches/notifications calls 403 a moment later and bounce
    // them straight back to /account-status, an infinite ping-pong that made the one page a
    // suspended tenant most needs unreachable. This is the tenant's own account chrome (branch
    // names, notification list) — not gated business functionality — so exposing exactly these
    // is intentional; deliberately NOT a "/api/v1/branches" *prefix* match, which would also
    // allow-list branch/counter/service-type create-edit-delete (BranchesController) and
    // notification-settings writes (NotificationsController) for a suspended, non-paying tenant.
    private static readonly HashSet<string> AlwaysAllowedExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/branches",
        "/api/v1/notifications",
        "/api/v1/notifications/count",
        "/hubs/notifications",
        "/hubs/notifications/negotiate"
    };

    public TenantStatusMiddleware(RequestDelegate next, ILogger<TenantStatusMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContextAccessor tenantAccessor)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Skip always-allowed endpoints
        if (IsAlwaysAllowed(path))
        {
            await _next(context);
            return;
        }

        var tenantContext = tenantAccessor.TenantContext;

        // Skip if tenant not resolved (public endpoints)
        if (tenantContext == null || !tenantContext.IsResolved)
        {
            await _next(context);
            return;
        }

        // Check tenant status
        var status = tenantContext.Status;

        switch (status)
        {
            case TenantStatus.Pending:
                await WriteForbiddenResponse(context,
                    "ACCOUNT_PENDING",
                    "Your account is pending verification. Please check your email to verify your account.",
                    "/verify-email");
                return;

            case TenantStatus.Suspended:
                _logger.LogWarning(
                    "Access denied for suspended tenant {OrganizationId}",
                    tenantContext.OrganizationId);

                await WriteForbiddenResponse(context,
                    "ACCOUNT_SUSPENDED",
                    "Your account has been suspended due to payment issues. Please update your payment method to restore access.",
                    "/billing/update-payment");
                return;

            case TenantStatus.Cancelled:
                _logger.LogWarning(
                    "Access denied for cancelled tenant {OrganizationId}",
                    tenantContext.OrganizationId);

                await WriteForbiddenResponse(context,
                    "ACCOUNT_CANCELLED",
                    "Your account has been cancelled. Please contact support if you wish to reactivate.",
                    "/billing/reactivate");
                return;

            case TenantStatus.Deleted:
                await WriteForbiddenResponse(context,
                    "ACCOUNT_DELETED",
                    "This account no longer exists.",
                    null);
                return;

            case TenantStatus.Active:
            case TenantStatus.Trialing:
                // Allow access
                break;

            default:
                // Unknown status - log and allow (fail open for unknown states)
                _logger.LogWarning(
                    "Unknown tenant status {Status} for organization {OrganizationId}",
                    status, tenantContext.OrganizationId);
                break;
        }

        // Trial-expiry gating itself now happens in TenantResolutionMiddleware, which runs
        // before this middleware and self-heals an expired Trialing row to Suspended on the
        // same request that discovers it — see that class for why. By the time execution reaches
        // here, `status` already reflects that correction, so a still-Trialing status at this
        // point genuinely means the trial has not ended yet.
        if (status == TenantStatus.Trialing)
        {
            context.Response.Headers.Append("X-Trial-Status", "active");
        }

        await _next(context);
    }

    private static bool IsAlwaysAllowed(string path)
    {
        return AlwaysAllowedExact.Contains(path) ||
               AlwaysAllowedEndpoints.Any(endpoint =>
                   path.StartsWith(endpoint, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WriteForbiddenResponse(
        HttpContext context,
        string errorCode,
        string message,
        string? actionUrl)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";

        var response = new
        {
            error = errorCode,
            message,
            actionUrl
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
}

/// <summary>
/// Extension methods for adding tenant status middleware
/// </summary>
public static class TenantStatusMiddlewareExtensions
{
    /// <summary>
    /// Adds tenant status checking middleware to the pipeline
    /// </summary>
    public static IApplicationBuilder UseTenantStatus(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantStatusMiddleware>();
    }
}
