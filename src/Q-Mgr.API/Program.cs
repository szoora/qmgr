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
using QMgr.API.Authorization;
using QMgr.API.Services;
using QMgr.Application;
using QMgr.Application.Interfaces;
using QMgr.Hubs;
using QMgr.Infrastructure;
using QMgr.Infrastructure.Jobs;
using QMgr.Domain.Constants;
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
var keyRingPath = builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(AppContext.BaseDirectory, "dataprotection-keys");
builder.Services.AddDataProtection()
    .SetApplicationName("QMgr")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

// Say so at startup if the key ring cannot be written, rather than letting the first Protect()
// call — a visitor check-in, an upload link, a share token — be the thing that finds out with a
// 500. Seen live on 2026-09-15: DataProtection:KeyPath had been written into the generated
// appsettings.Production.json, which install.sh preserves from the server's own copy, so the
// live API never received it and persisted keys under the read-only install root. The path now
// also travels as Environment=DataProtection__KeyPath in both systemd units. Logged, not fatal:
// a loud, specific error beats an API that refuses to start.
try
{
    Directory.CreateDirectory(keyRingPath);
    var probe = Path.Combine(keyRingPath, ".write-probe");
    File.WriteAllText(probe, DateTime.UtcNow.ToString("O"));
    File.Delete(probe);
}
catch (Exception ex)
{
    Log.Error(ex,
        "The Data Protection key ring at {KeyRingPath} is NOT writable. Every IDataProtector.Protect call (visitor badges, upload links, share tokens) will fail with a 500 until it is. " +
        "Set DataProtection__KeyPath in the service unit to a writable directory listed in ReadWritePaths (the deploy script uses /var/lib/qmgr/dataprotection-keys).",
        keyRingPath);
}

// The same shape of check for media storage. Flipping MediaStorage:Provider to S3 wires
// S3MediaStorageService for UPLOADS, but UploadsController still serves every byte with
// PhysicalFile off the local store — it needs a disk path for the existence check, the ETag and
// the range processing pdf.js and <video> depend on. So an S3 install uploads successfully and
// then 404s every welfare photograph, every visitor badge and every signage image, with nothing
// anywhere saying why. Serving from a bucket is real work that has never been run against one,
// and shipping it unexercised would be worse than saying so; until then this is the warning.
// Logged, not fatal, the same call as the key ring above.
var storageProvider = builder.Configuration["MediaStorage:Provider"];
if (!string.IsNullOrWhiteSpace(storageProvider) && !storageProvider.Equals("Local", StringComparison.OrdinalIgnoreCase) && !storageProvider.Equals("LocalDisk", StringComparison.OrdinalIgnoreCase))
{
    Log.Error(
        "MediaStorage:Provider is {Provider}, so uploads are written to that provider — but UploadsController serves every file from the LOCAL store and will answer 404 for all of them. " +
        "Uploads will appear to succeed and every image, attachment and badge will then be missing. Set MediaStorage:Provider back to Local until UploadsController can stream from the provider.",
        storageProvider);
}

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

// Scalar API Documentation. Mapped in every environment, not just Development, but **PLATFORM
// ADMINS ONLY** (user decision, 2026-09-18: "the api documentation should be only accessible to
// platform admins. it is a platform gated feature. user has nothing to do with it").
//
// It used to be .RequireAuthorization() — ANY authenticated user. That is not a gate worth the
// name here: a class teacher, a front-desk operator or a viewer could read the complete endpoint
// inventory of the whole product, platform and admin routes included, across every tenant. The
// three links that offered it (the user menu beside My Profile, the footer on every page, and the
// Support page's integrations tile) were themselves ungated, so it was not merely reachable — it
// was advertised.
//
// The policy is platform.admin, which no tenant role can hold: RbacSeeder excludes platform
// permissions from tenant roles by IsVisible, and PermissionAuthorizationHandler short-circuits
// SuperAdmin. Hiding the three links is NOT the fix — it is the tidy-up. This line is the fix.
//
// The path is /api/docs because that is what the Web app links to; the OpenAPI document Scalar's
// own page fetches afterwards is served from /openapi/, a separate top-level path with its own
// nginx block. Both carry the same policy, or the page would load and its content would not.
//
// A browser navigation carries no Authorization header (this app's auth is JWT-in-localStorage,
// not cookies), so the token arrives as ?access_token= — the same mechanism as the SignalR hub
// negotiation, extended in AddJwtAuthentication to cover these two route prefixes. The
// per-HttpContext overload reads it back out and bakes it into the OpenApiRoutePattern so the
// follow-up fetch is authenticated too.
const string DocsPolicy = RequirePermissionAttribute.PolicyPrefix + Permissions.PlatformAdmin;
app.MapOpenApi().RequireAuthorization(DocsPolicy);
app.MapScalarApiReference("/api/docs", (options, httpContext) =>
{
    var token = httpContext.Request.Query["access_token"].ToString();
    if (!string.IsNullOrEmpty(token))
    {
        options.OpenApiRoutePattern = $"/openapi/v1.json?access_token={Uri.EscapeDataString(token)}";
    }
}).RequireAuthorization(DocsPolicy);

