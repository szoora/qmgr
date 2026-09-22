using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
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

// Data Protection. The Web app had NO AddDataProtection call at all until 2026-09-10, so it fell
// back to an ephemeral key ring — regenerated on every process start, meaning antiforgery tokens
// (and anything else protected here) stopped validating after any restart or redeploy.
//
// Same bug class as the one that made every production walk-in check-in return 500: the deployed
// unit runs with ProtectSystem=strict + ProtectHome=true, which mounts everything outside
// ReadWritePaths read-only, and the framework's default key location is AppContext.BaseDirectory —
// inside the install root. The difference is only that this one FAILS SOFT: ASP.NET Core warns and
// carries on with in-memory keys rather than throwing, which is exactly why it never announced
// itself.
//
// The path is the SAME one the API persists to, deliberately: build-linux.ps1 creates it once at
// /var/lib/qmgr/dataprotection-keys, chowns it, and lists it in both units' ReadWritePaths. It sits
// outside $InstallRoot so a deploy does not replace it. SetApplicationName must match the API's
// ("QMgr") — the application name is part of the key-derivation purpose chain, so two processes
// sharing a ring but disagreeing on the name cannot read each other's payloads.
builder.Services.AddDataProtection()
    .SetApplicationName("QMgr")
    .PersistKeysToFileSystem(new DirectoryInfo(
        builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(AppContext.BaseDirectory, "dataprotection-keys")));

// Note: Web project is a UI client only - it calls API via HTTP
// DO NOT add Application/Infrastructure layers here as it causes conflicts
// with the API project (duplicate Mediator handlers, database connection conflicts)

// Localization for the customer-facing screens (kiosk, display, join, ticket status, feedback).
// Resource keys are the English text, so anything untranslated renders as readable English instead
// of a key name. See QMgr.Web.Resources.SharedResources.
//
// NO ResourcesPath, AND THAT IS THE WHOLE FIX. It read `options.ResourcesPath = "Resources"` from
// the day this was written, and every lookup in every language silently returned English.
// ResourceManagerStringLocalizerFactory builds the resource prefix as
//     <root namespace> + "." + <ResourcesPath> + "." + <type name with the root namespace trimmed>
// and the root namespace it uses is the ASSEMBLY name, "Q-Mgr.Web" - which is not a prefix of
// "QMgr.Web.Resources.SharedResources" (the hyphen), so nothing is trimmed and it looked for
// "Q-Mgr.Web.Resources.QMgr.Web.Resources.SharedResources". The satellite assemblies really do
// carry "QMgr.Web.Resources.SharedResources.lg.resources", so the name simply never matched.
//
// With no ResourcesPath the prefix is the type's own FullName, which is exactly that name: the
// marker class already lives in the Resources folder, so the path is in the namespace and adding
// it again is what broke it. Do not put it back to "tidy" the call.
//
// NOTHING ANYWHERE WOULD HAVE TOLD YOU. IStringLocalizer never throws on a missing resource - it
// returns the key, which here IS the English text, so a completely broken lookup and a correct
// English render are byte-identical. scripts/e2e/browser/localization.mjs is the only thing that
// can tell them apart, because it asserts a Luganda string actually appears.
builder.Services.AddLocalization();

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
// The browser's address and agent for this circuit, relayed on every API call (see the class).
builder.Services.AddScoped<ViewerRequestContext>();
builder.Services.AddScoped<IAppInitializationService, AppInitializationService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();
builder.Services.AddScoped<IDataExportService, DataExportService>();
builder.Services.AddScoped<IBatchApiService, BatchApiService>();
builder.Services.AddScoped<IQueueApiService, QueueApiService>();
builder.Services.AddScoped<IVisitorApiService, VisitorApiService>();
builder.Services.AddScoped<IReportsApiService, ReportsApiService>();
builder.Services.AddScoped<IAppointmentApiService, AppointmentApiService>();
builder.Services.AddScoped<IStudentApiService, StudentApiService>();
builder.Services.AddScoped<IClassTeacherApiService, ClassTeacherApiService>();
builder.Services.AddScoped<IStaffPerformanceApiService, StaffPerformanceApiService>();
builder.Services.AddScoped<IStaffOnboardingApiService, StaffOnboardingApiService>();
builder.Services.AddScoped<IModuleApiService, ModuleApiService>();
builder.Services.AddScoped<IPaymentApiService, PaymentApiService>();
builder.Services.AddScoped<ISelfServiceApiService, SelfServiceApiService>();
builder.Services.AddScoped<IMarketingApiService, MarketingApiService>();
builder.Services.AddScoped<IContentApiService, ContentApiService>();
builder.Services.AddScoped<IDocumentShareApiService, DocumentShareApiService>();
builder.Services.AddScoped<ISpotifyApiService, SpotifyApiService>();
builder.Services.AddScoped<IOrganizationApiService, OrganizationApiService>();
builder.Services.AddScoped<ISignalRService, SignalRService>();
builder.Services.AddScoped<IBranchStateService, BranchStateService>();
builder.Services.AddScoped<IModuleStateService, ModuleStateService>();
builder.Services.AddScoped<IConnectionMonitorService, ConnectionMonitorService>();
builder.Services.AddScoped<INotificationClientService, NotificationClientService>();
builder.Services.AddScoped<INotificationApiService, NotificationApiService>();
// What this deployment is called, for the tab's title and icon — the one thing a component
// cannot reach on its own. Scoped: it belongs to the circuit, like the session it describes.
builder.Services.AddScoped<IBrandContext, BrandContext>();
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

    var authHandler = new AuthenticationMessageHandler(authService, sp.GetRequiredService<ViewerRequestContext>(), logger)
    {
        InnerHandler = innerHandler
    };

    // Outermost: sees the final response after any 401-triggered refresh/retry inside
    // AuthenticationMessageHandler has already happened, so a 403 here is a real tenant-status
    // or permission decision, not a stale-token artifact.
    var tenantStatusHandler = new TenantStatusMessageHandler(
        sp.GetRequiredService<NavigationManager>(),
        sp.GetRequiredService<ILogger<TenantStatusMessageHandler>>())
    {
        InnerHandler = authHandler
    };

    return new HttpClient(tenantStatusHandler)
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

