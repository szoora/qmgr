namespace QMgr.Application.DTOs;

public record QueueStatusDto
{
    public Guid BranchId { get; init; }
    public string BranchName { get; init; } = string.Empty;
    public DateTime CurrentTime { get; init; }
    public QueueSummaryDto Summary { get; init; } = new();
    public List<ServiceTypeQueueDto> ServiceTypes { get; init; } = new();
    public List<CounterStatusDto> Counters { get; init; } = new();
}

public record QueueSummaryDto
{
    public int TotalWaiting { get; init; }
    public int TotalServing { get; init; }
    public int TotalCompletedToday { get; init; }
    public double AverageWaitMinutes { get; init; }
    public double AverageServiceMinutes { get; init; }
}

public record ServiceTypeQueueDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int WaitingCount { get; init; }
    public int EstimatedWaitMinutes { get; init; }
    public int CountersActive { get; init; }
}

public record CounterStatusDto
{
    public Guid Id { get; init; }
    public string CounterNumber { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string Status { get; init; } = string.Empty;
    public List<string> ServiceTypeCodes { get; init; } = new();
    public string? CurrentTokenDisplay { get; init; }
    public string? ServingCustomerName { get; init; }
    public int TokensServedToday { get; init; }
}

public record DashboardMetricsDto
{
    public QueueSummaryDto TodaySummary { get; init; } = new();
    public List<HourlyMetricDto> HourlyMetrics { get; init; } = new();
    public List<ServiceTypeMetricDto> ServiceTypeMetrics { get; init; } = new();
    public List<CounterPerformanceDto> CounterPerformance { get; init; } = new();
}

public record HourlyMetricDto
{
    public int Hour { get; init; }
    public int TokenCount { get; init; }
    public double AverageWaitMinutes { get; init; }
}

public record ServiceTypeMetricDto
{
    public string ServiceTypeName { get; init; } = string.Empty;
    public int TokenCount { get; init; }
    public double PercentageOfTotal { get; init; }
}

public record CounterPerformanceDto
{
    public string CounterNumber { get; init; } = string.Empty;
    public int TokensServed { get; init; }
    public double AverageServiceMinutes { get; init; }
}
