using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Content;
using QMgr.Infrastructure.Data;
using QMgr.Filters;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Lightweight campaign layer over the existing playlist/media content pipeline —
/// a campaign is a named, date-ranged grouping over playlist items that already
/// exist, not a parallel content system. See docs/TASK_TRACKER.md's Campaign
/// feature entry for the design rationale.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[RequireModule(ModuleCodes.EngagementCommunications)]
[Produces("application/json")]
public class CampaignsController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<CampaignsController> _logger;

    public CampaignsController(QMgrDbContext dbContext, ITenantContextAccessor tenantAccessor, ILogger<CampaignsController> logger)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// SECURITY: Campaign has no global EF query filter (matching Playlist/Display/
    /// DisplayZone's existing precedent in ContentController), so every action that
    /// reaches one by ID must verify branch ownership explicitly.
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
            return null;

        var branchExists = await _dbContext.Branches
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

    private static CampaignDto ToDto(Campaign c) => new()
    {
        Id = c.Id,
        BranchId = c.BranchId,
        Name = c.Name,
        Description = c.Description,
        StartDate = c.StartDate,
        EndDate = c.EndDate,
        IsActive = c.IsActive,
        CreatedAt = c.CreatedAt
    };

    /// <summary>
    /// Lists campaigns for a branch
    /// </summary>
    [HttpGet("branches/{branchId:guid}/campaigns")]
    [RequirePermission(Permissions.ContentView)]
    [ProducesResponseType(typeof(List<CampaignDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCampaigns(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var campaigns = await _dbContext.Campaigns
            .Where(c => c.BranchId == branchId)
            .OrderByDescending(c => c.StartDate)
            .Select(c => new CampaignDto
            {
                Id = c.Id,
                BranchId = c.BranchId,
                Name = c.Name,
                Description = c.Description,
                StartDate = c.StartDate,
                EndDate = c.EndDate,
                IsActive = c.IsActive,
                CreatedAt = c.CreatedAt,
                // Correlated subquery in the same SELECT (EF translates the DbSet reference
                // inline) — one round-trip for the whole list, not N+1.
                TotalImpressions = _dbContext.CampaignImpressions.Count(i => i.CampaignId == c.Id)
            })
            .ToListAsync();

        return Ok(campaigns);
    }

    /// <summary>
    /// Creates a new campaign
    /// </summary>
    [HttpPost("branches/{branchId:guid}/campaigns")]
    [RequirePermission(Permissions.ContentCreate)]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateCampaign(Guid branchId, [FromBody] CreateCampaignRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ProblemDetails { Title = "Validation failed", Detail = "Name is required.", Status = StatusCodes.Status400BadRequest });

        if (request.EndDate < request.StartDate)
            return BadRequest(new ProblemDetails { Title = "Validation failed", Detail = "End date must be on or after the start date.", Status = StatusCodes.Status400BadRequest });

        var campaign = new Campaign
        {
            BranchId = branchId,
            Name = request.Name,
            Description = request.Description,
            StartDate = request.StartDate,
            EndDate = request.EndDate
        };

        _dbContext.Campaigns.Add(campaign);
        await _dbContext.SaveChangesAsync();

        return CreatedAtAction(nameof(GetCampaigns), new { branchId }, ToDto(campaign));
    }

    /// <summary>
    /// Updates a campaign
    /// </summary>
    [HttpPut("campaigns/{campaignId:guid}")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCampaign(Guid campaignId, [FromBody] UpdateCampaignRequest request)
    {
        var campaign = await _dbContext.Campaigns.FindAsync(campaignId);
        if (campaign == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(campaign.BranchId);
        if (branchError != null) return branchError;

        var newStart = request.StartDate ?? campaign.StartDate;
        var newEnd = request.EndDate ?? campaign.EndDate;
        if (newEnd < newStart)
            return BadRequest(new ProblemDetails { Title = "Validation failed", Detail = "End date must be on or after the start date.", Status = StatusCodes.Status400BadRequest });

        campaign.Name = request.Name ?? campaign.Name;
        campaign.Description = request.Description ?? campaign.Description;
        campaign.StartDate = newStart;
        campaign.EndDate = newEnd;
        campaign.IsActive = request.IsActive ?? campaign.IsActive;
        campaign.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return Ok(ToDto(campaign));
    }

    /// <summary>
    /// Deletes a campaign. Playlist items attached to it are not deleted — their
    /// CampaignId is cleared (see CampaignConfiguration's SetNull delete behavior),
    /// so removing a campaign never removes real playlist content.
    /// </summary>
    [HttpDelete("campaigns/{campaignId:guid}")]
    [RequirePermission(Permissions.ContentDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCampaign(Guid campaignId)
    {
        var campaign = await _dbContext.Campaigns.FindAsync(campaignId);
        if (campaign == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(campaign.BranchId);
        if (branchError != null) return branchError;

        _dbContext.Campaigns.Remove(campaign);
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// Records that a campaign-attached media item was shown on a public display.
    /// Anonymous by design — called from the unauthenticated Customer Display page,
    /// matching ContentController.GetPlaylist's existing [AllowAnonymous] precedent
    /// for public-display-facing reads. BranchId is taken from the campaign record
    /// itself, never trusted from the request body.
    /// </summary>
    [HttpPost("campaigns/{campaignId:guid}/impressions")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordImpression(Guid campaignId, [FromBody] RecordCampaignImpressionRequest request)
    {
        var campaign = await _dbContext.Campaigns.FindAsync(campaignId);
        if (campaign == null)
            return NotFound();

        var now = DateTime.UtcNow;
        if (!campaign.IsActive || now < campaign.StartDate || now > campaign.EndDate)
            return NotFound();

        _dbContext.CampaignImpressions.Add(new CampaignImpression
        {
            CampaignId = campaignId,
            MediaContentId = request.MediaContentId,
            BranchId = campaign.BranchId
        });
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }

    #region Impression stats

    /// <summary>Inclusive day span an impression report may cover.</summary>
    private const int MaxStatsRangeDays = 366;

    /// <summary>
    /// Impression report for one campaign: daily series, per-media and per-branch breakdowns.
    /// Defaults to the trailing 30 days; the range is capped at 366 days (the From date is
    /// pulled forward if a wider one is asked for — the returned From/To reflect what was
    /// actually applied). Follows VisitorsController.GetVisitorReport's model: pull the
    /// minimal projection for the range, aggregate in memory.
    /// </summary>
    [HttpGet("campaigns/{campaignId:guid}/stats")]
    [RequirePermission(Permissions.ContentView)]
    [ProducesResponseType(typeof(CampaignStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCampaignStats(Guid campaignId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var (error, stats) = await BuildCampaignStatsAsync(campaignId, from, to);
        if (error != null) return error;
        return Ok(stats);
    }

    /// <summary>
    /// Same report as GetCampaignStats as a CSV download: a Day,Impressions section, a blank
    /// line, a Media,Impressions section, a blank line, then Branch,Impressions. Gated on
    /// ReportsExport like the visitor log export — it's a file export, not an on-screen view.
    /// </summary>
    [HttpGet("campaigns/{campaignId:guid}/stats/export")]
    [RequirePermission(Permissions.ReportsExport)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportCampaignStats(Guid campaignId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var (error, stats) = await BuildCampaignStatsAsync(campaignId, from, to);
        if (error != null) return error;

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Day,Impressions");
        foreach (var d in stats!.Daily)
            csv.AppendLine($"{d.Day:yyyy-MM-dd},{d.Count}");

        csv.AppendLine();
        csv.AppendLine("Media,Impressions");
        foreach (var m in stats.ByMedia)
            csv.AppendLine($"{CsvField(m.Title)},{m.Count}");

        csv.AppendLine();
        csv.AppendLine("Branch,Impressions");
        foreach (var b in stats.ByBranch)
            csv.AppendLine($"{CsvField(b.BranchName)},{b.Count}");

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"campaign-{Slugify(stats.CampaignName)}-impressions.csv");
    }

    private async Task<(IActionResult? Error, CampaignStatsDto? Stats)> BuildCampaignStatsAsync(Guid campaignId, DateOnly? from, DateOnly? to)
    {
        var campaign = await _dbContext.Campaigns.FindAsync(campaignId);
        if (campaign == null)
            return (NotFound(), null);

        var branchError = await VerifyBranchOwnership(campaign.BranchId);
        if (branchError != null) return (branchError, null);

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveStatsRange(from, to);

        var rows = await _dbContext.CampaignImpressions
            .Where(i => i.CampaignId == campaignId && i.CreatedAt >= rangeStart && i.CreatedAt < rangeEndExclusive)
            .Select(i => new { i.MediaContentId, i.BranchId, i.CreatedAt })
            .ToListAsync();

        var mediaIds = rows.Select(r => r.MediaContentId).Distinct().ToList();
        var mediaNames = mediaIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.MediaContents
                .Where(m => mediaIds.Contains(m.Id))
                .Select(m => new { m.Id, m.Name })
                .ToDictionaryAsync(m => m.Id, m => m.Name);

        var branchIds = rows.Select(r => r.BranchId).Distinct().ToList();
        var branchNames = branchIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.Branches
                .Where(b => branchIds.Contains(b.Id))
                .Select(b => new { b.Id, b.Name })
                .ToDictionaryAsync(b => b.Id, b => b.Name);

        // Zero-fill every day in the range so the daily series is a continuous line and the
        // CSV reads as a complete calendar, not just the days that happened to have traffic.
        var countsByDay = rows
            .GroupBy(r => DateOnly.FromDateTime(r.CreatedAt))
            .ToDictionary(g => g.Key, g => g.Count());
        var daily = new List<DayCountDto>();
        for (var day = fromDate; day <= toDate; day = day.AddDays(1))
            daily.Add(new DayCountDto { Day = day, Count = countsByDay.GetValueOrDefault(day) });

        var stats = new CampaignStatsDto
        {
            CampaignId = campaign.Id,
            CampaignName = campaign.Name,
            From = fromDate,
            To = toDate,
            TotalImpressions = rows.Count,
            UniqueMediaItems = mediaIds.Count,
            Daily = daily,
            ByMedia = rows
                .GroupBy(r => r.MediaContentId)
                .Select(g => new CampaignMediaImpressionsDto
                {
                    MediaContentId = g.Key,
                    Title = mediaNames.TryGetValue(g.Key, out var name) ? name : "(removed media)",
                    Count = g.Count()
                })
                .OrderByDescending(m => m.Count)
                .ThenBy(m => m.Title)
                .ToList(),
            ByBranch = rows
                .GroupBy(r => r.BranchId)
                .Select(g => new CampaignBranchImpressionsDto
                {
                    BranchId = g.Key,
                    BranchName = branchNames.TryGetValue(g.Key, out var name) ? name : "(removed branch)",
                    Count = g.Count()
                })
                .OrderByDescending(b => b.Count)
                .ThenBy(b => b.BranchName)
                .ToList()
        };

        return (null, stats);
    }

    private static (DateOnly From, DateOnly To, DateTime RangeStart, DateTime RangeEndExclusive) ResolveStatsRange(DateOnly? from, DateOnly? to)
    {
        var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var fromDate = from ?? toDate.AddDays(-29); // default: trailing 30 days inclusive
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);
        if (toDate.DayNumber - fromDate.DayNumber >= MaxStatsRangeDays)
            fromDate = toDate.AddDays(-(MaxStatsRangeDays - 1));

        // Npgsql rejects Kind=Unspecified DateTimes against "timestamp with time zone" columns —
        // DateOnly.ToDateTime always produces Unspecified, so stamp UTC explicitly (same as
        // VisitorsController.ResolveReportRange).
        var rangeStart = DateTime.SpecifyKind(fromDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var rangeEndExclusive = DateTime.SpecifyKind(toDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc).AddDays(1);
        return (fromDate, toDate, rangeStart, rangeEndExclusive);
    }

    private static string CsvField(string value)
    {
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    /// <summary>ASCII-only, dash-separated, max 40 chars — safe as a download filename.</summary>
    private static string Slugify(string name)
    {
        var sb = new System.Text.StringBuilder();
        var lastWasDash = true;
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch < 128 && char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
            if (sb.Length >= 40) break;
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : "campaign";
    }

    #endregion
}
