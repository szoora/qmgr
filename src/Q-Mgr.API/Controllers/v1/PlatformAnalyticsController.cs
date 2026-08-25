using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Platform analytics and reporting endpoints
/// </summary>
[ApiController]
[Route("api/v1/analytics/platform")]
[Authorize]
[RequirePermission(Permissions.PlatformAdmin)]
[Produces("application/json")]
public class PlatformAnalyticsController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ILogger<PlatformAnalyticsController> _logger;

    public PlatformAnalyticsController(
        QMgrDbContext dbContext,
        ILogger<PlatformAnalyticsController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Get platform-wide analytics metrics
    /// </summary>
    [HttpGet("metrics")]
    [ProducesResponseType(typeof(PlatformAnalyticsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMetrics(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var from = fromDate ?? DateTime.UtcNow.AddMonths(-1);
        var to = toDate ?? DateTime.UtcNow;

        var totalOrgs = await _dbContext.Organizations.CountAsync();
        var activeOrgs = await _dbContext.Organizations
            .CountAsync(o => o.Status == TenantStatus.Active || o.Status == TenantStatus.Trialing);

        var activeSubscriptions = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.Status == SubscriptionStatus.Active)
            .ToListAsync();

        var mrr = activeSubscriptions.Sum(s =>
            s.BillingCycle == BillingCycle.Monthly
                ? s.Plan?.MonthlyPriceUsd ?? 0
                : (s.Plan?.AnnualPriceUsd ?? 0) / 12);

        var arr = mrr * 12;

        var totalUsage = await _dbContext.UsageRecords
            .Where(u => u.CreatedAt >= from && u.CreatedAt <= to)
            .SumAsync(u => u.TokensCreated);

        var totalApiCalls = await _dbContext.UsageRecords
            .Where(u => u.CreatedAt >= from && u.CreatedAt <= to)
            .SumAsync(u => u.ApiCalls);

        return Ok(new PlatformAnalyticsDto
        {
            TotalOrganizations = totalOrgs,
            ActiveOrganizations = activeOrgs,
            MonthlyRecurringRevenue = mrr,
            AnnualRecurringRevenue = arr,
            TotalTokensServed = totalUsage,
            TotalApiCalls = totalApiCalls,
            From = from,
            To = to
        });
    }

    /// <summary>
    /// Get tenant growth trends over time
    /// </summary>
    [HttpGet("growth")]
    [ProducesResponseType(typeof(List<GrowthTrendDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGrowthTrends(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string groupBy = "month")
    {
        var from = fromDate ?? DateTime.UtcNow.AddYears(-1);
        var to = toDate ?? DateTime.UtcNow;

        var organizations = await _dbContext.Organizations
            .Where(o => o.CreatedAt >= from && o.CreatedAt <= to)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync();

        // Real churn: subscriptions actually cancelled (Subscription.CancelledAt),
        // not the placeholder 0 this used to hardcode.
        var cancellations = await _dbContext.Subscriptions
            .Where(s => s.CancelledAt != null && s.CancelledAt >= from && s.CancelledAt <= to)
            .Select(s => s.CancelledAt!.Value)
            .ToListAsync();

        var trends = new List<GrowthTrendDto>();

        if (groupBy.ToLower() == "day")
        {
            var grouped = organizations
                .GroupBy(o => o.CreatedAt.Date)
                .OrderBy(g => g.Key);

            int cumulative = await _dbContext.Organizations.CountAsync(o => o.CreatedAt < from);

            foreach (var group in grouped)
            {
                var activeAtPeriodStart = cumulative;
                cumulative += group.Count();

                var periodStart = group.Key;
                var periodEnd = periodStart.AddDays(1);
                var churned = cancellations.Count(c => c >= periodStart && c < periodEnd);
                var churnRate = activeAtPeriodStart > 0
                    ? Math.Round((decimal)churned / activeAtPeriodStart * 100, 2)
                    : 0;

                trends.Add(new GrowthTrendDto
                {
                    Date = group.Key,
                    NewTenants = group.Count(),
                    CumulativeTenants = cumulative,
                    ChurnRate = churnRate
                });
            }
        }
        else // month
        {
            var grouped = organizations
                .GroupBy(o => new { o.CreatedAt.Year, o.CreatedAt.Month })
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month);

            int cumulative = await _dbContext.Organizations.CountAsync(o => o.CreatedAt < from);

            foreach (var group in grouped)
            {
                var activeAtPeriodStart = cumulative;
                cumulative += group.Count();

                var periodStart = new DateTime(group.Key.Year, group.Key.Month, 1);
                var periodEnd = periodStart.AddMonths(1);
                var churned = cancellations.Count(c => c >= periodStart && c < periodEnd);
                var churnRate = activeAtPeriodStart > 0
                    ? Math.Round((decimal)churned / activeAtPeriodStart * 100, 2)
                    : 0;

                trends.Add(new GrowthTrendDto
                {
                    Date = periodStart,
                    NewTenants = group.Count(),
                    CumulativeTenants = cumulative,
                    ChurnRate = churnRate
                });
            }
        }

        return Ok(trends);
    }

    /// <summary>
    /// Get revenue metrics by period
    /// </summary>
    [HttpGet("revenue")]
    [ProducesResponseType(typeof(List<RevenueMetricsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRevenueMetrics(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var from = fromDate ?? DateTime.UtcNow.AddMonths(-12);
        var to = toDate ?? DateTime.UtcNow;

        var subscriptions = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.CreatedAt >= from && s.CreatedAt <= to)
            .ToListAsync();

        var metrics = subscriptions
            .GroupBy(s => new { s.CreatedAt.Year, s.CreatedAt.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                var monthlyRevenue = g.Sum(s =>
                    s.BillingCycle == BillingCycle.Monthly
                        ? s.Plan?.MonthlyPriceUsd ?? 0
                        : (s.Plan?.AnnualPriceUsd ?? 0) / 12);

                return new RevenueMetricsDto
                {
                    Period = new DateTime(g.Key.Year, g.Key.Month, 1),
                    MRR = monthlyRevenue,
                    ARR = monthlyRevenue * 12,
                    TotalRevenue = g.Sum(s =>
                        s.BillingCycle == BillingCycle.Monthly
                            ? s.Plan?.MonthlyPriceUsd ?? 0
                            : s.Plan?.AnnualPriceUsd ?? 0),
                    NewSubscriptions = g.Count(),
                    TrialConversions = g.Count(s => s.TrialEnd.HasValue && s.Status == SubscriptionStatus.Active)
                };
            })
            .ToList();

        return Ok(metrics);
    }

    /// <summary>
    /// Get platform usage trends
    /// </summary>
    [HttpGet("usage")]
    [ProducesResponseType(typeof(List<UsageTrendDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsageTrends(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var from = fromDate ?? DateTime.UtcNow.AddMonths(-12);
        var to = toDate ?? DateTime.UtcNow;

        var usage = await _dbContext.UsageRecords
            .Where(u => u.CreatedAt >= from && u.CreatedAt <= to)
            .GroupBy(u => new { u.Year, u.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new UsageTrendDto
            {
                Date = new DateTime(g.Key.Year, g.Key.Month, 1),
                TotalTokens = g.Sum(u => u.TokensCreated),
                TotalApiCalls = g.Sum(u => u.ApiCalls),
                ActiveUsers = g.Sum(u => u.ActiveUsers),
                ActiveBranches = g.Sum(u => u.ActiveBranches),
                StorageUsedMb = g.Sum(u => u.StorageUsedBytes) / 1024 / 1024
            })
            .ToListAsync();

        return Ok(usage);
    }

    /// <summary>
    /// Get subscription tier distribution
    /// </summary>
    [HttpGet("tier-distribution")]
    [ProducesResponseType(typeof(List<TierDistributionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTierDistribution()
    {
        var distribution = await _dbContext.Organizations
            .GroupBy(o => o.Tier)
            .Select(g => new TierDistributionDto
            {
                Tier = g.Key.ToString(),
                Count = g.Count(),
                Percentage = 0 // Will calculate after getting total
            })
            .ToListAsync();

        var total = distribution.Sum(d => d.Count);
        foreach (var item in distribution)
        {
            item.Percentage = total > 0 ? (decimal)item.Count / total * 100 : 0;
        }

        return Ok(distribution);
    }

    /// <summary>
    /// Get top performing organizations
    /// </summary>
    [HttpGet("top-organizations")]
    [ProducesResponseType(typeof(List<TopOrganizationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTopOrganizations([FromQuery] int limit = 10)
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 1);

        var topOrgs = await _dbContext.Organizations
            .Where(o => o.Status == TenantStatus.Active)
            .Select(o => new
            {
                Organization = o,
                Usage = _dbContext.UsageRecords
                    .Where(u => u.OrganizationId == o.Id && u.Year == now.Year && u.Month == now.Month)
                    .FirstOrDefault()
            })
            .OrderByDescending(x => x.Usage != null ? x.Usage.TokensCreated : 0)
            .Take(limit)
            .ToListAsync();

        var result = topOrgs.Select(x => new TopOrganizationDto
        {
            OrganizationId = x.Organization.Id,
            OrganizationName = x.Organization.Name,
            Tier = x.Organization.Tier.ToString(),
            TokensThisMonth = x.Usage?.TokensCreated ?? 0,
            ApiCallsThisMonth = x.Usage?.ApiCalls ?? 0,
            ActiveUsers = x.Usage?.ActiveUsers ?? 0
        }).ToList();

        return Ok(result);
    }
}

#region DTOs

public class PlatformAnalyticsDto
{
    public int TotalOrganizations { get; set; }
    public int ActiveOrganizations { get; set; }
    public decimal MonthlyRecurringRevenue { get; set; }
    public decimal AnnualRecurringRevenue { get; set; }
    public int TotalTokensServed { get; set; }
    public int TotalApiCalls { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

public class GrowthTrendDto
{
    public DateTime Date { get; set; }
    public int NewTenants { get; set; }
    public int CumulativeTenants { get; set; }
    public decimal ChurnRate { get; set; }
}

public class RevenueMetricsDto
{
    public DateTime Period { get; set; }
    public decimal MRR { get; set; }
    public decimal ARR { get; set; }
    public decimal TotalRevenue { get; set; }
    public int NewSubscriptions { get; set; }
    public int TrialConversions { get; set; }
}

public class UsageTrendDto
{
    public DateTime Date { get; set; }
    public int TotalTokens { get; set; }
    public int TotalApiCalls { get; set; }
    public int ActiveUsers { get; set; }
    public int ActiveBranches { get; set; }
    public long StorageUsedMb { get; set; }
}

public class TierDistributionDto
{
    public string Tier { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Percentage { get; set; }
}

public class TopOrganizationDto
{
    public Guid OrganizationId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public int TokensThisMonth { get; set; }
    public int ApiCallsThisMonth { get; set; }
    public int ActiveUsers { get; set; }
}

#endregion
