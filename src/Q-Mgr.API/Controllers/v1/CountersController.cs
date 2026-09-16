using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.API.Authorization;
using QMgr.Filters;
using QMgr.Application.Commands.Queue;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/counters")]
[Authorize]
[RequirePermission(Permissions.QueueManage)]
[RequireModule(ModuleCodes.CoreQueue)]
[Produces("application/json")]
public class CountersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IStaffSystemAwards _systemAwards;
    private readonly ILogger<CountersController> _logger;

    public CountersController(IMediator mediator, IStaffSystemAwards systemAwards, ILogger<CountersController> logger)
    {
        _mediator = mediator;
        _systemAwards = systemAwards;
        _logger = logger;
    }

    /// <summary>
    /// Calls the next token in queue for the counter
    /// </summary>
    [HttpPost("{counterId:guid}/call-next")]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CallNextToken(Guid counterId)
    {
        var userId = GetCurrentUserId();

        var result = await _mediator.Send(new CallNextTokenCommand
        {
            CounterId = counterId,
            UserId = userId
        });

        if (result == null)
            return NoContent(); // No tokens waiting

        _logger.LogInformation("Counter {CounterId} called token {TokenId}", counterId, result.Id);

        return Ok(result);
    }

    /// <summary>
    /// Calls a specific token to the counter
    /// </summary>
    [HttpPost("{counterId:guid}/call/{tokenId:guid}")]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CallSpecificToken(Guid counterId, Guid tokenId)
    {
        var userId = GetCurrentUserId();

        var result = await _mediator.Send(new CallSpecificTokenCommand
        {
            TokenId = tokenId,
            CounterId = counterId,
            UserId = userId
        });

        if (result == null)
            return NotFound();

        return Ok(result);
    }

    /// <summary>
    /// Marks the current service as complete
    /// </summary>
    [HttpPost("{counterId:guid}/complete")]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteService(Guid counterId, [FromBody] CompleteServiceRequest request)
    {
        var userId = GetCurrentUserId();

        var result = await _mediator.Send(new CompleteServiceCommand
        {
            TokenId = request.TokenId,
            UserId = userId,
            Notes = request.Notes
        });

        if (result == null)
            return NotFound(new ProblemDetails { Title = "Token not found", Status = StatusCodes.Status404NotFound });

        _logger.LogInformation("Counter {CounterId} completed service for token {TokenId}", counterId, request.TokenId);

        // Staff Performance system award for the serving user — after the command has committed,
        // policy-gated (off by default) and never throwing, the same post-commit side-effect rule
        // the visitor badge token follows.
        await _systemAwards.CreditTokenServedAsync(request.TokenId);

        return Ok(result);
    }

    /// <summary>
    /// Marks current token as no-show
    /// </summary>
    [HttpPost("{counterId:guid}/no-show")]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkNoShow(Guid counterId, [FromBody] NoShowRequest request)
    {
        var userId = GetCurrentUserId();

        var result = await _mediator.Send(new MarkNoShowCommand
        {
            TokenId = request.TokenId,
            UserId = userId
        });

        // 200 with the updated token, matching complete and transfer. This used to be 204, which
        // made no-show the one action of the three whose caller had to refetch to repaint.
        if (result == null)
            return NotFound(new ProblemDetails { Title = "Token not found", Status = StatusCodes.Status404NotFound });

        _logger.LogInformation("Counter {CounterId} marked token {TokenId} as no-show", counterId, request.TokenId);

        return Ok(result);
    }

    /// <summary>
    /// Transfers a token to another counter
    /// </summary>
    [HttpPost("{counterId:guid}/transfer")]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TransferToken(Guid counterId, [FromBody] TransferTokenRequest request)
    {
        var userId = GetCurrentUserId();

        var result = await _mediator.Send(new TransferTokenCommand
        {
            TokenId = request.TokenId,
            ToCounterId = request.ToCounterId,
            UserId = userId,
            Reason = request.Reason
        });

        if (result == null)
            return NotFound();

        return Ok(result);
    }

    private Guid GetCurrentUserId()
    {
        var claim = User.FindFirst("sub") ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim != null ? Guid.Parse(claim.Value) : Guid.Empty;
    }
}

public record CompleteServiceRequest
{
    public Guid TokenId { get; init; }
    public string? Notes { get; init; }
}

public record NoShowRequest
{
    public Guid TokenId { get; init; }
}

public record TransferTokenRequest
{
    public Guid TokenId { get; init; }
    public Guid ToCounterId { get; init; }
    public string? Reason { get; init; }
}
