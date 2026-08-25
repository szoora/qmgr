using System.Net;
using System.Net.Http.Headers;

namespace QMgr.Web.Services;

public class AuthenticationMessageHandler : DelegatingHandler
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthenticationMessageHandler> _logger;

    // Access tokens are short-lived (60 min, see JWT:ExpiryMinutes) while a Blazor Server
    // circuit can stay open far longer. Without this, every request made after the token
    // expires 401s with no recovery until the user manually logs out and back in. This
    // instance is scoped per-circuit (per AddScoped registration), so the lock only ever
    // coordinates concurrent requests from the same user, not across users.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Task<string?>? _inFlightRefresh;

    public AuthenticationMessageHandler(
        IAuthService authService,
        ILogger<AuthenticationMessageHandler> logger)
    {
        // IAuthService is safe to depend on directly here: it talks to the API through the
        // separate "QMgrAuthApi" client, which deliberately has no AuthenticationMessageHandler
        // attached, so there's no circular dependency back into this handler.
        _authService = authService;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Always resolve via GetAccessTokenAsync (checks in-memory storage, falling back to
        // localStorage) rather than reading ITokenStorageService directly. On a fresh Blazor
        // circuit (e.g. after a server restart, or simply a new tab) the scoped in-memory
        // token cache starts empty, and pages aren't consistently responsible for hydrating
        // it before their first API call — several call AppInitializationService.InitializeAsync()
        // to do so, several don't, and even where it's called there's no guarantee it finishes
        // before a child page's own OnInitializedAsync fires its request. Resolving the token
        // here, on every request, removes that race entirely instead of relying on callers to
        // have warmed the cache first.
        var token = await _authService.GetAccessTokenAsync();

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            _logger.LogDebug("No access token available for request: {Method} {Url}",
                request.Method, request.RequestUri);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // Always attempt a refresh on 401, even if we thought we had no token: a transient
        // localStorage/JS-interop hiccup right after login can make GetAccessTokenAsync return
        // empty for a moment even though a valid refresh token exists. RefreshTokenAsync() is a
        // cheap no-op (no network call) when there's genuinely no refresh token to use, so this
        // costs nothing for a truly logged-out caller.

        var newToken = await GetOrStartRefreshAsync();
        if (string.IsNullOrEmpty(newToken))
            return response;

        response.Dispose();

        var retryRequest = await CloneRequestAsync(request);
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private async Task<string?> GetOrStartRefreshAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            // A concurrent request may have already refreshed the token while we waited
            // for the lock — reuse that result instead of hitting the refresh endpoint twice.
            _inFlightRefresh ??= RefreshAsync();
            return await _inFlightRefresh;
        }
        finally
        {
            _inFlightRefresh = null;
            _refreshLock.Release();
        }
    }

    private async Task<string?> RefreshAsync()
    {
        _logger.LogInformation("Access token rejected (401); attempting silent refresh");
        return await _authService.RefreshTokenAsync();
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        if (original.Content != null)
        {
            var buffer = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(buffer);
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.Add(header.Key, header.Value);
        }

        foreach (var header in original.Headers)
        {
            if (header.Key == "Authorization") continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
