using System.Collections.Concurrent;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services;

public class RequestMetricsService : IRequestMetricsService
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly record struct Sample(DateTime Timestamp, long ElapsedMs, bool IsError);

    private readonly ConcurrentQueue<Sample> _samples = new();

    public void RecordRequest(long elapsedMs, int statusCode)
    {
        _samples.Enqueue(new Sample(DateTime.UtcNow, elapsedMs, statusCode >= 500));
        Trim();
    }

    public decimal GetRequestsPerSecond()
    {
        var snapshot = Trim();
        if (snapshot.Count == 0) return 0;

        var oldest = snapshot.Min(s => s.Timestamp);
        var spanSeconds = Math.Max((DateTime.UtcNow - oldest).TotalSeconds, 1);
        return Math.Round((decimal)(snapshot.Count / spanSeconds), 2);
    }

    public decimal GetAverageResponseTimeMs()
    {
        var snapshot = Trim();
        if (snapshot.Count == 0) return 0;

        return Math.Round((decimal)snapshot.Average(s => s.ElapsedMs), 2);
    }

    public decimal GetErrorRatePercent()
    {
        var snapshot = Trim();
        if (snapshot.Count == 0) return 0;

        var errorCount = snapshot.Count(s => s.IsError);
        return Math.Round((decimal)errorCount / snapshot.Count * 100m, 2);
    }

    private List<Sample> Trim()
    {
        var cutoff = DateTime.UtcNow - Window;
        while (_samples.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            _samples.TryDequeue(out _);
        }
        return _samples.ToList();
    }
}
