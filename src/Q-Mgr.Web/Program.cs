using System.Text.Json;
using System.Text.Json.Serialization;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR;
using QMgr.Web.Components;
using QMgr.Web.Components.Shared.UI;
using QMgr.Web.Services;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

// Configure JSON serialization options to match the API
var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};
jsonOptions.Converters.Add(new JsonStringEnumConverter());
builder.Services.AddSingleton(jsonOptions);

// Add Radzen services
builder.Services.AddRadzenComponents();

// Note: Web project is a UI client only - it calls API via HTTP
// DO NOT add Application/Infrastructure layers here as it causes conflicts
// with the API project (duplicate Mediator handlers, database connection conflicts)

// Add Blazor services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });

// Blazor Server's SignalR circuit defaults to a 32 KB max incoming message size,
// which silently truncates/fails MediaLibrary's InputFile uploads (images, video, etc.)
// for any file larger than a few KB, regardless of the app-level MaxFileSize check.
// Raise it to match the media library's upload size ceiling (see MediaLibrary.razor MaxFileSize).
builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 200 * 1024 * 1024; // 200 MB
});

// Add local storage
builder.Services.AddBlazoredLocalStorage();

// Add Q-Mgr UI component services

// Add authorization services
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// Add custom services
builder.Services.AddScoped<ITokenStorageService, TokenStorageService>();
builder.Services.AddScoped<IAppInitializationService, AppInitializationService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();
builder.Services.AddScoped<IQueueApiService, QueueApiService>();
builder.Services.AddScoped<IVisitorApiService, VisitorApiService>();
builder.Services.AddScoped<IMarketingApiService, MarketingApiService>();
builder.Services.AddScoped<IContentApiService, ContentApiService>();
builder.Services.AddScoped<ISpotifyApiService, SpotifyApiService>();
builder.Services.AddScoped<IOrganizationApiService, OrganizationApiService>();
builder.Services.AddScoped<ISignalRService, SignalRService>();
builder.Services.AddScoped<IBranchStateService, BranchStateService>();
builder.Services.AddScoped<IConnectionMonitorService, ConnectionMonitorService>();
builder.Services.AddScoped<INotificationClientService, NotificationClientService>();
builder.Services.AddScoped<INotificationApiService, NotificationApiService>();
builder.Services.AddScoped<IToastService, ToastService>();

// Add HTTP client for API calls
var isDevelopment = builder.Environment.IsDevelopment();

// HTTP client for authentication endpoints (no auth handler to avoid circular dependency)
builder.Services.AddHttpClient("QMgrAuthApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001");
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    if (isDevelopment)
    {
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }
    return handler;
});

// Authenticated HttpClient for API calls, scoped per Blazor Server circuit.
//
// Deliberately NOT built via builder.Services.AddHttpClient(...).AddHttpMessageHandler<T>():
// IHttpClientFactory pools the underlying HttpMessageHandler chain (including any
// AddHttpMessageHandler<T> delegating handlers) per client name, independent of which
// DI scope resolves it later, and rebuilds it only every HandlerLifetime (default 2 min).
// AuthenticationMessageHandler needs the CURRENT circuit's IAuthService/token state on
// every request — with factory pooling, whichever circuit happens to build the chain
// first "wins" it, and every other circuit's requests silently carry that first circuit's
// (often empty, pre-login) token instead of their own. Constructing the client directly
// here means AddScoped's factory lambda — which genuinely does run once per circuit,
// using that circuit's IServiceProvider — is the only thing that builds it.
builder.Services.AddScoped(sp =>
{
    var authService = sp.GetRequiredService<IAuthService>();
    var logger = sp.GetRequiredService<ILogger<AuthenticationMessageHandler>>();

    var innerHandler = new HttpClientHandler();
    if (isDevelopment)
    {
        innerHandler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }

    var authHandler = new AuthenticationMessageHandler(authService, logger)
    {
        InnerHandler = innerHandler
    };

    return new HttpClient(authHandler)
    {
        BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001")
    };
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
// Content-fingerprinted static assets (replaces UseStaticFiles): the served URL
// changes when a file's content changes, so browsers can cache aggressively and
// indefinitely without ever serving stale JS/CSS after a deploy. Concretely fixes
// a real repro this session: a long-lived browser tab kept serving a pre-upgrade
// cached copy of Radzen.Blazor.js after the Radzen.Blazor 8→11 package bump,
// causing RadzenDataGrid's JS interop calls to reference functions that no longer
// existed in the (stale, cached) JS — silently killing the Blazor circuit on any
// page with a data grid. UseStaticFiles has no such cache-busting by default.
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .WithStaticAssets();

app.Run();
