using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// ---------------------------------------------------------------------------------------------
// Reports: JSON siblings of ReportsController's CSV exports.
//
// These types exist so the Reports pages and the CSV downloads can never disagree: both are
// computed by the same actions on ReportsController from the same Token / Feedback / Counter
// rows over the same date range. Nothing on the Reports pages may be derived from anything else.
//
// Every "measurement" that can be absent is nullable (double?), NOT defaulted to zero. A zero
// average wait is a real, meaningful measurement ("nobody waited"); a null means "no tokens were
// served in this range, so there is nothing to average." Collapsing the second into the first is
// exactly how a report starts lying, so the UI must render null as an em dash / empty state and
// never as a number.
// ---------------------------------------------------------------------------------------------

/// <summary>One calendar day (UTC) of queue activity — the daily series behind the overview chart.</summary>
public record QueueDayStatDto
{
    public DateOnly Day { get; init; }
    public int Issued { get; init; }
    public int Served { get; init; }
    public int NoShow { get; init; }
    public int Cancelled { get; init; }
    public int Transferred { get; init; }

    /// <summary>Tokens issued that day still Waiting/Called/Serving at the time of the query.</summary>
    public int StillOpen { get; init; }

    /// <summary>Null when no token was served that day — not zero. Averaged over served tokens only.</summary>
    public double? AvgWaitMinutes { get; init; }
    public double? AvgServiceMinutes { get; init; }
}

/// <summary>
/// <c>GET api/v1/branches/{branchId}/reports/overview?from=&amp;to=</c> — the same aggregates as
/// the overview CSV export, plus the range totals the Reports pages show as summary tiles.
/// </summary>
public record QueueOverviewReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }

    public int TotalIssued { get; init; }
    public int TotalServed { get; init; }
    public int TotalNoShow { get; init; }
    public int TotalCancelled { get; init; }
    public int TotalTransferred { get; init; }
    public int StillOpen { get; init; }

    /// <summary>Null when nothing was served in the whole range.</summary>
    public double? AvgWaitMinutes { get; init; }
    public double? AvgServiceMinutes { get; init; }

    /// <summary>Counters currently in <see cref="CounterStatus.Active"/> — a live figure, not a range aggregate.</summary>
    public int ActiveCounters { get; init; }
    public int TotalCounters { get; init; }

    /// <summary>Contiguous day series including zero days, so a chart can plot it directly.</summary>
    public List<QueueDayStatDto> ByDay { get; init; } = new();

    /// <summary>Tokens issued per hour-of-day (UTC, 0-23, always all 24 entries).</summary>
    public List<HourCountDto> ByHour { get; init; } = new();
}

/// <summary>Per-counter row for the Counter Performance report.</summary>
public record CounterPerformanceDto
{
    public Guid CounterId { get; init; }
    public string CounterNumber { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public CounterStatus Status { get; init; }
    public string? AssignedStaff { get; init; }
    public List<string> ServiceTypes { get; init; } = new();

    /// <summary>Every token assigned to this counter in the range, whatever its final status.</summary>
    public int TokensHandled { get; init; }
    public int Served { get; init; }
    public int NoShow { get; init; }
    public int Transferred { get; init; }

    public double? AvgWaitMinutes { get; init; }
    public double? AvgServiceMinutes { get; init; }

    /// <summary>Sum of ServiceDurationMinutes over served tokens — the utilisation numerator.</summary>
    public int TotalServiceMinutes { get; init; }

    /// <summary>The utilisation denominator. See <see cref="CounterPerformanceReportDto.UtilisationDefinition"/>.</summary>
    public int ActiveMinutes { get; init; }

    /// <summary>
    /// Null when <see cref="ActiveMinutes"/> is zero (the counter has no recorded activity in this
    /// range) — there is no honest percentage to show, so the UI shows an em dash, never 0%.
    /// </summary>
    public double? UtilisationPercent { get; init; }
}

/// <summary>
/// <c>GET api/v1/branches/{branchId}/reports/counters?from=&amp;to=</c> — the same rows as the
/// counter CSV export, plus a real utilisation figure.
/// </summary>
public record CounterPerformanceReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public List<CounterPerformanceDto> Counters { get; init; } = new();

