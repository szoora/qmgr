namespace QMgr.Application.Interfaces;

/// <summary>
/// Tracks real, in-process HTTP request metrics (timing, error rate) over a
/// rolling window, backing the previously-hardcoded/TODO fields on
/// HealthController's performance-metrics endpoint. Resets on process
/// restart — there is no persistent metrics store — which is an accurate
/// reflection of what's actually being measured, not a gap to hide.
/// </summary>
public interface IRequestMetricsService
{
    void RecordRequest(long elapsedMs, int statusCode);

    /// <summary>Requests observed per second, averaged over the tracked window.</summary>
    decimal GetRequestsPerSecond();

    /// <summary>Mean response time in milliseconds over the tracked window.</summary>
    decimal GetAverageResponseTimeMs();

    /// <summary>Percentage of requests in the tracked window that returned a 5xx status.</summary>
    decimal GetErrorRatePercent();
}
