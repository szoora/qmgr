using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using QMgr.API.Authorization;
using QMgr.Domain.Entities.Integration;
using QMgr.Domain.Interfaces;

namespace QMgr.API.Middleware;

/// <summary>
/// Middleware for API key authentication (alternative to JWT for simple integrations).
///
/// Header contract (hardened 2026-09-03 — previously the bare client id was a full credential,
/// the BCrypt <see cref="ApiClient.ClientSecretHash"/> was never checked on this path):
/// <list type="bullet">
///   <item><c>X-API-Key: &lt;clientId&gt;</c> + <c>X-API-Secret: &lt;clientSecret&gt;</c>, or</item>
///   <item><c>X-API-Key: &lt;clientId&gt;.&lt;clientSecret&gt;</c> (split on the FIRST '.'; client ids are
///   <c>qmgr_</c> + 16 hex chars and secrets are <c>qmgr_sk_</c> + 48 hex chars, so neither can
///   contain a '.').</item>
/// </list>
/// Successful secret verifications are cached for 10 minutes (BCrypt is deliberately slow); the
/// cache key includes the stored hash, so a regenerated secret invalidates old entries immediately.
/// Wrong secrets are never cached. <see cref="ApiClient.RateLimitPerMinute"/> is enforced here as a
/// fixed one-minute window per client, and API-key principals are only admitted to endpoints that
/// declare a <see cref="RequirePermissionAttribute"/> (or are <c>[AllowAnonymous]</c>) — an action
/// with only a class-level <c>[Authorize]</c> is otherwise reachable by any valid key.
/// </summary>
public class ApiKeyAuthenticationMiddleware
{
    public const string ApiKeyHeader = "X-API-Key";
    public const string ApiSecretHeader = "X-API-Secret";
    public const char CombinedSeparator = '.';

