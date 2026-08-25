using QMgr.Domain.Interfaces;

namespace QMgr.API.Middleware;

/// <summary>
/// Middleware for API key authentication (alternative to JWT for simple integrations)
/// </summary>
public class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;

    public ApiKeyAuthenticationMiddleware(RequestDelegate next, ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
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
        if (!context.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader))
        {
            await _next(context);
            return;
        }

        var apiKey = apiKeyHeader.FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
        {
            await _next(context);
            return;
        }

        // Validate API key
        var client = await unitOfWork.ApiClients.FirstOrDefaultAsync(
            c => c.ClientId == apiKey && c.IsActive);

        if (client == null)
        {
            _logger.LogWarning("Invalid API key attempt: {ApiKey}", apiKey[..Math.Min(8, apiKey.Length)] + "...");
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_api_key",
                error_description = "The provided API key is invalid or inactive"
            });
            return;
        }

        // Check rate limiting
        // (Rate limiting is handled separately, but we track usage here)
        client.LastUsedAt = DateTime.UtcNow;
        await unitOfWork.SaveChangesAsync();

        // Add claims to context for authorization
        var claims = new List<System.Security.Claims.Claim>
        {
            new("client_id", client.ClientId),
            new("org_id", client.OrganizationId.ToString()),
            new("auth_method", "api_key")
        };

        if (client.Scopes != null)
        {
            foreach (var scope in client.Scopes)
            {
                claims.Add(new System.Security.Claims.Claim("scope", scope));
            }
        }

        var identity = new System.Security.Claims.ClaimsIdentity(claims, "ApiKey");
        context.User = new System.Security.Claims.ClaimsPrincipal(identity);

        await _next(context);
    }
}
