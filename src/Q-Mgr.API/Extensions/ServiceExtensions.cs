using System.Text;
using AspNetCoreRateLimit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QMgr.API.Hubs;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Extensions;

public static class ServiceExtensions
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Read JWT secret from environment variable first, then fallback to configuration
        // Environment variable: JWT__Secret (double underscore for nested config)
        var jwtSecret = Environment.GetEnvironmentVariable("JWT__Secret")
                        ?? configuration["JWT:Secret"];

        var isDevelopment = configuration["ASPNETCORE_ENVIRONMENT"] == "Development";

        // Validate JWT secret exists and meets minimum security requirements
        if (string.IsNullOrWhiteSpace(jwtSecret))
        {
            throw new InvalidOperationException(
                "JWT Secret is not configured. Set environment variable JWT__Secret or configure JWT:Secret in appsettings.json");
        }

        if (jwtSecret.Length < 32)
        {
            throw new InvalidOperationException(
                $"JWT Secret must be at least 32 characters long for security. Current length: {jwtSecret.Length}");
        }

        // Warn if using default/example secret - only enforce in production
        if (!isDevelopment && (jwtSecret.Contains("YourSuperSecretKey") || jwtSecret.Contains("ChangeMe")))
        {
            throw new InvalidOperationException(
                "JWT Secret is using a default/example value. This is a critical security vulnerability. " +
                "Set a strong, unique secret via JWT__Secret environment variable.");
        }

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = configuration["JWT:Issuer"],
                ValidAudience = configuration["JWT:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtSecret)),
                ClockSkew = TimeSpan.Zero
            };

            // Allow SignalR (and, below, the API docs page) to receive the token from the query
            // string rather than an Authorization header - neither a WebSocket handshake nor a
            // plain browser navigation (clicking the "API Documentation" link) can attach one.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;

                    if (!string.IsNullOrEmpty(accessToken) &&
                        (path.StartsWithSegments("/hubs") ||
                         path.StartsWithSegments("/api/docs") ||
                         path.StartsWithSegments("/openapi")))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });

        return services;
    }

    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration, string? connectionString)
    {
        // The admin-facing Platform Settings UI (PlatformSetting row, Category="RateLimiting")
        // was previously a completely disconnected copy of this config — it wrote to the
        // database while AspNetCoreRateLimit only ever read appsettings.json's "IpRateLimiting"
        // section, so editing it in the UI had zero effect on real request throttling. Fixed by
        // preferring the DB row here (read once at startup, same as appsettings.json always was —
        // this does NOT add hot-reload/live-without-restart support, matching the scope decision
        // to skip the AspNetCoreRateLimit IIpPolicyStore dynamic-update work). Field names in
        // RateLimitSettings/RateLimitRule (Domain/Entities/Platform/PlatformSetting.cs) already
        // mirror IpRateLimitOptions/RateLimitRule exactly, so standard IConfiguration binding
        // (case-insensitive JSON-to-property matching) picks them up with no manual mapping.
        var dbRateLimitConfig = TryLoadRateLimitConfigFromDatabase(connectionString);
        var rateLimitSection = dbRateLimitConfig?.GetSection("IpRateLimiting") ?? configuration.GetSection("IpRateLimiting");

        services.Configure<IpRateLimitOptions>(rateLimitSection);
        services.Configure<IpRateLimitPolicies>(configuration.GetSection("IpRateLimitPolicies"));

        services.AddInMemoryRateLimiting();
        services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

        return services;
    }

    /// <summary>
    /// Reads the "RateLimiting" PlatformSetting row directly (no DI container exists yet at this
    /// point in startup) and wraps its JSON as an IConfiguration section shaped like
    /// appsettings.json's "IpRateLimiting" section, so AspNetCoreRateLimit's standard options
    /// binding picks it up unchanged. Returns null on any failure (no connection string, no row
    /// saved yet, malformed JSON) so the caller falls back to appsettings.json — startup must
    /// never fail because of this.
    /// </summary>
    private static IConfigurationRoot? TryLoadRateLimitConfigFromDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        try
        {
            var options = new DbContextOptionsBuilder<QMgrDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            using var db = new QMgrDbContext(options);
            var settingsJson = db.PlatformSettings
                .Where(s => s.Category == "RateLimiting")
                .Select(s => s.SettingsJson)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(settingsJson))
                return null;

            var wrapped = $"{{\"IpRateLimiting\":{settingsJson}}}";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wrapped));
            return new ConfigurationBuilder().AddJsonStream(stream).Build();
        }
        catch
        {
            // Table/row may not exist yet on a fresh install, or the DB may not be reachable
            // this early in startup — appsettings.json is the safe fallback either way.
            return null;
        }
    }

    public static IServiceCollection AddSignalRServices(this IServiceCollection services)
    {
        services.AddSingleton<IQueueHubContext, QueueHubContext>();
        services.AddSingleton<IDisplayHubContext, DisplayHubContext>();

        return services;
    }
}
