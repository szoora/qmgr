using System.Text.Json;
using System.Text.Json.Serialization;
using AspNetCoreRateLimit;
using Hangfire;
using Hangfire.PostgreSql;
using HealthChecks.NpgSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Extensions;
using QMgr.API.Hubs;
using QMgr.API.Middleware;
using QMgr.API.Services;
using QMgr.Application;
using QMgr.Application.Interfaces;
using QMgr.Hubs;
using QMgr.Infrastructure;
using QMgr.Infrastructure.Jobs;
using QMgr.Middleware;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/qmgr-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// SECURITY: Validate database connection string (relaxed for development)
var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
                      ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    // Only fail in production - allow empty in dev for easier setup
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Database connection string is not configured. " +
            "Set environment variable DB_CONNECTION_STRING. " +
            "Example: Host=localhost;Database=qmgr;Username=postgres;Password=your-strong-password");
    }
    else
    {
        Log.Warning("Database connection string is empty. Application may fail to connect to database.");
    }
}
else
{
    // Warn (don't block startup) on an obviously weak/default password in production. This used
    // to throw and hard-abort the app. Downgraded 2026-08-31 per explicit instruction: on this
    // deployment target, Postgres binds 127.0.0.1-only (confirmed via `ss -tlnp` — never reachable
    // over the network) and appsettings.Production.json is root/www-data-only on an access-
    // controlled VPS, so this substring check wasn't stopping a real attacker — anyone able to
    // read the password value already has server access, at which point the check just blocks a
    // legitimate deploy without adding real protection. Kept as a warning rather than deleted
    // outright so a genuinely weak password is still visible in logs, not silently invisible.
    if (!builder.Environment.IsDevelopment())
    {
        var lowerConnection = connectionString.ToLowerInvariant();
        if (lowerConnection.Contains("password=sav") ||
            lowerConnection.Contains("password=123") ||
            lowerConnection.Contains("password=password") ||
            lowerConnection.Contains("password=postgres"))
        {
            Log.Warning(
                "Database connection string contains a weak or default password. " +
                "Consider setting a strong password via DB_CONNECTION_STRING environment variable.");
        }
    }

    // Update configuration to use validated connection string
    builder.Configuration["ConnectionStrings:DefaultConnection"] = connectionString;
}

// Add services
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor(); // needed by LocalDiskMediaStorageService to build absolute file URLs

// Data Protection — encrypts secrets at rest (currently: the platform Spotify connection's
// OAuth tokens). Built into the ASP.NET Core shared framework, no extra package. Keys persist
// to a filesystem path so they survive process restarts; mount that path as a durable volume
// in production (same pattern as the media_uploads volume in docker-compose.yml) — without a
// persisted key ring, a container redeploy makes previously-encrypted tokens undecryptable and
// the platform Spotify connection would need to be reconnected.
builder.Services.AddDataProtection()
    .SetApplicationName("QMgr")
    .PersistKeysToFileSystem(new DirectoryInfo(
        builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(AppContext.BaseDirectory, "dataprotection-keys")));

// Add API services
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

// Add SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// Register SignalR hub services
builder.Services.AddScoped<IQueueHubService, QueueHubService>();
builder.Services.AddScoped<IQueueHubContext, QueueHubContext>();
builder.Services.AddScoped<INotificationHubService, NotificationHubService>();
builder.Services.AddSingleton<QMgr.API.Hubs.IDisplayHubContext, QMgr.API.Hubs.DisplayHubContext>();

// ASP.NET Core's form-reading middleware caps multipart bodies at 128MB by default,
// independent of Kestrel's own request size limit — raised to match ContentController's
// media upload endpoint (200MB, [RequestSizeLimit] on that action handles the Kestrel side).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 200 * 1024 * 1024;
});

// Register Print service
builder.Services.AddScoped<QMgr.API.Services.Printing.IPrintService, QMgr.API.Services.Printing.PrintService>();

// Register platform configuration and security services
builder.Services.AddScoped<QMgr.API.Application.Services.IPlatformConfigurationService, QMgr.API.Application.Services.PlatformConfigurationService>();
builder.Services.AddScoped<QMgr.API.Application.Services.IPasswordValidationService, QMgr.API.Application.Services.PasswordValidationService>();

// Add JWT Authentication
builder.Services.AddJwtAuthentication(builder.Configuration);

// Add RBAC Authorization with permission-based policies
builder.Services.AddRbacAuthorization();
builder.Services.AddRbacPolicyProvider();

// Add CORS - configured for SignalR which requires credentials.
// Origins are the UNION of appsettings/environment ("Cors:AllowedOrigins") and the Platform
// Settings "CORS" row edited in the admin UI, read once here at startup (same pattern as rate
// limiting). A union — not DB-wins — so a freshly seeded CORS row (localhost defaults) can never
// knock out the production origin the deploy script bakes into appsettings.Production.json.
var corsFromDb = QMgr.API.Extensions.ServiceExtensions.TryLoadCorsSettingsFromDatabase(connectionString);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowWebUI", policy =>
    {
        var configured = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? new[] { "https://localhost:5002", "http://localhost:5003" };
        var origins = configured
            .Concat(corsFromDb?.AllowedOrigins ?? new List<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        policy.WithOrigins(origins)
              .AllowAnyMethod()
              .AllowAnyHeader();
        if (corsFromDb?.AllowCredentials ?? true)
        {
            policy.AllowCredentials(); // Required for SignalR
        }
    });
});

// Add Rate Limiting
builder.Services.AddMemoryCache();
builder.Services.AddRateLimiting(builder.Configuration, connectionString);

// Add Health Checks
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!);

