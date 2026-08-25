using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.API.Hubs;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// A branch's scrolling ticker/marquee banner — opt-in, shown on both
/// CustomerDisplay and SignageDisplay when enabled (see docs/TASK_TRACKER.md
/// for the design decision: one config shared across both display routes,
/// not configured separately per route).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Produces("application/json")]
public class DisplayBannerController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly IDisplayHubContext _displayHub;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<DisplayBannerController> _logger;

    public DisplayBannerController(QMgrDbContext context, IDisplayHubContext displayHub, ITenantContextAccessor tenantAccessor, ILogger<DisplayBannerController> logger)
    {
        _context = context;
        _displayHub = displayHub;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// SECURITY: without this, any authenticated user holding SettingsEdit in their own org
    /// could overwrite another tenant's public display banner by supplying a foreign branchId —
    /// same pattern as TokensController/PrintController's VerifyBranchOwnership.
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

        var branchExists = await _context.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }

    /// <summary>
    /// Gets a branch's display banner config. AllowAnonymous — CustomerDisplay
    /// and SignageDisplay are public, unauthenticated pages that need to read
    /// this to know whether/how to render the banner. The config itself isn't
    /// sensitive: it's literally what's about to be shown on a public screen.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/display-banner")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(DisplayBannerSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDisplayBanner(Guid branchId)
    {
        var settings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        if (settings == null || !settings.DisplayBannerEnabled || settings.DisplayBannerSettingsJson == null)
        {
            return Ok(new DisplayBannerSettingsDto { Enabled = false });
        }

        try
        {
            var banner = JsonSerializer.Deserialize<DisplayBannerSettingsDto>(settings.DisplayBannerSettingsJson);
            if (banner == null)
                return Ok(new DisplayBannerSettingsDto { Enabled = false });

            banner.Enabled = true; // DisplayBannerEnabled is the source of truth for on/off, not the blob
            return Ok(banner);
        }
        catch
        {
            return Ok(new DisplayBannerSettingsDto { Enabled = false });
        }
    }

    [HttpPut("branches/{branchId:guid}/display-banner")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(DisplayBannerSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateDisplayBanner(Guid branchId, [FromBody] DisplayBannerSettingsDto request)
    {
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var settings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        if (settings == null)
        {
            settings = new QMgr.Domain.Entities.Organization.BranchSettings
            {
                Id = Guid.NewGuid(),
                BranchId = branchId
            };
            _context.BranchSettings.Add(settings);
        }

        settings.DisplayBannerEnabled = request.Enabled;
        settings.DisplayBannerSettingsJson = JsonSerializer.Serialize(request);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated display banner settings for branch {BranchId}", branchId);

        await _displayHub.UpdateDisplayBanner(branchId);

        return Ok(request);
    }
}
