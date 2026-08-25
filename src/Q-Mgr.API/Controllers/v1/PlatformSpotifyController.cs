using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Manages the single platform-wide Spotify connection. Gated to Super Admin
/// (via the existing platform.settings.* permissions) — this is deliberately
/// NOT a per-organization integration; tenants only ever pick a playlist
/// from this one connected account (see SpotifyController for that).
/// </summary>
[ApiController]
[Route("api/v1/platform/spotify")]
[Produces("application/json")]
[Authorize]
public class PlatformSpotifyController : ControllerBase
{
    private readonly ISpotifyService _spotifyService;
    private readonly ILogger<PlatformSpotifyController> _logger;

    public PlatformSpotifyController(ISpotifyService spotifyService, ILogger<PlatformSpotifyController> logger)
    {
        _spotifyService = spotifyService;
        _logger = logger;
    }

    [HttpGet("status")]
    [RequirePermission("platform.settings.view")]
    [ProducesResponseType(typeof(SpotifyConnectionStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var status = await _spotifyService.GetStatusAsync(ct);
        return Ok(status);
    }

    [HttpGet("connect")]
    [RequirePermission("platform.settings.edit")]
    [ProducesResponseType(typeof(SpotifyAuthorizeUrlDto), StatusCodes.Status200OK)]
    public IActionResult Connect()
    {
        var (authorizeUrl, _, _) = _spotifyService.BuildAuthorizeRequest();
        return Ok(new SpotifyAuthorizeUrlDto { AuthorizeUrl = authorizeUrl });
    }

    [HttpPost("callback")]
    [RequirePermission("platform.settings.edit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Callback([FromBody] SpotifyCallbackRequest request, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null)
            return Unauthorized();

        var codeVerifier = _spotifyService.TryGetCodeVerifier(request.State);
        if (codeVerifier == null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Authorization expired",
                Detail = "This Spotify authorization attempt expired or was already used. Please try connecting again.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            await _spotifyService.ExchangeCodeAsync(request.Code, request.State, codeVerifier, currentUserId.Value, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Spotify connection failed during callback");
            return BadRequest(new ProblemDetails
            {
                Title = "Spotify connection failed",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok();
    }

    [HttpPost("disconnect")]
    [RequirePermission("platform.settings.edit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        await _spotifyService.DisconnectAsync(ct);
        return Ok();
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

public record SpotifyCallbackRequest
{
    public string Code { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
}