app.UseHttpsRedirection();
app.UseSerilogRequestLogging();
app.UseMiddleware<QMgr.API.Middleware.RequestMetricsMiddleware>();

// wwwroot only. Uploads are NOT here any more: until 2026-09-15 they were written to
// wwwroot/uploads/media and this line served every welfare attachment and visitor photograph to
// anyone holding the URL, before UseAuthentication() below had run. The store now lives outside
// wwwroot (LocalDiskMediaStorageService.ResolveStoreDirectory) and UploadsController serves
// /uploads/media/{file} with a per-file decision — public for signage, token or record-permission
// for everything else. UploadStoreRelocation (startup, below) carries an existing store across.
//
// Because uploads are a controller now, UseCors() applies to them too — the old note that a
// local cross-origin fetch of an upload could never work is no longer true.
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

// A token issued against a temporary password may only change the password or sign out (duty rota
// plan §12.3). Straight after authentication, so nothing below ever acts on one.
app.UseMiddleware<QMgr.Middleware.PasswordChangeOnlyMiddleware>();

// Tenant resolution - must be after authentication to access JWT claims
app.UseTenantResolution();

// Check tenant status (suspended, cancelled, etc.)
app.UseTenantStatus();

// Enforce usage limits based on subscription plan
app.UseUsageLimits();

// Refuse routes belonging to a module the tenant has not purchased. Central backstop behind the
// per-action [RequireModule] attribute — see ModuleRouteMap for the one place the mapping lives.
app.UseModuleAccess();

app.UseAuthorization();

app.MapControllers();
app.MapHub<QueueHub>("/hubs/queue");
app.MapHub<DisplayHub>("/hubs/display");
app.MapHub<NotificationHub>("/hubs/notifications");

// Health check endpoint (unauthenticated for development - secure in production)
app.MapHealthChecks("/health");

// Attach the upload-link signer to its static face before anything can map a DTO — see
// UploadLinks for why the static mappers cannot take it by injection.
QMgr.Infrastructure.Services.Storage.UploadLinks.Use(app.Services.GetRequiredService<IUploadAccessService>());

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

    // The module catalog, in EVERY environment. Until 2026-09-16 the only seed lived in DbSeeder,
    // which runs in Development only, so a module added to the code never reached a production
    // catalog. Insert-if-missing; never overwrites an administrator's edit.
    await new QMgr.Infrastructure.Data.ModuleCatalogDefaults(
        db,
        scope.ServiceProvider.GetRequiredService<ILogger<QMgr.Infrastructure.Data.ModuleCatalogDefaults>>()).RunAsync();

    // Repoint upload links saved with the internal loopback host (http://127.0.0.1:{ApiPort}) onto
    // MediaStorage:PublicBaseUrl. A no-op unless that key is set, and a no-op once repaired.
    var uploadLinkRepair = new QMgr.Infrastructure.Data.UploadLinkRepair(
        db,
        configuration,
        scope.ServiceProvider.GetRequiredService<ILogger<QMgr.Infrastructure.Data.UploadLinkRepair>>());
    await uploadLinkRepair.RunAsync();

    // Legacy uploads get their FilePath, the only column UploadAuthorizer classifies media by.
    await new QMgr.Infrastructure.Data.MediaFilePathBackfill(
        db,
        scope.ServiceProvider.GetRequiredService<ILogger<QMgr.Infrastructure.Data.MediaFilePathBackfill>>()).RunAsync();

    // Move any files still under wwwroot/uploads/media into the gated store, and drop the
    // production symlink that made them reachable as static files. Idempotent; see the class.
    new QMgr.Infrastructure.Data.UploadStoreRelocation(
        configuration,
        app.Environment,
        scope.ServiceProvider.GetRequiredService<ILogger<QMgr.Infrastructure.Data.UploadStoreRelocation>>()).Run();

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

    // Fill the platform Email/SMTP settings from configuration if they are still blank. AFTER the
    // initializer, which creates the row on a fresh database; this covers the existing install the
    // initializer skips entirely (it returns early once any PlatformSettings row exists).
    var platformEmailDefaults = new QMgr.Infrastructure.Data.PlatformEmailDefaults(
        db,
        configuration,
        scope.ServiceProvider.GetRequiredService<ILogger<QMgr.Infrastructure.Data.PlatformEmailDefaults>>());
    await platformEmailDefaults.RunAsync();

    // The row may have just changed underneath the 30-minute settings cache.
    await platformSettingsService.ReloadCacheAsync();
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
VisitorReportSubscriptionJobsRegistration.RegisterRecurringJobs();
WelfareReminderJobRegistration.RegisterRecurringJobs();
AppointmentJobsRegistration.RegisterRecurringJobs();
DocumentShareJobsRegistration.RegisterRecurringJobs();
StaffPerformanceJobsRegistration.RegisterRecurringJobs();
ReminderLadderJobRegistration.RegisterRecurringJobs();
TimetableIntegrityJobRegistration.RegisterRecurringJobs();
LessonGenerationJobRegistration.RegisterRecurringJobs();
StaffOnboardingJobsRegistration.RegisterRecurringJobs();

app.Run();
