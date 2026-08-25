using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Interfaces;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Data.Repositories;
using QMgr.Infrastructure.Services;
using QMgr.Infrastructure.Services.Billing;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Tenant Context (Singleton - uses AsyncLocal internally)
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();

        // Database
        services.AddDbContext<QMgrDbContext>(options =>
        {
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsqlOptions =>
                {
                    npgsqlOptions.EnableRetryOnFailure(3);
                    npgsqlOptions.CommandTimeout(30);
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "qmgr");
                });
        });

        // Repositories
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ITokenRepository, TokenRepository>();
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

        // Services
        services.AddScoped<IQueueService, QueueService>();
        services.AddScoped<IWebhookService, WebhookService>();

        // Notification Services
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationSettingsService, NotificationSettingsService>();

        // Platform-level email (org-less context, e.g. pre-verification signup email)
        services.AddScoped<IEmailSender, EmailSender>();

        // Billing Services
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IStripeService, StripeService>();
        services.AddScoped<IUsageTrackingService, UsageTrackingService>();
        services.AddScoped<IFeatureFlagService, FeatureFlagService>();

        // Tenant Provisioning Service (for self-service onboarding)
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        // Platform Settings Service
        services.AddScoped<IPlatformSettingsService, PlatformSettingsService>();

        // Request metrics (backs HealthController's performance-metrics endpoint)
        services.AddSingleton<IRequestMetricsService, RequestMetricsService>();

        // Media storage — defaults to local disk (matches pre-existing ContentController
        // behavior exactly); set MediaStorage:Provider="S3" once real bucket credentials
        // exist (Production Rollout Plan Stage 3). Registering IAmazonS3 only when actually
        // selected avoids requiring AWS config just to run locally.
        var mediaStorageProvider = configuration["MediaStorage:Provider"] ?? "Local";
        if (string.Equals(mediaStorageProvider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAmazonS3>(_ =>
            {
                var serviceUrl = configuration["MediaStorage:S3:ServiceUrl"]; // set for MinIO/DigitalOcean Spaces; leave unset for real AWS
                var region = configuration["MediaStorage:S3:Region"];
                var s3Config = new AmazonS3Config();
                if (!string.IsNullOrEmpty(serviceUrl))
                {
                    s3Config.ServiceURL = serviceUrl;
                    s3Config.ForcePathStyle = true;
                }
                else if (!string.IsNullOrEmpty(region))
                {
                    s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
                }
                return new AmazonS3Client(s3Config);
            });
            services.AddScoped<IMediaStorageService, S3MediaStorageService>();
        }
        else
        {
            services.AddScoped<IMediaStorageService, LocalDiskMediaStorageService>();
        }

        // HTTP Client for webhooks
        services.AddHttpClient("Webhook", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // HTTP Clients for Spotify (platform-wide OAuth connection — see ISpotifyService)
        services.AddHttpClient("SpotifyAuth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient("SpotifyApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<ISpotifyService, SpotifyService>();

        // HTTP Client for SMS Gateway
        services.AddHttpClient("SmsGateway", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            // Default base URL can be overridden by NotificationSettings.SmsGatewayUrl
            var smsGatewayUrl = configuration["SmsGateway:BaseUrl"];
            if (!string.IsNullOrEmpty(smsGatewayUrl))
            {
                client.BaseAddress = new Uri(smsGatewayUrl);
            }
        });

        // HTTP Client for Mobile Money (CRM Epay Gateway)
        services.AddHttpClient<IMobileMoneyService, MobileMoneyService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60); // Mobile money can be slow
            var crmApiUrl = configuration["MobileMoney:CrmApiUrl"];
            if (!string.IsNullOrEmpty(crmApiUrl))
            {
                client.BaseAddress = new Uri(crmApiUrl);
            }
        });

        // Redis caching (optional)
        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConnection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
                options.InstanceName = "QMgr_";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        return services;
    }
}
