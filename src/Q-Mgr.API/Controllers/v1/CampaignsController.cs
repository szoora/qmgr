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
                CreatedAt = c.CreatedAt
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
}