    /// <summary>
    /// The literal wording the UI must show as the utilisation footnote. It lives here rather
    /// than in the Razor page so the definition, the computation and the caption can never drift
    /// apart — a plausible-looking utilisation percentage with an unstated definition is exactly
    /// what the fabricated <c>Random.Shared.Next(40, 95)</c> figure used to be.
    /// </summary>
    public string UtilisationDefinition { get; init; } = string.Empty;
}

/// <summary>Per-service-type row for the Queue Analytics breakdown.</summary>
public record ServiceTypeReportRowDto
{
    public Guid ServiceTypeId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Issued { get; init; }
    public int Served { get; init; }
    public int NoShow { get; init; }
    public int Cancelled { get; init; }
    public int StillOpen { get; init; }
    public double? AvgWaitMinutes { get; init; }
    public double? AvgServiceMinutes { get; init; }
}

/// <summary><c>GET api/v1/branches/{branchId}/reports/services?from=&amp;to=</c>.</summary>
public record ServiceTypeReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public List<ServiceTypeReportRowDto> ServiceTypes { get; init; } = new();
}

/// <summary>One bar of the star-rating histogram.</summary>
public record RatingCountDto
{
    public int Stars { get; init; }
    public int Count { get; init; }

    /// <summary>Share of all rated feedback in the range, 0-100 (0 when there is no feedback at all).</summary>
    public double Percent { get; init; }
}

/// <summary>Feedback grouped by service type or by counter.</summary>
public record FeedbackBreakdownDto
{
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
    public double AverageRating { get; init; }
}

/// <summary>Average rating for one calendar day — the feedback trend line.</summary>
public record FeedbackDayRatingDto
{
    public DateOnly Day { get; init; }
    public int Count { get; init; }
    public double AverageRating { get; init; }
}

/// <summary>
/// One recent feedback entry for the on-page list. Deliberately carries NO customer PII
/// (name/phone/email): those columns are in the CSV export, which is gated on
/// <c>reports.export</c> precisely because it carries them. This read is gated on the weaker
/// <c>reports.view</c>, so it stays PII-free.
/// </summary>
public record FeedbackCommentDto
{
    public Guid Id { get; init; }
    public DateTime SubmittedAt { get; init; }
    public int Rating { get; init; }
    public string? Comment { get; init; }
    public FeedbackCategory Category { get; init; }
    public FeedbackSource Source { get; init; }
    public string? ServiceTypeName { get; init; }
    public string? CounterName { get; init; }
    public string? TokenDisplayNumber { get; init; }

    /// <summary>True once a staff response has been recorded — the "responder status" on the list.</summary>
    public bool HasResponse { get; init; }
    public DateTime? RespondedAt { get; init; }
}

/// <summary>
/// <c>GET api/v1/branches/{branchId}/reports/feedback?from=&amp;to=</c> — the aggregates behind
/// the Customer Feedback page. Sentiment buckets follow the usual 5-point convention: 4-5 stars
/// positive, 3 neutral, 1-2 negative.
/// </summary>
public record FeedbackReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }

    public int TotalCount { get; init; }

    /// <summary>Null when there is no feedback in the range — the UI shows an empty state, not 0.0 stars.</summary>
    public double? AverageRating { get; init; }

    public int PositiveCount { get; init; }
    public int NeutralCount { get; init; }
    public int NegativeCount { get; init; }
    public double PositivePercent { get; init; }
    public double NeutralPercent { get; init; }
    public double NegativePercent { get; init; }

    /// <summary>Always five entries, 5 stars down to 1, so the histogram keeps a stable shape.</summary>
    public List<RatingCountDto> Distribution { get; init; } = new();

    /// <summary>Only days that actually received feedback — a day with none has no average to plot.</summary>
    public List<FeedbackDayRatingDto> ByDay { get; init; } = new();

    public List<FeedbackBreakdownDto> ByServiceType { get; init; } = new();
    public List<FeedbackBreakdownDto> ByCounter { get; init; } = new();

    /// <summary>Most recent first, capped server-side.</summary>
    public List<FeedbackCommentDto> RecentComments { get; init; } = new();
}