// Culture comes from the cookie the picker below writes; without this the request-localization
// middleware would leave every circuit on the server's own culture.
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(QMgr.Web.Services.SupportedCultures.Codes[0])
    .AddSupportedCultures(QMgr.Web.Services.SupportedCultures.Codes)
    .AddSupportedUICultures(QMgr.Web.Services.SupportedCultures.Codes);
localizationOptions.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider
{
    CookieName = QMgr.Web.Services.SupportedCultures.CookieName
});
app.UseRequestLocalization(localizationOptions);

app.UseAntiforgery();

// Changing language in Blazor Server needs a real HTTP round trip: the already-rendered markup and
// the localization middleware both have to agree, so the choice is stored in a cookie here and the
// browser is redirected, starting a fresh circuit in the chosen language.
app.MapGet("/culture/set", (HttpContext http, string culture, string? redirectUri) =>
{
    if (!QMgr.Web.Services.SupportedCultures.IsSupported(culture))
    {
        return Results.BadRequest("Unsupported language.");
    }

    http.Response.Cookies.Append(
        QMgr.Web.Services.SupportedCultures.CookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, Path = "/" });

    // Only ever return to a path on this site: redirectUri arrives from the query string, so
    // accepting an absolute URL here would make this an open redirect.
    var target = redirectUri;
    if (string.IsNullOrWhiteSpace(target) || !target.StartsWith('/') || target.StartsWith("//"))
    {
        target = "/";
    }

    return Results.LocalRedirect(target);
});

// The PWA manifest for a tenant's OWN host (white-label plan, C3). An installed app on a member
// of staff's phone carries their school's name, icon and colour rather than ours — an installed
// icon reading "Q-Mgr" on a white-labelled deployment is the most visible place the white-labelling
// could leak, and it is the one place a user keeps looking at when the browser is shut.
//
// App.razor only links this on a host that actually resolves to a tenant; on the platform host the
// static wwwroot/manifest.json is linked instead. This endpoint re-checks anyway and falls back to
// the platform manifest for an unknown host — a link is not a permission.
app.MapGet("/app-manifest.json", async (HttpContext http, QMgr.Web.Services.IOrganizationApiService organizations) =>
{
    var branding = await organizations.GetBrandingForHostAsync(http.Request.Host.Host);

    var name = branding.Resolved && !string.IsNullOrWhiteSpace(branding.BrandName) ? branding.BrandName! : "Q-Mgr";
    var theme = branding.Resolved && !string.IsNullOrWhiteSpace(branding.PrimaryColor) ? branding.PrimaryColor! : "#8c2f52";
    // A tenant's uploaded logo, else ours. Purpose "any" only: a maskable icon has to carry its own
    // safe zone and we cannot know that an uploaded logo does — declaring it maskable would let
    // Android crop the school's name off its own icon.
    var icon = branding.Resolved && !string.IsNullOrWhiteSpace(branding.LogoUrl) ? branding.LogoUrl! : "/images/icon-512.svg";
    var iconType = icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml"
        : icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
        : icon.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
        : "image/jpeg";

    var manifest = new Dictionary<string, object?>
    {
        ["name"] = name,
        ["short_name"] = name.Length > 12 ? name[..12].TrimEnd() : name,
        ["description"] = $"{name} — front office: queues, visitors, signage, welfare and secure documents.",
        ["start_url"] = "/",
        ["display"] = "standalone",
        ["background_color"] = "#0d1117",
        ["theme_color"] = theme,
        ["orientation"] = "any",
        ["scope"] = "/",
        ["lang"] = "en",
        ["icons"] = new[]
        {
            new Dictionary<string, object?> { ["src"] = icon, ["sizes"] = "any", ["type"] = iconType, ["purpose"] = "any" }
        }
    };

    // Never cached at the edge: the manifest depends on the Host header, and a shared cache that
    // ignored that would hand one school's name to another.
    http.Response.Headers.CacheControl = "private, max-age=300";
    return Results.Json(manifest, contentType: "application/manifest+json");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .WithStaticAssets();

app.Run();
