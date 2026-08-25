using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Tenant-facing read access to the platform's connected Spotify account —
/// any authenticated user can list playlists to pick one as background
/// music for their own image galleries. Connecting/disconnecting the
/// account itself is Super Admin only (see PlatformSpotifyController).
/// </summary>
[ApiController]
[Route("api/v1/spotify")]
[Produces("application/json")]
[Authorize]
public class SpotifyController : ControllerBase
{
    private readonly ISpotifyService _spotifyService;

    public SpotifyController(ISpotifyService spotifyService)
    {
        _spotifyService = spotifyService;
    }

    [HttpGet("playlists")]
    [ProducesResponseType(typeof(List<SpotifyPlaylistDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlaylists(CancellationToken ct)
    {
        var playlists = await _spotifyService.GetPlaylistsAsync(ct);
        return Ok(playlists);
    }

    /// <summary>
    /// Hands out a short-lived Spotify access token for the Web Playback SDK
    /// to authenticate with. Deliberately AllowAnonymous — CustomerDisplay/
    /// SignageDisplay are public, unauthenticated pages, and the SDK needs
    /// this token in client-side JS to turn the browser tab into a playable
    /// device. Accepted, bounded risk: the token expires hourly and only
    /// carries playback-control scopes, no account-level access.
    /// </summary>
    [HttpGet("playback-token")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SpotifyPlaybackTokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPlaybackToken(CancellationToken ct)
    {
        var token = await _spotifyService.GetValidAccessTokenAsync(ct);
        if (token == null)
            return NotFound(new ProblemDetails { Title = "Spotify not connected", Status = StatusCodes.Status404NotFound });

        return Ok(new SpotifyPlaybackTokenDto { AccessToken = token });
    }
}
