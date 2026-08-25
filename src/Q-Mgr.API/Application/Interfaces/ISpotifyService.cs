using QMgr.Application.DTOs;

namespace QMgr.Application.Interfaces;

/// <summary>
/// Manages the single platform-wide Spotify connection (OAuth Authorization
/// Code + PKCE — no client secret needed) and exposes the connected
/// account's playlists for tenants to pick from.
/// </summary>
public interface ISpotifyService
{
    /// <summary>
    /// Builds the Spotify authorize URL and returns it along with the PKCE
    /// state/verifier pair the caller must persist until the callback fires.
    /// </summary>
    (string AuthorizeUrl, string State, string CodeVerifier) BuildAuthorizeRequest();

    /// <summary>
    /// Looks up the code_verifier cached for a given state by BuildAuthorizeRequest.
    /// Returns null if the state is unknown or expired.
    /// </summary>
    string? TryGetCodeVerifier(string state);

    /// <summary>
    /// Exchanges an authorization code for tokens and persists the
    /// connection (upserting the singleton row). Throws on failure.
    /// </summary>
    Task ExchangeCodeAsync(string code, string state, string codeVerifier, Guid connectedByUserId, CancellationToken ct = default);

    Task<SpotifyConnectionStatusDto> GetStatusAsync(CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a currently-valid access token for the platform connection,
    /// refreshing it first if it's expired or near expiry. Returns null if
    /// nothing is connected.
    /// </summary>
    Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default);

    Task<List<SpotifyPlaylistDto>> GetPlaylistsAsync(CancellationToken ct = default);
}
