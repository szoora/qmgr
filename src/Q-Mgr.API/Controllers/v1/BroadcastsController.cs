using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Marketing;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/marketing/broadcasts")]
[Produces("application/json")]
[Authorize]
public class BroadcastsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;

    public BroadcastsController(QMgrDbContext context, ITenantContextAccessor tenantAccessor)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
    }

    private static BroadcastDto MapToDto(Broadcast b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Channel = b.Channel,
        Subject = b.Subject,
        MessageBody = b.MessageBody,
        AudienceTagFilter = b.AudienceTagFilter,
        Status = b.Status,
        ScheduledAt = b.ScheduledAt,
        SendStartedAt = b.SendStartedAt,
        SendCompletedAt = b.SendCompletedAt,
        TotalRecipients = b.TotalRecipients,
        SentCount = b.SentCount,
        FailedCount = b.FailedCount,
        CreatedAt = b.CreatedAt
    };

    [HttpGet]
    [RequirePermission(Permissions.MarketingView)]
    [ProducesResponseType(typeof(List<BroadcastDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBroadcasts()
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var broadcasts = await _context.Broadcasts
            .Where(b => b.OrganizationId == tenantContext.OrganizationId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        return Ok(broadcasts.Select(MapToDto).ToList());
    }

    [HttpGet("{broadcastId:guid}")]
    [RequirePermission(Permissions.MarketingView)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBroadcast(Guid broadcastId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var broadcast = await _context.Broadcasts
            .FirstOrDefaultAsync(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId);
        if (broadcast == null) return NotFound();

        return Ok(MapToDto(broadcast));
    }

    /// <summary>
    /// Creates a broadcast as a Draft — never sends anything by itself. Sending requires the
    /// separate Schedule action below, which needs MarketingSend, a deliberately higher-stakes
    /// permission than the MarketingManage needed here to just draft one.
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.MarketingManage)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateBroadcast([FromBody] CreateBroadcastRequest request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.MessageBody))
            return BadRequest(new ProblemDetails { Title = "Name and message body are required", Status = StatusCodes.Status400BadRequest });

        if (request.Channel == BroadcastChannel.Email && string.IsNullOrWhiteSpace(request.Subject))
            return BadRequest(new ProblemDetails { Title = "Subject is required for email broadcasts", Status = StatusCodes.Status400BadRequest });

        var currentUserId = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var broadcast = new Broadcast
        {
            OrganizationId = tenantContext.OrganizationId,
            Name = request.Name,
            Channel = request.Channel,
            Subject = request.Subject,
            MessageBody = request.MessageBody,
            AudienceTagFilter = request.AudienceTagFilter,
            Status = BroadcastStatus.Draft,
            CreatedByUserId = Guid.TryParse(currentUserId, out var uid) ? uid : Guid.Empty
        };
        _context.Broadcasts.Add(broadcast);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetBroadcast), new { broadcastId = broadcast.Id }, MapToDto(broadcast));
    }

    /// <summary>
    /// Schedules a Draft broadcast to send — either immediately (next job tick, within ~60s)
    /// or at a future ScheduledAt. This is the one action that actually causes messages to go
    /// out, which is why it's gated on MarketingSend rather than MarketingManage.
    /// </summary>
    [HttpPost("{broadcastId:guid}/schedule")]
    [RequirePermission(Permissions.MarketingSend)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ScheduleBroadcast(Guid broadcastId, [FromBody] ScheduleBroadcastRequest? request = null)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var affected = await _context.Broadcasts
            .Where(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId && b.Status == BroadcastStatus.Draft)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BroadcastStatus.Scheduled)
                .SetProperty(b => b.ScheduledAt, request != null && request.ScheduledAt.HasValue ? request.ScheduledAt : DateTime.UtcNow)
                .SetProperty(b => b.UpdatedAt, DateTime.UtcNow));

        if (affected == 0)
        {
            var exists = await _context.Broadcasts.AnyAsync(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId);
            if (!exists) return NotFound();
            return BadRequest(new ProblemDetails { Title = "Only a Draft broadcast can be scheduled", Status = StatusCodes.Status400BadRequest });
        }

        var broadcast = await _context.Broadcasts.FirstAsync(b => b.Id == broadcastId);
        return Ok(MapToDto(broadcast));
    }

    [HttpPost("{broadcastId:guid}/cancel")]
    [RequirePermission(Permissions.MarketingSend)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelBroadcast(Guid broadcastId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        // Only a still-Scheduled broadcast can be cancelled — once the job has claimed it
        // (Status = Sending) it may already have sent some recipients, so "cancel" would be
        // misleading at that point.
        var affected = await _context.Broadcasts
            .Where(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId && b.Status == BroadcastStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BroadcastStatus.Cancelled)
                .SetProperty(b => b.UpdatedAt, DateTime.UtcNow));

        if (affected == 0)
        {
            var exists = await _context.Broadcasts.AnyAsync(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId);
            if (!exists) return NotFound();
            return BadRequest(new ProblemDetails { Title = "Only a Scheduled broadcast (not yet started sending) can be cancelled", Status = StatusCodes.Status400BadRequest });
        }

        var broadcast = await _context.Broadcasts.FirstAsync(b => b.Id == broadcastId);
        return Ok(MapToDto(broadcast));
    }
}

public record ScheduleBroadcastRequest
{
    public DateTime? ScheduledAt { get; init; }
}