    private static readonly TimeSpan VerifiedSecretCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LastUsedStampInterval = TimeSpan.FromMinutes(5);

    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;

    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IUnitOfWork unitOfWork)
    {
        // Skip if already authenticated via JWT
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // Check for API key header
        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var apiKeyHeader))
        {
            await _next(context);
            return;
        }

        var rawKey = apiKeyHeader.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(rawKey))
        {
            await _next(context);
            return;
        }

        // Resolve client id + secret from either the combined form or the two-header form.
        string clientId;
        string? secret;
        var separatorIndex = rawKey.IndexOf(CombinedSeparator);
        if (separatorIndex > 0)
        {
            clientId = rawKey[..separatorIndex];
            secret = rawKey[(separatorIndex + 1)..];
        }
        else
        {
            clientId = rawKey;
            secret = context.Request.Headers.TryGetValue(ApiSecretHeader, out var secretHeader)
                ? secretHeader.FirstOrDefault()?.Trim()
                : null;
        }

        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogWarning("API key request without a secret for client {ClientId}", Redact(clientId));
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "missing_api_secret",
                error_description = "An API secret is required. Send either 'X-API-Key: <clientId>' together with " +
                                    "'X-API-Secret: <clientSecret>', or a single 'X-API-Key: <clientId>.<clientSecret>' header.",
                headers = new
                {
                    twoHeader = new { X_API_Key = "<clientId>", X_API_Secret = "<clientSecret>" },
                    combined = new { X_API_Key = "<clientId>.<clientSecret>" }
                }
            });
            return;
        }

        // Validate API key
        var client = await unitOfWork.ApiClients.FirstOrDefaultAsync(
            c => c.ClientId == clientId && c.IsActive);

        // Unknown client id and wrong secret produce the identical response on purpose — no oracle
        // for "this client id exists".
        if (client == null || string.IsNullOrEmpty(client.ClientSecretHash) || !VerifySecret(client, secret))
        {
            _logger.LogWarning("Invalid API key attempt: {ApiKey}", Redact(clientId));
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_api_key",
                error_description = "The provided API key is invalid or inactive"
            });
            return;
        }

        // Per-client rate limit (fixed one-minute window). Counted only for authenticated requests so
        // that knowing a client id alone can't exhaust that client's quota.
        if (client.RateLimitPerMinute > 0 && IsRateLimited(client, out var retryAfterSeconds))
        {
            _logger.LogWarning("API client {ClientId} exceeded its rate limit of {Limit}/min", client.ClientId, client.RateLimitPerMinute);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
            await context.Response.WriteAsJsonAsync(new
            {
                error = "RATE_LIMITED",
                limit = client.RateLimitPerMinute,
                retryAfterSeconds
            });
            return;
        }

        // Stamp LastUsedAt at most once every 5 minutes instead of a DB write on every request.
        var now = DateTime.UtcNow;
        if (client.LastUsedAt == null || now - client.LastUsedAt.Value > LastUsedStampInterval)
        {
            try
            {
                client.LastUsedAt = now;
                await unitOfWork.ApiClients.UpdateAsync(client);
                await unitOfWork.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Usage bookkeeping must never fail an otherwise-valid request.
                _logger.LogWarning(ex, "Failed to stamp LastUsedAt for API client {ClientId}", client.ClientId);
            }
        }

        // Add claims to context for authorization
        var claims = new List<Claim>
        {
            new("client_id", client.ClientId),
            new("org_id", client.OrganizationId.ToString()),
            new("auth_method", "api_key")
        };

        if (client.Scopes != null)
        {
            foreach (var scope in client.Scopes)
            {
                claims.Add(new Claim("scope", scope));
            }
        }

        var identity = new ClaimsIdentity(claims, "ApiKey");
        context.User = new ClaimsPrincipal(identity);

        // Endpoint gate: API-key principals may only reach endpoints that are explicitly
        // permission-guarded (so the scope → permission mapping actually applies) or that are
        // anonymous anyway. Routing has already run here — Program.cs never calls UseRouting()
        // itself, so WebApplication inserts it at the very front of the pipeline — which is what
        // makes GetEndpoint() reliable in this middleware. Endpoints carrying no authorization
        // metadata at all (e.g. /health; FallbackPolicy is null) are treated like [AllowAnonymous].
        var endpoint = context.GetEndpoint();
        if (endpoint != null)
        {
            var metadata = endpoint.Metadata;
            var isAnonymous = metadata.GetMetadata<IAllowAnonymous>() != null
                              || metadata.GetMetadata<IAuthorizeData>() == null;
            var isPermissionGuarded = metadata.GetMetadata<RequirePermissionAttribute>() != null;

            if (!isAnonymous && !isPermissionGuarded)
            {
                _logger.LogWarning("API client {ClientId} denied on {Method} {Path}: endpoint is not permission-guarded",
                    client.ClientId, context.Request.Method, context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "API_KEY_NOT_ALLOWED",
                    message = "API keys can only call endpoints that are guarded by an explicit permission " +
                              "(and therefore by an API-key scope). This endpoint is available to signed-in users only."
                });
                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// BCrypt-verifies the presented secret, caching a positive result for 10 minutes. The cache key
    /// is derived from the client id, the SHA-256 of the presented secret, and the stored hash — so a
    /// regenerated secret (new hash) can never be satisfied by a previously-cached verification.
    /// Negative results are deliberately not cached.
    /// </summary>
    private bool VerifySecret(ApiClient client, string secret)
    {
        var cacheKey = $"apikey-auth:{client.ClientId}:{Sha256Hex(secret + "\n" + client.ClientSecretHash)}";
        if (_cache.TryGetValue(cacheKey, out bool verified) && verified)
        {
            return true;
        }

        bool ok;
        try
        {
            ok = BCrypt.Net.BCrypt.Verify(secret, client.ClientSecretHash);
        }
        catch (Exception ex)
        {
            // A malformed stored hash counts as "wrong secret", never as an exception to the caller.
            _logger.LogError(ex, "BCrypt verification failed for API client {ClientId}", client.ClientId);
            ok = false;
        }

        if (ok)
        {
            _cache.Set(cacheKey, true, VerifiedSecretCacheDuration);
        }

        return ok;
    }

    /// <summary>
    /// Fixed-window counter keyed <c>apikey-rl:{clientId}:{yyyyMMddHHmm}</c>. Returns true when this
    /// request pushes the client past <see cref="ApiClient.RateLimitPerMinute"/>.
    /// </summary>
    private bool IsRateLimited(ApiClient client, out int retryAfterSeconds)
    {
        var now = DateTime.UtcNow;
        var windowStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        var windowEnd = windowStart.AddMinutes(1);
        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((windowEnd - now).TotalSeconds));

        var cacheKey = $"apikey-rl:{client.ClientId}:{windowStart:yyyyMMddHHmm}";
        var counter = _cache.GetOrCreate(cacheKey, entry =>
        {
            // Keep the entry a little past the window so a late straggler can't restart the count.
            entry.AbsoluteExpiration = new DateTimeOffset(windowEnd.AddSeconds(5));
            return new RateCounter();
        })!;

        int count;
        lock (counter)
        {
            count = ++counter.Count;
        }

        return count > client.RateLimitPerMinute;
    }

    private sealed class RateCounter
    {
        public int Count;
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Redact(string value)
        => value.Length <= 8 ? value : value[..8] + "...";
}
