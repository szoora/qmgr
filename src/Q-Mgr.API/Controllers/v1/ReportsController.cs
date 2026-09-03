using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
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
