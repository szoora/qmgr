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

    // Endpoints that should always be accessible regardless of tenant status
    private static readonly HashSet<string> AlwaysAllowedEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/auth/login",
        "/api/v1/auth/refresh",
        "/api/v1/billing",
        "/api/v1/register",
        "/health",
        "/swagger",
        "/scalar"
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

        // Check if trial has expired
        if (status == TenantStatus.Trialing)
        {
            // Add trial info to response headers
            context.Response.Headers.Append("X-Trial-Status", "active");

            // Note: Trial expiration check should be handled by a background job
            // that updates the tenant status when trial expires
        }

        await _next(context);
    }

    private static bool IsAlwaysAllowed(string path)
    {
        return AlwaysAllowedEndpoints.Any(endpoint =>
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
