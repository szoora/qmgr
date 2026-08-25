using System.Diagnostics;
using QMgr.Application.Interfaces;

namespace QMgr.API.Middleware;

/// <summary>
/// Records every request's elapsed time and status code into IRequestMetricsService,
/// feeding HealthController's performance-metrics endpoint with real data instead of
/// hardcoded placeholders. Placed early in the pipeline so timing reflects the full
/// request (rate limiting, auth, tenant resolution included), not just controller time.
/// </summary>
public class RequestMetricsMiddleware
{
    private readonly RequestDelegate _next;

    public RequestMetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IRequestMetricsService metrics)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            metrics.RecordRequest(sw.ElapsedMilliseconds, context.Response.StatusCode);
        }
    }
}
