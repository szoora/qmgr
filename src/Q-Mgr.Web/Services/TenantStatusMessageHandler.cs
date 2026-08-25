using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace QMgr.Web.Services;

/// <summary>
/// Watches every authenticated API response for the { error: "ACCOUNT_SUSPENDED"/"ACCOUNT_
/// CANCELLED"/"ACCOUNT_PENDING"/"ACCOUNT_DELETED", message, actionUrl } 403 shape
/// TenantStatusMiddleware returns for a non-active tenant, and redirects to a real status page
/// instead of leaving the caller to fail silently.
///
/// Before this existed, a suspended tenant's every data call 403'd with no page anywhere
/// reading the response — found live: Dashboard's empty-state onboarding screen ("Welcome to
/// Q-Mgr! Let's set up your first branch") silently took over because the branches call it
/// depends on 403'd, which looks identical to "no data yet" from the component's point of view.
/// The backend already built a well-formed error response; nothing on this side consumed it.
/// </summary>
public class TenantStatusMessageHandler : DelegatingHandler
{
    private static readonly HashSet<string> TenantStatusErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCOUNT_PENDING",
        "ACCOUNT_SUSPENDED",
        "ACCOUNT_CANCELLED",
        "ACCOUNT_DELETED"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly NavigationManager _navigation;
    private readonly ILogger<TenantStatusMessageHandler> _logger;

    // A Dashboard-style page load fires many concurrent requests, so several can hit a 403 at
    // once before NavigateTo(forceLoad: true) — which is fire-and-forget, it doesn't block until
    // the browser has actually left the page — has taken effect. Without this guard, each of
    // those requests independently sees "not yet on account-status" and calls NavigateTo again;
    // the resulting competing full-page navigations can interrupt each other mid-flight, leaving
    // the browser on a circuit that never finishes connecting (rendered content visible, but
    // @onclick handlers dead — confirmed live: the page displayed correctly but every button
    // press did nothing). One instance of this handler = one circuit (AddScoped), so this flag
    // safely limits it to firing once per circuit's redirect.
    private bool _redirecting;

    public TenantStatusMessageHandler(NavigationManager navigation, ILogger<TenantStatusMessageHandler> logger)
    {
        _navigation = navigation;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Forbidden)
            return response;

        // Already on the status page, or already redirecting there — don't re-navigate on every
        // background poll's 403 or on concurrent in-flight requests (see _redirecting above).
        if (_redirecting || _navigation.Uri.Contains("/account-status", StringComparison.OrdinalIgnoreCase))
            return response;

        // Reading the body here consumes the original content stream, so it's restored as a
        // fresh StringContent below regardless of outcome — otherwise every ordinary 403
        // (permission denied on a specific endpoint, not a tenant-status gate) would arrive at
        // its caller with an already-drained, unreadable body.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Content = new StringContent(body);
        if (response.Content.Headers.ContentType == null)
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<TenantStatusError>(body, JsonOptions);

            if (parsed?.Error != null && TenantStatusErrorCodes.Contains(parsed.Error))
            {
                _redirecting = true;
                _logger.LogInformation("Tenant status gate hit ({Code}); redirecting to account-status page", parsed.Error);

                var url = $"/account-status?code={Uri.EscapeDataString(parsed.Error)}" +
                           $"&message={Uri.EscapeDataString(parsed.Message ?? "")}" +
                           (string.IsNullOrEmpty(parsed.ActionUrl) ? "" : $"&actionUrl={Uri.EscapeDataString(parsed.ActionUrl)}");

                _navigation.NavigateTo(url, forceLoad: true);
            }
        }
        catch (JsonException)
        {
            // Not the tenant-status shape — an ordinary 403 (permission denied on a specific
            // endpoint), which callers already handle on their own. Nothing to do here.
        }

        return response;
    }

    private record TenantStatusError
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
        public string? ActionUrl { get; init; }
    }
}
