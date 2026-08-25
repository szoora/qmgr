using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using System.Text.Json;

namespace QMgr.Middleware;

/// <summary>
/// Middleware that enforces usage limits based on subscription plan.
/// Returns 402 Payment Required if limits are exceeded.
/// </summary>
public class UsageLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UsageLimitMiddleware> _logger;

    // Endpoints that consume tokens
    private static readonly HashSet<string> TokenCreationEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/queue/tokens",
        "/api/v1/tokens",
        "/api/v1/kiosk/token"
    };

    // Endpoints that are API calls (for API limit tracking)
    private static readonly HashSet<string> ApiEndpointPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/",
        "/api/v2/"
    };

    // Endpoints exempt from limit checks
    private static readonly HashSet<string> ExemptEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/auth",
        "/api/v1/billing",
        "/api/v1/health",
        "/health",
        "/swagger",
        "/scalar"
    };

    public UsageLimitMiddleware(RequestDelegate next, ILogger<UsageLimitMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContextAccessor tenantAccessor,
        IUsageTrackingService usageTrackingService,
        IBillingService billingService)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        // Skip exempt endpoints
        if (IsExemptEndpoint(path))
        {
            await _next(context);
            return;
        }

        var tenantContext = tenantAccessor.TenantContext;

        // Skip if tenant not resolved
        if (tenantContext == null || !tenantContext.IsResolved)
        {
            await _next(context);
            return;
        }

        var organizationId = tenantContext.OrganizationId;

        // Check token creation limit for POST requests to token endpoints
        if (method == "POST" && IsTokenCreationEndpoint(path))
        {
            var limitCheck = await billingService.CheckLimitAsync(organizationId, "tokens");

            if (!limitCheck.IsWithinLimit)
            {
                _logger.LogWarning(
                    "Token limit exceeded for organization {OrganizationId}: {Current}/{Max}",
                    organizationId, limitCheck.CurrentUsage, limitCheck.MaxAllowed);

                await WritePaymentRequiredResponse(context, "TOKEN_LIMIT_EXCEEDED",
                    $"Monthly token limit of {limitCheck.MaxAllowed} exceeded. Please upgrade your plan.",
                    limitCheck);
                return;
            }

            // Check if approaching limit (80%)
            if (limitCheck.PercentageUsed >= 80)
            {
                context.Response.Headers.Append("X-Usage-Warning", "Approaching token limit");
                context.Response.Headers.Append("X-Usage-Percent", limitCheck.PercentageUsed.ToString("F0"));
            }
        }

        // Check API call limit for all API endpoints
        if (IsApiEndpoint(path))
        {
            // Check if plan has API access
            var subscription = await billingService.GetSubscriptionWithPlanAsync(organizationId);

            if (subscription != null && !subscription.Limits.HasApiAccess)
            {
                // Free tier without API access - only allow certain endpoints
                if (!IsAllowedForFreeTier(path))
                {
                    _logger.LogWarning(
                        "API access denied for organization {OrganizationId} - no API access on current plan",
                        organizationId);

                    await WriteForbiddenResponse(context, "API_ACCESS_DENIED",
                        "API access is not available on your current plan. Please upgrade to enable API access.");
                    return;
                }
            }

            // Check API call limit
            var apiLimitCheck = await billingService.CheckLimitAsync(organizationId, "api_calls");

            if (!apiLimitCheck.IsWithinLimit && apiLimitCheck.MaxAllowed > 0)
            {
                _logger.LogWarning(
                    "API call limit exceeded for organization {OrganizationId}: {Current}/{Max}",
                    organizationId, apiLimitCheck.CurrentUsage, apiLimitCheck.MaxAllowed);

                await WritePaymentRequiredResponse(context, "API_LIMIT_EXCEEDED",
                    $"Monthly API call limit of {apiLimitCheck.MaxAllowed} exceeded. Please upgrade your plan.",
                    apiLimitCheck);
                return;
            }

            // Track API call
            await usageTrackingService.IncrementApiCallsAsync(organizationId);

            // Add usage headers
            if (apiLimitCheck.MaxAllowed > 0)
            {
                context.Response.Headers.Append("X-RateLimit-Limit", apiLimitCheck.MaxAllowed.ToString());
                context.Response.Headers.Append("X-RateLimit-Remaining", apiLimitCheck.Remaining.ToString());
            }
        }

        await _next(context);
    }

    private static bool IsExemptEndpoint(string path)
    {
        return ExemptEndpoints.Any(exempt => path.StartsWith(exempt, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTokenCreationEndpoint(string path)
    {
        return TokenCreationEndpoints.Any(endpoint => path.StartsWith(endpoint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsApiEndpoint(string path)
    {
        return ApiEndpointPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAllowedForFreeTier(string path)
    {
        // Free tier can access these endpoints even without "API access" feature
        var allowedPaths = new[]
        {
            "/api/v1/auth",
            "/api/v1/queue/status",
            "/api/v1/queue/board",
            "/api/v1/kiosk",
            "/api/v1/display",
            "/api/v1/billing"
        };

        return allowedPaths.Any(allowed => path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WritePaymentRequiredResponse(
        HttpContext context,
        string errorCode,
        string message,
        LimitCheckResult limitCheck)
    {
        context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        context.Response.ContentType = "application/json";

        var response = new
        {
            error = errorCode,
            message,
            usage = new
            {
                current = limitCheck.CurrentUsage,
                limit = limitCheck.MaxAllowed,
                remaining = limitCheck.Remaining,
                percentUsed = limitCheck.PercentageUsed
            },
            upgradeUrl = "/billing/plans"
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }

    private static async Task WriteForbiddenResponse(
        HttpContext context,
        string errorCode,
        string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";

        var response = new
        {
            error = errorCode,
            message,
            upgradeUrl = "/billing/plans"
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
}

/// <summary>
/// Extension methods for adding usage limit middleware
/// </summary>
public static class UsageLimitMiddlewareExtensions
{
    /// <summary>
    /// Adds usage limit enforcement middleware to the pipeline
    /// </summary>
    public static IApplicationBuilder UseUsageLimits(this IApplicationBuilder app)
    {
        return app.UseMiddleware<UsageLimitMiddleware>();
    }
}