// Add Hangfire for background job processing
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options =>
        options.UseNpgsqlConnection(builder.Configuration.GetConnectionString("DefaultConnection")!)));

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = Environment.ProcessorCount * 2;
    options.Queues = new[] { "default", "billing", "notifications" };
});

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Scalar API Documentation - Modern API reference. Available in every environment (not just
// Development) since real customers/integrators are meant to reach this in production, gated by
// login - see the .RequireAuthorization() calls below.
//
// BUG FIX (path/host): this used to map at Scalar's own default "/scalar" prefix while every
// Web-app link pointed at a relative "/api/docs" - broken on two counts, since that path resolves
// against the Web app's own origin (a different process/port entirely), not the API's, and even
// on the API host "/api/docs" was never actually mapped to anything. Mapping it here at the exact
// path the Web app links to (combined with making those links absolute against the API's real
// *public* origin, not its internal loopback address - see MainLayout.razor/appsettings'
// ApiPublicUrl) closes both gaps.
//
// SECURITY: gated to authenticated users, per explicit request - this was previously wide open to
// anyone who found the link. A plain browser navigation to these routes carries no Authorization
// header (this app's auth is JWT-in-localStorage, not cookies), so the caller passes the JWT as
// an ?access_token= query parameter - the exact same mechanism already used for the SignalR hub
// negotiation, extended in AddJwtAuthentication (ServiceExtensions.cs) to also cover these two
// route prefixes. The per-HttpContext options overload below reads that same query token back out
// and bakes it into the OpenApiRoutePattern Scalar's own page embeds, so the browser's *follow-up*
// fetch of the OpenAPI document (a separate request Scalar's JS makes after the page loads) is
// authenticated too - without this, the page would load but the actual API reference content
// would fail to fetch.
app.MapOpenApi().RequireAuthorization();
app.MapScalarApiReference("/api/docs", (options, httpContext) =>
{
    var token = httpContext.Request.Query["access_token"].ToString();
    if (!string.IsNullOrEmpty(token))
    {
        options.OpenApiRoutePattern = $"/openapi/v1.json?access_token={Uri.EscapeDataString(token)}";
    }
}).RequireAuthorization();

app.UseHttpsRedirection();
app.UseSerilogRequestLogging();
app.UseMiddleware<QMgr.API.Middleware.RequestMetricsMiddleware>();

// Serves uploaded media (wwwroot/uploads/media/...) written by ContentController's
// UploadMediaContent action. Public/unauthenticated by design, same as the existing
// [AllowAnonymous] media-read endpoints — display/kiosk screens need to load this
// content without a session.
app.UseStaticFiles();

app.UseCors("AllowWebUI");

// Rate limiting was registered via AddRateLimiting() but the middleware
// itself was never added to the pipeline, meaning it did nothing — this is
// what actually enforces it. Placed early so it protects login/auth
// endpoints against brute force before any real work happens.
app.UseIpRateLimiting();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

app.UseAuthentication();

// Tenant resolution - must be after authentication to access JWT claims
app.UseTenantResolution();

// Check tenant status (suspended, cancelled, etc.)
app.UseTenantStatus();

// Enforce usage limits based on subscription plan
app.UseUsageLimits();

app.UseAuthorization();

app.MapControllers();
app.MapHub<QueueHub>("/hubs/queue");
app.MapHub<DisplayHub>("/hubs/display");
app.MapHub<NotificationHub>("/hubs/notifications");

// Health check endpoint (unauthenticated for development - secure in production)
app.MapHealthChecks("/health");

// Initialize database BEFORE Hangfire tries to connect
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<QMgr.Infrastructure.Data.QMgrDbContext>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    // Initialize database (checks existence, creates if needed, runs migrations)
    var initializer = new QMgr.Infrastructure.Data.DatabaseInitializer(
        db,
        configuration,
        scope.ServiceProvider.GetRequiredService<ILogger<QMgr.Infrastructure.Data.DatabaseInitializer>>());
    await initializer.InitializeAsync();

    // Seed RBAC data (permissions, roles, role-permission mappings)
    // This runs in all environments to ensure roles/permissions are always available
    var rbacSeeder = new QMgr.Infrastructure.Data.RbacSeeder(
        db,
        scope.ServiceProvider.GetRequiredService<ILogger<QMgr.Infrastructure.Data.RbacSeeder>>());
    await rbacSeeder.SeedAsync();

    // Seed demo data (development only)
    if (app.Environment.IsDevelopment())
    {
        var seeder = new QMgr.Infrastructure.Data.DbSeeder(
            db,
            scope.ServiceProvider.GetRequiredService<ILogger<QMgr.Infrastructure.Data.DbSeeder>>());
        await seeder.SeedAsync();
    }

    // Initialize platform settings (from appsettings.json to database)
    var platformSettingsService = scope.ServiceProvider.GetRequiredService<QMgr.Application.Interfaces.IPlatformSettingsService>();
    await platformSettingsService.InitializeDefaultSettingsAsync();
}

// Hangfire Dashboard (protected)
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthorizationFilter(app.Environment.IsDevelopment()) },
    DashboardTitle = "Q-Mgr Background Jobs"
});

// Register recurring billing jobs (AFTER database is initialized)
BillingJobsRegistration.RegisterRecurringJobs();
RateLimitJobsRegistration.RegisterRecurringJobs();
WebhookJobsRegistration.RegisterRecurringJobs();
BroadcastJobsRegistration.RegisterRecurringJobs();
VisitorRetentionJobsRegistration.RegisterRecurringJobs();
WelfareReminderJobRegistration.RegisterRecurringJobs();

app.Run();
