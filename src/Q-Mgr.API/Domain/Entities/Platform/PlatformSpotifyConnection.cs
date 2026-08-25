using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Platform;

/// <summary>
/// Platform-wide Spotify connection — a single account connected once by a
/// Super Admin (not per-organization), used to source playlists tenants can
/// pick from for image-gallery background music. Deliberately a singleton:
/// the app never creates a second row, always upserts the one with the
/// well-known Id below.
/// </summary>
public class PlatformSpotifyConnection : BaseAuditableEntity
{
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000005907");

    public string SpotifyUserId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    // Encrypted at rest via IDataProtector — never store or log the plaintext.
    public string AccessTokenProtected { get; set; } = string.Empty;
    public string RefreshTokenProtected { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresAt { get; set; }
    public string Scopes { get; set; } = string.Empty;

    public Guid ConnectedByUserId { get; set; }
    public DateTime ConnectedAt { get; set; }
}
