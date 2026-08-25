using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Platform;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

public class SpotifyService : ISpotifyService
{
    private const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    private const string ApiBase = "https://api.spotify.com/v1";

    // streaming: required for the Web Playback SDK. playlist-read-*: to list
    // playlists tenants can pick from. user-read-*: to show "connected as X".
    private const string Scopes = "streaming user-read-email user-read-private playlist-read-private playlist-read-collaborative user-modify-playback-state user-read-playback-state";

    private static readonly TimeSpan PkceStateTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(2);

    private readonly QMgrDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IDataProtector _protector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SpotifyService> _logger;

    public SpotifyService(
        QMgrDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IDataProtectionProvider dataProtectionProvider,
        IConfiguration configuration,
        ILogger<SpotifyService> logger)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _protector = dataProtectionProvider.CreateProtector("Spotify.Tokens.v1");
        _configuration = configuration;
        _logger = logger;
    }

    private string ClientId => _configuration["Spotify:ClientId"]
        ?? throw new InvalidOperationException("Spotify:ClientId is not configured.");

    private string RedirectUri => _configuration["Spotify:RedirectUri"]
        ?? throw new InvalidOperationException("Spotify:RedirectUri is not configured.");

    public (string AuthorizeUrl, string State, string CodeVerifier) BuildAuthorizeRequest()
    {
        var state = GenerateUrlSafeRandom(32);
        var codeVerifier = GenerateUrlSafeRandom(64);
        var codeChallenge = ComputeCodeChallenge(codeVerifier);

        // Cached under `state` so the callback (a separate HTTP request — the
        // browser navigated away to Spotify and back) can retrieve the
        // matching verifier. TTL bounds how long a stale/abandoned
        // authorization attempt lingers in memory.
        _cache.Set(CacheKey(state), codeVerifier, PkceStateTtl);

        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = codeChallenge,
            ["state"] = state,
            ["scope"] = Scopes
        };

        var url = AuthorizeEndpoint + "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return (url, state, codeVerifier);
    }

    public async Task ExchangeCodeAsync(string code, string state, string codeVerifier, Guid connectedByUserId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("SpotifyAuth");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = codeVerifier
        });

        var response = await client.PostAsync(TokenEndpoint, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Spotify token exchange failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException("Spotify authorization failed. Please try connecting again.");
        }

        var token = JsonSerializer.Deserialize<SpotifyTokenResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Spotify returned an unexpected token response.");

        var profile = await GetProfileAsync(token.AccessToken, ct);

        var existing = await _dbContext.PlatformSpotifyConnections
            .FirstOrDefaultAsync(c => c.Id == PlatformSpotifyConnection.SingletonId, ct);

        var now = DateTime.UtcNow;
        if (existing == null)
        {
            existing = new PlatformSpotifyConnection { Id = PlatformSpotifyConnection.SingletonId };
            _dbContext.PlatformSpotifyConnections.Add(existing);
            existing.ConnectedAt = now;
        }

        existing.SpotifyUserId = profile.Id;
        existing.DisplayName = profile.DisplayName ?? profile.Id;
        existing.AccessTokenProtected = _protector.Protect(token.AccessToken);
        existing.RefreshTokenProtected = _protector.Protect(token.RefreshToken ?? existing.RefreshTokenProtected);
        existing.AccessTokenExpiresAt = now.AddSeconds(token.ExpiresIn);
        existing.Scopes = token.Scope ?? Scopes;
        existing.ConnectedByUserId = connectedByUserId;
        existing.UpdatedAt = now;

        await _dbContext.SaveChangesAsync(ct);
        _cache.Remove(CacheKey(state));
    }

    public async Task<SpotifyConnectionStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var connection = await _dbContext.PlatformSpotifyConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == PlatformSpotifyConnection.SingletonId, ct);

        if (connection == null)
        {
            return new SpotifyConnectionStatusDto { IsConnected = false };
        }

        return new SpotifyConnectionStatusDto
        {
            IsConnected = true,
            SpotifyUserId = connection.SpotifyUserId,
            DisplayName = connection.DisplayName,
            ConnectedAt = connection.ConnectedAt
        };
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var connection = await _dbContext.PlatformSpotifyConnections
            .FirstOrDefaultAsync(c => c.Id == PlatformSpotifyConnection.SingletonId, ct);

        if (connection != null)
        {
            _dbContext.PlatformSpotifyConnections.Remove(connection);
            await _dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        var connection = await _dbContext.PlatformSpotifyConnections
            .FirstOrDefaultAsync(c => c.Id == PlatformSpotifyConnection.SingletonId, ct);

        if (connection == null)
            return null;

        if (connection.AccessTokenExpiresAt - TokenRefreshMargin > DateTime.UtcNow)
        {
            return _protector.Unprotect(connection.AccessTokenProtected);
        }

        // Expired or about to be — refresh. PKCE-established refresh tokens
        // don't require a client secret either, same as the initial exchange.
        var client = _httpClientFactory.CreateClient("SpotifyAuth");
        var refreshToken = _protector.Unprotect(connection.RefreshTokenProtected);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        });

        var response = await client.PostAsync(TokenEndpoint, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Spotify token refresh failed: {Status} {Body}", response.StatusCode, body);
            return null;
        }

        var token = JsonSerializer.Deserialize<SpotifyTokenResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Spotify returned an unexpected token response.");

        connection.AccessTokenProtected = _protector.Protect(token.AccessToken);
        if (!string.IsNullOrEmpty(token.RefreshToken))
        {
            // Spotify doesn't always rotate the refresh token — only overwrite when it does.
            connection.RefreshTokenProtected = _protector.Protect(token.RefreshToken);
        }
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
        connection.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        return token.AccessToken;
    }

    public async Task<List<SpotifyPlaylistDto>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        var accessToken = await GetValidAccessTokenAsync(ct);
        if (accessToken == null)
            return new List<SpotifyPlaylistDto>();

        var client = _httpClientFactory.CreateClient("SpotifyApi");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync($"{ApiBase}/me/playlists?limit=50", ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Spotify playlists fetch failed: {Status}", response.StatusCode);
            return new List<SpotifyPlaylistDto>();
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var page = JsonSerializer.Deserialize<SpotifyPlaylistsPage>(body, JsonOptions);

        return page?.Items?.Select(p => new SpotifyPlaylistDto
        {
            Id = p.Id,
            Name = p.Name,
            ImageUrl = p.Images?.FirstOrDefault()?.Url,
            TrackCount = p.Tracks?.Total ?? 0
        }).ToList() ?? new List<SpotifyPlaylistDto>();
    }

    private async Task<SpotifyProfile> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("SpotifyApi");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync($"{ApiBase}/me", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Spotify profile fetch failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException("Connected to Spotify but couldn't read the account profile.");
        }

        return JsonSerializer.Deserialize<SpotifyProfile>(body, JsonOptions)
            ?? throw new InvalidOperationException("Spotify returned an unexpected profile response.");
    }

    /// <summary>
    /// Looks up the code_verifier cached for a given state and consumes the
    /// cache entry's lookup key — call BEFORE ExchangeCodeAsync (which itself
    /// only clears the entry using the verifier value, not the state).
    /// </summary>
    public string? TryGetCodeVerifier(string state) => _cache.Get<string>(CacheKey(state));

    private static string CacheKey(string value) => $"spotify:pkce:{value}";

    private static string GenerateUrlSafeRandom(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private record SpotifyTokenResponse
    {
        public string AccessToken { get; init; } = string.Empty;
        public string? RefreshToken { get; init; }
        public int ExpiresIn { get; init; }
        public string? Scope { get; init; }
    }

    private record SpotifyProfile
    {
        public string Id { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
    }

    private record SpotifyPlaylistsPage
    {
        public List<SpotifyPlaylistItem>? Items { get; init; }
    }

    private record SpotifyPlaylistItem
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public List<SpotifyImage>? Images { get; init; }
        public SpotifyTracksInfo? Tracks { get; init; }
    }

    private record SpotifyImage
    {
        public string Url { get; init; } = string.Empty;
    }

    private record SpotifyTracksInfo
    {
        public int Total { get; init; }
    }
}
