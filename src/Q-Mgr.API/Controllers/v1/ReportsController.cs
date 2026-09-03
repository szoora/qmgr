using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// CSV exports backing the Reports pages (Overview, Counter Performance, Customer Feedback).
/// Mirrors <see cref="VisitorsController.ExportVisitorReport"/> exactly — hand-rolled CSV via
/// StringBuilder, <c>File(bytes, "text/csv", name)</c>, consumed by the Web app via a data: URL
/// and the <c>downloadDataUrl</c> JS helper. Every row set is computed from Token / Feedback
/// rows over an admin-chosen date range (default: trailing 7 days), materialized once and
/// aggregated in memory — same reasoning as the visitor report: not a hot path, and it avoids
/// fighting EF's SQL translation of nullable-average / DateOnly grouping.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action already has its own [RequirePermission]
public class ReportsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IFeatureFlagService _featureFlags;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IFeatureFlagService featureFlags,
        ILogger<ReportsController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _featureFlags = featureFlags;
        _logger = logger;
    }

    /// <summary>
    /// Token/Feedback/Counter (like Visitor) have no global EF query filter — they're
    /// branch-scoped, not directly org-scoped — so every action reaching them by branchId must
    /// verify ownership explicitly. Copied from VisitorsController; SuperAdmin bypass matches
    /// every other VerifyBranchOwnership in this codebase.
    /// </summary>
    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var superAdminBranchExists = await _context.Branches.AnyAsync(b => b.Id == branchId);
            return superAdminBranchExists ? null : NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' does not exist.",
                Status = StatusCodes.Status404NotFound
            });
        }

        var branchExists = await _context.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }

    /// <summary>
    /// The <c>export_reports</c> feature-flag gate, done inline rather than via
    /// <c>[RequireFeature(FeatureCodes.ExportReports)]</c> because that attribute has no
    /// SuperAdmin bypass (unlike <c>[RequireModule]</c>): the platform admin's own org has no
    /// subscription and no modules, so the attribute would resolve free-tier flags and 403 every
    /// export for SuperAdmin. Same response shape as RequireFeatureAttribute so the Web client
    /// handles both identically. Call AFTER VerifyBranchOwnership so tenantContext is known good.
    /// </summary>
    private async Task<IActionResult?> VerifyExportFeature()
    {
        var tenantContext = _tenantAccessor.TenantContext!;
        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
            return null;

        var enabled = await _featureFlags.IsFeatureEnabledAsync(tenantContext.OrganizationId, FeatureCodes.ExportReports);
        if (enabled) return null;

        return new ObjectResult(new
        {
            error = "FEATURE_NOT_AVAILABLE",
            feature = FeatureCodes.ExportReports,
            message = "Report export is not available on your current plan. Add a module from Billing to enable exports.",
            upgradeUrl = "/billing/modules"
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }

    // =========================================================================================
    // JSON reads — the on-screen siblings of the CSV exports below.
    //
    // Every figure the Reports pages display comes from one of these four actions, computed from
    // the same rows over the same range as the matching /export action, so the screen and the
    // download can never disagree. They are gated on Permissions.ReportsView (weaker than the
    // exports' ReportsExport) because they carry no customer PII — see FeedbackCommentDto.
    //
    // Averages are nullable all the way to the browser: null means "nothing was served/rated in
    // this range, so there is nothing to average", which the UI renders as an empty state. It is
    // NOT rounded down to zero — a zero here would read as a measurement ("customers waited no
    // time at all") when it is in fact an absence of one.
    // =========================================================================================

    /// <summary>
    /// Daily queue series plus range totals. Same aggregation as
    /// <see cref="ExportOverviewReport"/>, including the zero days, so the chart and the CSV
    /// plot the same line.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/reports/overview")]
    [RequirePermission(Permissions.ReportsView)]
    [RequireModule(ModuleCodes.CoreQueue)]
    public async Task<IActionResult> GetOverviewReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var tokens = await _context.Tokens
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.CreatedAt >= rangeStart && t.CreatedAt < rangeEndExclusive)
            .Select(t => new { t.CreatedAt, t.Status, t.ActualWaitMinutes, t.ServiceDurationMinutes })
            .ToListAsync();

        var counterStatuses = await _context.Counters
            .AsNoTracking()
            .Where(c => c.BranchId == branchId)
            .Select(c => c.Status)
            .ToListAsync();

        var byDay = tokens.ToLookup(t => DateOnly.FromDateTime(t.CreatedAt));
        var series = new List<QueueDayStatDto>();
        for (var day = fromDate; day <= toDate; day = day.AddDays(1))
        {
            var rows = byDay[day].ToList();
            var servedRows = rows.Where(t => t.Status == TokenStatus.Completed).ToList();
            series.Add(new QueueDayStatDto
            {
                Day = day,
                Issued = rows.Count,
                Served = servedRows.Count,
                NoShow = rows.Count(t => t.Status == TokenStatus.NoShow),
                Cancelled = rows.Count(t => t.Status == TokenStatus.Cancelled),
                Transferred = rows.Count(t => t.Status == TokenStatus.Transferred),
                StillOpen = rows.Count(t => t.Status == TokenStatus.Waiting || t.Status == TokenStatus.Called || t.Status == TokenStatus.Serving),
                AvgWaitMinutes = AverageOrNull(servedRows.Select(t => t.ActualWaitMinutes)),
                AvgServiceMinutes = AverageOrNull(servedRows.Select(t => t.ServiceDurationMinutes))
            });
        }

        var served = tokens.Where(t => t.Status == TokenStatus.Completed).ToList();

        return Ok(new QueueOverviewReportDto
        {
            From = fromDate,
            To = toDate,
            TotalIssued = tokens.Count,
            TotalServed = served.Count,
            TotalNoShow = tokens.Count(t => t.Status == TokenStatus.NoShow),
            TotalCancelled = tokens.Count(t => t.Status == TokenStatus.Cancelled),
            TotalTransferred = tokens.Count(t => t.Status == TokenStatus.Transferred),
            StillOpen = tokens.Count(t => t.Status == TokenStatus.Waiting || t.Status == TokenStatus.Called || t.Status == TokenStatus.Serving),
            AvgWaitMinutes = AverageOrNull(served.Select(t => t.ActualWaitMinutes)),
            AvgServiceMinutes = AverageOrNull(served.Select(t => t.ServiceDurationMinutes)),
            ActiveCounters = counterStatuses.Count(s => s == CounterStatus.Active),
            TotalCounters = counterStatuses.Count,
            ByDay = series,
            ByHour = Enumerable.Range(0, 24)
                .Select(h => new HourCountDto { Hour = h, Count = tokens.Count(t => t.CreatedAt.Hour == h) })
                .ToList()
        });
    }

    /// <summary>
    /// Per-counter performance, including a real utilisation figure.
    ///
    /// UTILISATION IS DEFINED HERE, once, and the definition travels to the UI as
    /// <see cref="CounterPerformanceReportDto.UtilisationDefinition"/> so the caption on screen
    /// can never drift from the arithmetic:
    ///
    ///   utilisation % = 100 × total service minutes ÷ active minutes
    ///
    /// where <b>total service minutes</b> is the sum of ServiceDurationMinutes over the tokens
    /// this counter actually completed in the range, and <b>active minutes</b> is the counter's
    /// own observed working window — for each calendar day, the span from its first recorded
    /// activity (ServiceStartedAt, else CalledAt, else the token's CreatedAt) to its last
    /// (ServiceCompletedAt, else the same fallbacks), summed across days, and floored at the
    /// total service minutes so a single-token day can't produce a nonsensical &gt;100%.
    ///
    /// Days on which the counter handled nothing contribute to neither figure — they are not
    /// counted as idle time, because nothing in the schema records whether the counter was even
    /// meant to be open. That is also why this is deliberately NOT "share of the working day":
    /// Counter has no opening/closing times and Branch.OperatingHours is free-form JSON that may
    /// be absent, so any working-day denominator would be an assumption dressed as a measurement.
    /// A counter with no activity at all in the range gets a null utilisation, not 0%.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/reports/counters")]
    [RequirePermission(Permissions.ReportsView)]
    [RequireModule(ModuleCodes.CoreQueue)]
    public async Task<IActionResult> GetCounterReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var counters = await _context.Counters
            .AsNoTracking()
            .Where(c => c.BranchId == branchId)
            .OrderBy(c => c.CounterNumber)
            .Select(c => new
            {
                c.Id,
                c.CounterNumber,
                c.DisplayName,
                c.Status,
                AssignedStaff = c.AssignedUser != null ? (c.AssignedUser.FirstName + " " + c.AssignedUser.LastName) : null,
                ServiceTypes = c.CounterServiceTypes.Select(cst => cst.ServiceType!.Code).ToList()
            })
            .ToListAsync();

        var tokens = await _context.Tokens
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.CounterId != null && t.CreatedAt >= rangeStart && t.CreatedAt < rangeEndExclusive)
            .Select(t => new
            {
                CounterId = t.CounterId!.Value,
                t.CreatedAt,
                t.CalledAt,
                t.ServiceStartedAt,
                t.ServiceCompletedAt,
                t.Status,
                t.ActualWaitMinutes,
                t.ServiceDurationMinutes
            })
            .ToListAsync();

        var byCounter = tokens.ToLookup(t => t.CounterId);

        var rowsOut = new List<CounterPerformanceDto>();
        foreach (var c in counters)
        {
            var rows = byCounter[c.Id].ToList();
            var served = rows.Where(t => t.Status == TokenStatus.Completed).ToList();
            var totalServiceMinutes = served.Sum(t => t.ServiceDurationMinutes ?? 0);

            // Observed working window, per day, summed. See the XML doc above for why this and
            // not a working-day denominator.
            var observedMinutes = rows
                .Select(t => new
                {
                    Start = t.ServiceStartedAt ?? t.CalledAt ?? t.CreatedAt,
                    End = t.ServiceCompletedAt ?? t.ServiceStartedAt ?? t.CalledAt ?? t.CreatedAt
                })
                .GroupBy(w => DateOnly.FromDateTime(w.Start))
                .Sum(g => Math.Max(0d, (g.Max(w => w.End) - g.Min(w => w.Start)).TotalMinutes));

            var activeMinutes = (int)Math.Round(Math.Max(observedMinutes, totalServiceMinutes));

            rowsOut.Add(new CounterPerformanceDto
            {
                CounterId = c.Id,
                CounterNumber = c.CounterNumber,
                DisplayName = string.IsNullOrWhiteSpace(c.DisplayName) ? $"Counter {c.CounterNumber}" : c.DisplayName!,
                Status = c.Status,
                AssignedStaff = string.IsNullOrWhiteSpace(c.AssignedStaff) ? null : c.AssignedStaff!.Trim(),
                ServiceTypes = c.ServiceTypes,
                TokensHandled = rows.Count,
                Served = served.Count,
                NoShow = rows.Count(t => t.Status == TokenStatus.NoShow),
                Transferred = rows.Count(t => t.Status == TokenStatus.Transferred),
                AvgWaitMinutes = AverageOrNull(served.Select(t => t.ActualWaitMinutes)),
                AvgServiceMinutes = AverageOrNull(served.Select(t => t.ServiceDurationMinutes)),
                TotalServiceMinutes = totalServiceMinutes,
                ActiveMinutes = activeMinutes,
                UtilisationPercent = activeMinutes > 0
                    ? Math.Round(100.0 * totalServiceMinutes / activeMinutes, 1)
                    : null
            });
        }

        return Ok(new CounterPerformanceReportDto
        {
            From = fromDate,
            To = toDate,
            Counters = rowsOut,
            UtilisationDefinition = UtilisationDefinitionText
        });
    }

    /// <summary>Per-service-type aggregates — the JSON sibling of <see cref="ExportServiceTypeReport"/>.</summary>
    [HttpGet("branches/{branchId:guid}/reports/services")]
    [RequirePermission(Permissions.ReportsView)]
    [RequireModule(ModuleCodes.CoreQueue)]
    public async Task<IActionResult> GetServiceTypeReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var serviceTypes = await _context.ServiceTypes
            .AsNoTracking()
            .Where(s => s.BranchId == branchId)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Code, s.Name })
            .ToListAsync();

        var tokens = await _context.Tokens
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.CreatedAt >= rangeStart && t.CreatedAt < rangeEndExclusive)
            .Select(t => new { t.ServiceTypeId, t.Status, t.ActualWaitMinutes, t.ServiceDurationMinutes })
            .ToListAsync();

        var byService = tokens.ToLookup(t => t.ServiceTypeId);

        var rowsOut = serviceTypes.Select(s =>
        {
            var rows = byService[s.Id].ToList();
            var served = rows.Where(t => t.Status == TokenStatus.Completed).ToList();
            return new ServiceTypeReportRowDto
            {
                ServiceTypeId = s.Id,
                Code = s.Code,
                Name = s.Name,
                Issued = rows.Count,
                Served = served.Count,
                NoShow = rows.Count(t => t.Status == TokenStatus.NoShow),
                Cancelled = rows.Count(t => t.Status == TokenStatus.Cancelled),
                StillOpen = rows.Count(t => t.Status == TokenStatus.Waiting || t.Status == TokenStatus.Called || t.Status == TokenStatus.Serving),
                AvgWaitMinutes = AverageOrNull(served.Select(t => t.ActualWaitMinutes)),
                AvgServiceMinutes = AverageOrNull(served.Select(t => t.ServiceDurationMinutes))
            };
        }).ToList();

        return Ok(new ServiceTypeReportDto { From = fromDate, To = toDate, ServiceTypes = rowsOut });
    }

    /// <summary>
    /// Feedback aggregates for the range. Gated on the Engagement module, matching
    /// <see cref="ExportFeedbackReport"/>. Unlike the export, the comment list carries no
    /// customer name/phone/email — this action only requires ReportsView, and the PII is exactly
    /// why the export requires the stronger ReportsExport.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/reports/feedback")]
    [RequirePermission(Permissions.ReportsView)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
    public async Task<IActionResult> GetFeedbackReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var feedback = await _context.Feedbacks
            .AsNoTracking()
            .Where(f => f.BranchId == branchId && f.CreatedAt >= rangeStart && f.CreatedAt < rangeEndExclusive)
            .Select(f => new
            {
                f.Id,
                f.CreatedAt,
                f.Rating,
                f.Comment,
                f.Category,
                f.Source,
                ServiceTypeName = f.ServiceType != null ? f.ServiceType.Name : null,
                CounterNumber = f.Counter != null ? f.Counter.CounterNumber : null,
                CounterDisplayName = f.Counter != null ? f.Counter.DisplayName : null,
                f.TokenDisplayNumber,
                f.Response,
                f.RespondedAt
            })
            .ToListAsync();

        var total = feedback.Count;
        var positive = feedback.Count(f => f.Rating >= 4);
        var neutral = feedback.Count(f => f.Rating == 3);
        var negative = feedback.Count(f => f.Rating >= 1 && f.Rating <= 2);

        static double Share(int part, int whole) => whole == 0 ? 0 : Math.Round(100.0 * part / whole, 1);
        static string CounterLabel(string? displayName, string? number) =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName!
            : !string.IsNullOrWhiteSpace(number) ? $"Counter {number}"
            : "Unassigned";

        return Ok(new FeedbackReportDto
        {
            From = fromDate,
            To = toDate,
            TotalCount = total,
            AverageRating = total == 0 ? null : Math.Round(feedback.Average(f => (double)f.Rating), 2),
            PositiveCount = positive,
            NeutralCount = neutral,
            NegativeCount = negative,
            PositivePercent = Share(positive, total),
            NeutralPercent = Share(neutral, total),
            NegativePercent = Share(negative, total),
            Distribution = Enumerable.Range(1, 5).Reverse().Select(stars =>
            {
                var count = feedback.Count(f => f.Rating == stars);
                return new RatingCountDto { Stars = stars, Count = count, Percent = Share(count, total) };
            }).ToList(),
            ByDay = feedback
                .GroupBy(f => DateOnly.FromDateTime(f.CreatedAt))
                .OrderBy(g => g.Key)
                .Select(g => new FeedbackDayRatingDto
                {
                    Day = g.Key,
                    Count = g.Count(),
                    AverageRating = Math.Round(g.Average(f => (double)f.Rating), 2)
                })
                .ToList(),
            ByServiceType = feedback
                .GroupBy(f => string.IsNullOrWhiteSpace(f.ServiceTypeName) ? "Unspecified" : f.ServiceTypeName!)
                .OrderByDescending(g => g.Count())
                .Select(g => new FeedbackBreakdownDto
                {
                    Name = g.Key,
                    Count = g.Count(),
                    AverageRating = Math.Round(g.Average(f => (double)f.Rating), 2)
                })
                .ToList(),
            ByCounter = feedback
                .GroupBy(f => CounterLabel(f.CounterDisplayName, f.CounterNumber))
                .OrderByDescending(g => g.Count())
                .Select(g => new FeedbackBreakdownDto
                {
                    Name = g.Key,
                    Count = g.Count(),
                    AverageRating = Math.Round(g.Average(f => (double)f.Rating), 2)
                })
                .ToList(),
            RecentComments = feedback
                .OrderByDescending(f => f.CreatedAt)
                .Take(RecentFeedbackLimit)
                .Select(f => new FeedbackCommentDto
                {
                    Id = f.Id,
                    SubmittedAt = f.CreatedAt,
                    Rating = f.Rating,
                    Comment = f.Comment,
                    Category = f.Category,
                    Source = f.Source,
                    ServiceTypeName = f.ServiceTypeName,
                    CounterName = string.IsNullOrWhiteSpace(f.CounterDisplayName) && string.IsNullOrWhiteSpace(f.CounterNumber)
                        ? null
                        : CounterLabel(f.CounterDisplayName, f.CounterNumber),
                    TokenDisplayNumber = f.TokenDisplayNumber,
                    HasResponse = !string.IsNullOrWhiteSpace(f.Response),
                    RespondedAt = f.RespondedAt
                })
                .ToList()
        });
    }

    /// <summary>How many feedback entries the on-page "Recent Feedback" list carries.</summary>
    private const int RecentFeedbackLimit = 50;

    /// <summary>
    /// The utilisation caption shown on the Counter Performance page, kept next to the
    /// computation in <see cref="GetCounterReport"/> so the two can't drift.
    /// </summary>
    private const string UtilisationDefinitionText =
        "Utilisation = total service minutes ÷ active minutes. Active minutes is each counter's own " +
        "recorded working window: per day, the span from its first recorded activity (service start, " +
        "or call time) to its last (service completion), summed across days, and never less than the " +
        "minutes it actually spent serving. Days on which a counter handled no tokens are excluded " +
        "from both figures. This is the share of the time a counter was demonstrably open that it " +
        "spent serving customers — not a share of the working day, because counter opening and " +
        "closing times are not recorded.";

    private static double? AverageOrNull(IEnumerable<int?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Average(), 1);
    }

    /// <summary>
    /// Daily queue summary — one row per calendar day in the range (UTC), including zero days so
    /// the CSV is a contiguous series a spreadsheet can chart directly.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/reports/overview/export")]
    [RequirePermission(Permissions.ReportsExport)]
    [RequireModule(ModuleCodes.CoreQueue)]
    public async Task<IActionResult> ExportOverviewReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var featureError = await VerifyExportFeature();
        if (featureError != null) return featureError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var tokens = await _context.Tokens
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.CreatedAt >= rangeStart && t.CreatedAt < rangeEndExclusive)
            .Select(t => new { t.CreatedAt, t.Status, t.ActualWaitMinutes, t.ServiceDurationMinutes })
            .ToListAsync();

        var byDay = tokens.ToLookup(t => DateOnly.FromDateTime(t.CreatedAt));

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Date,Tokens Issued,Served,No-Show,Cancelled,Transferred,Still Open,Avg Wait (min),Avg Service (min)");
        for (var day = fromDate; day <= toDate; day = day.AddDays(1))
        {
            var rows = byDay[day].ToList();
            var served = rows.Where(t => t.Status == TokenStatus.Completed).ToList();
            var open = rows.Count(t => t.Status == TokenStatus.Waiting || t.Status == TokenStatus.Called || t.Status == TokenStatus.Serving);
            csv.AppendLine(string.Join(",", new[]
            {
                CsvField(day.ToString("yyyy-MM-dd")),
                CsvField(rows.Count.ToString()),
                CsvField(served.Count.ToString()),
                CsvField(rows.Count(t => t.Status == TokenStatus.NoShow).ToString()),
                CsvField(rows.Count(t => t.Status == TokenStatus.Cancelled).ToString()),
                CsvField(rows.Count(t => t.Status == TokenStatus.Transferred).ToString()),
                CsvField(open.ToString()),
                CsvField(FormatAverage(served.Select(t => t.ActualWaitMinutes))),
                CsvField(FormatAverage(served.Select(t => t.ServiceDurationMinutes)))
            }));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"queue-summary-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.csv");
    }

    /// <summary>
    /// Per-counter rows for the range — every counter configured on the branch appears, even
    /// ones that served nothing, so a supervisor sees idle counters rather than having them
    /// silently vanish from the export.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/reports/counters/export")]
    [RequirePermission(Permissions.ReportsExport)]
    [RequireModule(ModuleCodes.CoreQueue)]
    public async Task<IActionResult> ExportCounterReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var featureError = await VerifyExportFeature();
        if (featureError != null) return featureError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var counters = await _context.Counters
            .AsNoTracking()
            .Where(c => c.BranchId == branchId)
            .OrderBy(c => c.CounterNumber)
            .Select(c => new
            {
                c.Id,
                c.CounterNumber,
                c.DisplayName,
                c.Status,
                AssignedStaff = c.AssignedUser != null ? (c.AssignedUser.FirstName + " " + c.AssignedUser.LastName) : null,
                ServiceTypes = c.CounterServiceTypes.Select(cst => cst.ServiceType!.Code).ToList()
            })
            .ToListAsync();

        var tokens = await _context.Tokens
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.CounterId != null && t.CreatedAt >= rangeStart && t.CreatedAt < rangeEndExclusive)
            .Select(t => new { CounterId = t.CounterId!.Value, t.Status, t.ActualWaitMinutes, t.ServiceDurationMinutes })
            .ToListAsync();

        var byCounter = tokens.ToLookup(t => t.CounterId);

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Counter,Status,Assigned Staff,Service Types,Tokens Handled,Served,No-Show,Transferred,Avg Wait (min),Avg Service (min),Total Service (min)");
        foreach (var c in counters)
        {
            var rows = byCounter[c.Id].ToList();
            var served = rows.Where(t => t.Status == TokenStatus.Completed).ToList();
            csv.AppendLine(string.Join(",", new[]
            {
                CsvField(string.IsNullOrWhiteSpace(c.DisplayName) ? $"Counter {c.CounterNumber}" : c.DisplayName),
                CsvField(c.Status.ToString()),
                CsvField((c.AssignedStaff ?? "").Trim()),
                CsvField(string.Join("; ", c.ServiceTypes)),
                CsvField(rows.Count.ToString()),
                CsvField(served.Count.ToString()),
                CsvField(rows.Count(t => t.Status == TokenStatus.NoShow).ToString()),
                CsvField(rows.Count(t => t.Status == TokenStatus.Transferred).ToString()),
                CsvField(FormatAverage(served.Select(t => t.ActualWaitMinutes))),
                CsvField(FormatAverage(served.Select(t => t.ServiceDurationMinutes))),
                CsvField(served.Sum(t => t.ServiceDurationMinutes ?? 0).ToString())
            }));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"counter-performance-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.csv");
    }

    /// <summary>
    /// Per-service-type rows for the range — backs the "Service Type Analysis" option in the
    /// Reports Overview report-type picker.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/reports/services/export")]
    [RequirePermission(Permissions.ReportsExport)]
    [RequireModule(ModuleCodes.CoreQueue)]
    public async Task<IActionResult> ExportServiceTypeReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var featureError = await VerifyExportFeature();
        if (featureError != null) return featureError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var serviceTypes = await _context.ServiceTypes
            .AsNoTracking()
            .Where(s => s.BranchId == branchId)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Code, s.Name })
            .ToListAsync();

        var tokens = await _context.Tokens
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.CreatedAt >= rangeStart && t.CreatedAt < rangeEndExclusive)
            .Select(t => new { t.ServiceTypeId, t.Status, t.ActualWaitMinutes, t.ServiceDurationMinutes })
            .ToListAsync();

        var byService = tokens.ToLookup(t => t.ServiceTypeId);

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Service Type,Code,Tokens Issued,Served,No-Show,Cancelled,Avg Wait (min),Avg Service (min)");
        foreach (var s in serviceTypes)
        {
            var rows = byService[s.Id].ToList();
            var served = rows.Where(t => t.Status == TokenStatus.Completed).ToList();
            csv.AppendLine(string.Join(",", new[]
            {
                CsvField(s.Name),
                CsvField(s.Code),
                CsvField(rows.Count.ToString()),
                CsvField(served.Count.ToString()),
                CsvField(rows.Count(t => t.Status == TokenStatus.NoShow).ToString()),
                CsvField(rows.Count(t => t.Status == TokenStatus.Cancelled).ToString()),
                CsvField(FormatAverage(served.Select(t => t.ActualWaitMinutes))),
                CsvField(FormatAverage(served.Select(t => t.ServiceDurationMinutes)))
            }));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"service-types-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.csv");
    }

    /// <summary>
    /// Raw feedback log for the range. Gated on the Engagement module (same as every action on
    /// FeedbackController) rather than Core Queue. Rows carry customer PII (name/phone/email),
    /// which is why this is ReportsExport rather than FeedbackView — same reasoning as the
    /// visitor-log export.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/reports/feedback/export")]
    [RequirePermission(Permissions.ReportsExport)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
    public async Task<IActionResult> ExportFeedbackReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var featureError = await VerifyExportFeature();
        if (featureError != null) return featureError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var feedback = await _context.Feedbacks
            .AsNoTracking()
            .Where(f => f.BranchId == branchId && f.CreatedAt >= rangeStart && f.CreatedAt < rangeEndExclusive)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new
            {
                f.CreatedAt,
                f.ServiceDate,
                f.Rating,
                f.Category,
                f.Source,
                ServiceTypeName = f.ServiceType != null ? f.ServiceType.Name : null,
                CounterNumber = f.Counter != null ? f.Counter.CounterNumber : null,
                CounterDisplayName = f.Counter != null ? f.Counter.DisplayName : null,
                f.TokenDisplayNumber,
                f.CustomerName,
                f.CustomerPhone,
                f.CustomerEmail,
                f.Comment,
                f.Response,
                f.RespondedAt
            })
            .ToListAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Submitted,Service Date,Rating,Category,Source,Service Type,Counter,Token,Customer Name,Phone,Email,Comment,Response,Responded At");
        foreach (var f in feedback)
        {
            var counterLabel = string.IsNullOrWhiteSpace(f.CounterDisplayName)
                ? (f.CounterNumber != null ? $"Counter {f.CounterNumber}" : "")
                : f.CounterDisplayName;
            csv.AppendLine(string.Join(",", new[]
            {
                CsvField(f.CreatedAt.ToString("u")),
                CsvField(f.ServiceDate?.ToString("u") ?? ""),
                CsvField(f.Rating.ToString()),
                CsvField(f.Category.ToString()),
                CsvField(f.Source.ToString()),
                CsvField(f.ServiceTypeName ?? ""),
                CsvField(counterLabel),
                CsvField(f.TokenDisplayNumber ?? ""),
                CsvField(f.CustomerName ?? ""),
                CsvField(f.CustomerPhone ?? ""),
                CsvField(f.CustomerEmail ?? ""),
                CsvField(f.Comment ?? ""),
                CsvField(f.Response ?? ""),
                CsvField(f.RespondedAt?.ToString("u") ?? "")
            }));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"customer-feedback-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.csv");
    }

    private static string FormatAverage(IEnumerable<int?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? "" : Math.Round(present.Average(), 1).ToString("0.#");
    }

    private static (DateOnly From, DateOnly To, DateTime RangeStart, DateTime RangeEndExclusive) ResolveReportRange(DateOnly? from, DateOnly? to)
    {
        var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var fromDate = from ?? toDate.AddDays(-6); // default: trailing 7 days
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        // Npgsql rejects Kind=Unspecified DateTimes against "timestamp with time zone" columns —
        // DateOnly.ToDateTime always produces Unspecified, so it has to be stamped UTC explicitly
        // rather than compared directly against CreatedAt (which is already UTC).
        var rangeStart = DateTime.SpecifyKind(fromDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var rangeEndExclusive = DateTime.SpecifyKind(toDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc).AddDays(1);
        return (fromDate, toDate, rangeStart, rangeEndExclusive);
    }

    private static string CsvField(string value)
    {
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
