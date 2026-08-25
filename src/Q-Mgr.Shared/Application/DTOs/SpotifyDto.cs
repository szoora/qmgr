namespace QMgr.Application.DTOs;

/// <summary>
/// Status of the platform-wide Spotify connection. Never carries token
/// values — those stay server-side, encrypted, and are never serialized out.
/// </summary>
public record SpotifyConnectionStatusDto
{
    public bool IsConnected { get; init; }
    public string? SpotifyUserId { get; init; }
    public string? DisplayName { get; init; }
    public DateTime? ConnectedAt { get; init; }
}

/// <summary>
/// Returned by GET /api/v1/platform/spotify/connect — the URL the browser
/// should navigate to for the Spotify authorization step.
/// </summary>
public record SpotifyAuthorizeUrlDto
{
    public string AuthorizeUrl { get; init; } = string.Empty;
}

/// <summary>
/// A short-lived access token for the Web Playback SDK. See
/// SpotifyController.GetPlaybackToken for why this endpoint is anonymous.
/// </summary>
public record SpotifyPlaybackTokenDto
{
    public string AccessToken { get; init; } = string.Empty;
}

public record SpotifyPlaylistDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public int TrackCount { get; init; }
}

/// <summary>
/// Sets (or clears, when both fields are null) which Spotify playlist plays
/// as background music behind a Playlist's image-gallery content.
/// </summary>
public record SetPlaylistSpotifyBackgroundRequest
{
    public string? SpotifyPlaylistId { get; init; }
    public string? SpotifyPlaylistName { get; init; }
}
