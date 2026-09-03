using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// The Reports pages' only data source. Every number on /reports, /reports/queue,
/// /reports/counters and /reports/feedback comes from here, and every one of these calls hits
/// the JSON sibling of the CSV export the same page offers — so what a manager reads on screen
/// and what they download are the same computation over the same rows.
/// </summary>
public interface IReportsApiService
{
    Task<ReportLoadResult<QueueOverviewReportDto>> GetOverviewAsync(Guid branchId, DateOnly from, DateOnly to);
    Task<ReportLoadResult<CounterPerformanceReportDto>> GetCounterPerformanceAsync(Guid branchId, DateOnly from, DateOnly to);
    Task<ReportLoadResult<ServiceTypeReportDto>> GetServiceTypesAsync(Guid branchId, DateOnly from, DateOnly to);
    Task<ReportLoadResult<FeedbackReportDto>> GetFeedbackAsync(Guid branchId, DateOnly from, DateOnly to);
}

/// <summary>
/// Outcome of a report read. Unlike most API services in this app, a failure is NOT collapsed to
/// null: a 403 (module not purchased, or missing reports.view) is a real, user-actionable answer
/// that the page has to show as a message. A silently empty chart is indistinguishable from
/// "no data yet", which is precisely the confusion these pages are being fixed to end.
/// Web-only — never crosses the API boundary, so it deliberately does not live in Q-Mgr.Shared.
/// </summary>
public record ReportLoadResult<T>(T? Data, int StatusCode, string? ErrorMessage) where T : class
{
    public bool Success => Data != null;
    public bool IsForbidden => StatusCode == 403;

    /// <summary>Message to show the user when <see cref="Success"/> is false.</summary>
    public string DisplayMessage => ErrorMessage
        ?? (IsForbidden
            ? "This report isn't available on your current plan, or you don't have permission to view it."
            : StatusCode == 0
                ? "Unable to reach the server. Please check your connection and try again."
                : "Couldn't load this report. Please try again.");
}

public class ReportsApiService : IReportsApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ReportsApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public ReportsApiService(HttpClient httpClient, ILogger<ReportsApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public Task<ReportLoadResult<QueueOverviewReportDto>> GetOverviewAsync(Guid branchId, DateOnly from, DateOnly to)
        => GetReportAsync<QueueOverviewReportDto>("overview", branchId, from, to);

    public Task<ReportLoadResult<CounterPerformanceReportDto>> GetCounterPerformanceAsync(Guid branchId, DateOnly from, DateOnly to)
        => GetReportAsync<CounterPerformanceReportDto>("counters", branchId, from, to);

    public Task<ReportLoadResult<ServiceTypeReportDto>> GetServiceTypesAsync(Guid branchId, DateOnly from, DateOnly to)
        => GetReportAsync<ServiceTypeReportDto>("services", branchId, from, to);

    public Task<ReportLoadResult<FeedbackReportDto>> GetFeedbackAsync(Guid branchId, DateOnly from, DateOnly to)
        => GetReportAsync<FeedbackReportDto>("feedback", branchId, from, to);

    private async Task<ReportLoadResult<T>> GetReportAsync<T>(string report, Guid branchId, DateOnly from, DateOnly to)
        where T : class
    {
        var url = $"api/v1/branches/{branchId}/reports/{report}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
                return data == null
                    ? new ReportLoadResult<T>(null, (int)response.StatusCode, "The server returned an empty report.")
                    : new ReportLoadResult<T>(data, (int)response.StatusCode, null);
            }

            // Both RequireFeature/RequireModule ({ error, message, ... }) and ProblemDetails
            // ({ title, detail, ... }) bodies are possible — pull whichever human-readable field
            // is present rather than assuming one shape. Same reasoning as
            // QueueApiService.ExportReportCsvAsync.
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "message", "detail", "title" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                        {
                            message = el.GetString();
                            break;
                        }
                    }
                }
            }
            catch (JsonException) { /* empty or non-JSON error body — DisplayMessage falls back */ }

            _logger.LogWarning("Report '{Report}' for branch {BranchId} returned {StatusCode}: {Message}",
                report, branchId, (int)response.StatusCode, message);
            return new ReportLoadResult<T>(null, (int)response.StatusCode, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load report '{Report}' for branch {BranchId}", report, branchId);
            return new ReportLoadResult<T>(null, 0, null);
        }
    }
}
