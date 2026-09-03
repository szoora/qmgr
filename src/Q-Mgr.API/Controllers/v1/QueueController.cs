using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Queries.Queue;
using QMgr.Domain.Constants;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Public queue status endpoints for customer-facing displays and kiosks.
/// Note: These endpoints are intentionally open for public kiosk/display access.
/// For authenticated queue management, use the TokensController.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/queue")]
[Produces("application/json")]
[AllowAnonymous] // Public endpoints for customer kiosk/display - no authentication required
public class QueueController : ControllerBase
{
    private readonly IMediator _mediator;

    public QueueController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Gets the current queue status for a branch
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(QueueStatusDto), StatusCodes.Status200OK)]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetQueueStatus(Guid branchId)
    {
        var result = await _mediator.Send(new GetQueueStatusQuery { BranchId = branchId });
        return Ok(result);
    }

    /// <summary>
    /// Waiting tokens for a public display — the same query TokensController.GetWaitingTokens runs,
    /// but anonymous (a display screen has no session) and with every customer field stripped.
    /// The public board only ever shows ticket numbers and service names.
    /// </summary>
    [HttpGet("waiting")]
    [ProducesResponseType(typeof(List<TokenDto>), StatusCodes.Status200OK)]
    [ResponseCache(Duration = 3)]
    public async Task<IActionResult> GetPublicWaitingTokens(Guid branchId, [FromQuery] Guid? serviceTypeId = null, [FromQuery] int? limit = null)
    {
        var cappedLimit = Math.Clamp(limit ?? 50, 1, 200);
        var result = await _mediator.Send(new GetWaitingTokensQuery
        {
            BranchId = branchId,
            ServiceTypeId = serviceTypeId,
            Limit = cappedLimit
        });

        var scrubbed = result.Select(t => t with
        {
            Customer = null,
            Notes = null,
            Metadata = null,
            ExternalReference = null,
            ExternalSystem = null
        }).ToList();

        return Ok(scrubbed);
    }

    /// <summary>
    /// Gets estimated wait time for a service type
    /// </summary>
    [HttpGet("wait-time/{serviceTypeId:guid}")]
    [ProducesResponseType(typeof(WaitTimeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWaitTime(Guid branchId, Guid serviceTypeId)
    {
        var status = await _mediator.Send(new GetQueueStatusQuery { BranchId = branchId });
        var serviceType = status.ServiceTypes.FirstOrDefault(st => st.Id == serviceTypeId);

        if (serviceType == null)
            return NotFound();

        return Ok(new WaitTimeResponse
        {
            ServiceTypeId = serviceTypeId,
            ServiceTypeName = serviceType.Name,
            WaitingCount = serviceType.WaitingCount,
            EstimatedWaitMinutes = serviceType.EstimatedWaitMinutes,
            CountersActive = serviceType.CountersActive
        });
    }
}

public record WaitTimeResponse
{
    public Guid ServiceTypeId { get; init; }
    public string ServiceTypeName { get; init; } = string.Empty;
    public int WaitingCount { get; init; }
    public int EstimatedWaitMinutes { get; init; }
    public int CountersActive { get; init; }
}
