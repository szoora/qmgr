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
        services.AddSingleton<IVisitorBadgeTokenService, VisitorBadgeTokenService>();
        // Gated uploads (2026-09-15): the token minter is stateless, the authorizer reads the
        // caller's HTTP context and memoises the student scope, so it is scoped like that service.
        services.AddSingleton<IUploadAccessService, UploadAccessService>();
        services.AddScoped<IUploadAuthorizer, UploadAuthorizer>();
        services.AddScoped<IDocumentShareService, DocumentShareService>();
        services.AddScoped<IVisitorActivityBroadcaster, VisitorActivityBroadcaster>();
        services.AddScoped<QMgr.API.Application.Services.IVisitorReportingService, QMgr.API.Application.Services.VisitorReportingService>();
        services.AddScoped<IRosterImportBroadcaster, RosterImportBroadcaster>();
        services.AddScoped<IBatchOperationService, BatchOperationService>();

        // Row-level student visibility (the second axis to the permission table — see the service's
        // own doc comment). SCOPED and memoised per request on purpose: it must NOT ride the
        // five-minute permission cache, or a teacher removed from a class keeps reading it.
        services.AddScoped<IStudentScopeService, StudentScopeService>();

        // Staff Performance Monitor (2026-09-16). The staff-axis twin of the student scope, the
        // explicit activity log, the one policy reader, the scorer and the alert rule.
        services.AddScoped<IStaffScopeService, StaffScopeService>();
        services.AddScoped<IActivityLogger, ActivityLogger>();
        services.AddScoped<IStaffPerformancePolicyService, StaffPerformancePolicyService>();
        services.AddScoped<ITimetableSettingsService, TimetableSettingsService>();
        services.AddSingleton<IReminderLadderService, ReminderLadderService>();
        services.AddScoped<IStaffScoringService, StaffScoringService>();
        services.AddScoped<IStaffAlertService, StaffAlertService>();
        // Policy-gated automatic credit from welfare, queue and visitor activity (off by default).
        services.AddScoped<IStaffSystemAwards, StaffSystemAwards>();
        services.AddScoped<IStaffProfileChangeNotifier, StaffProfileChangeNotifier>();

        // Staff onboarding (duty rota plan §12, 2026-09-17): the one reader of
        // Organization.Settings["StaffOnboarding"], and the email twin of the phone verification code.
        services.AddScoped<IStaffOnboardingPolicyService, StaffOnboardingPolicyService>();
        services.AddScoped<IEmailCodeVerificationService, EmailCodeVerificationService>();

        // Notification Services
        services.AddScoped<INotificationService, NotificationService>();
        // Per-user channel preferences, applied before anything leaves the building.
        services.AddScoped<INotificationPreferenceResolver, NotificationPreferenceResolver>();
        // Off-thread, retried, logged delivery. Resolved by Hangfire, not injected anywhere.
        services.AddScoped<QMgr.Infrastructure.Jobs.NotificationDispatchJob>();
        // The staff sweeps. Hangfire resolves these through the same container, and the
        // Development-only "run the weekly lesson analysis now" trigger injects the first one.
        services.AddScoped<QMgr.Infrastructure.Jobs.StaffPerformanceJobs>();
        // Tells a class teacher when one of their students has a (non-confidential) case logged.
        services.AddScoped<IWelfareAlertService, WelfareAlertService>();
        // Queue-side customer messaging: ticket issued, nearly your turn, called to counter.
        services.AddScoped<IQueueCustomerNotifier, QueueCustomerNotifier>();
        // Duplicate-registration detection and phone ownership proof.
        services.AddScoped<IRegistrationGuardService, RegistrationGuardService>();
        services.AddScoped<IPhoneVerificationService, PhoneVerificationService>();
        services.AddScoped<INotificationSettingsService, NotificationSettingsService>();

        // Platform-level email (org-less context, e.g. pre-verification signup email)
        services.AddScoped<IEmailSender, EmailSender>();
        // One home for "which SMTP account does this organization send through?" — used by the
        // tenant send, the tenant test-send and the platform sender alike.
        services.AddScoped<ISmtpProfileResolver, SmtpProfileResolver>();

        // Billing Services
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IStripeService, StripeService>();
        services.AddScoped<IUsageTrackingService, UsageTrackingService>();
        services.AddScoped<IFeatureFlagService, FeatureFlagService>();
        services.AddScoped<IModuleLimitResolver, ModuleLimitResolver>();
        services.AddScoped<IBillingAccountProvider, BillingAccountProvider>();
        services.AddScoped<IModuleAccessService, ModuleAccessService>();

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
            // UPLOADS only. UploadsController still serves from the local store, so this provider
            // makes every uploaded file unreachable once written — Program.cs logs a loud error at
            // startup saying so. Do not flip this on without teaching the serving path to stream.
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

        // HTTP Clients for Telegram Bot API and WhatsApp Cloud API — real, fixed endpoints
        // (unlike SmsGateway below, these aren't pluggable per-org, Telegram/Meta host them).
        services.AddHttpClient("TelegramApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.BaseAddress = new Uri("https://api.telegram.org/");
        });
        services.AddHttpClient("WhatsAppApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.BaseAddress = new Uri("https://graph.facebook.com/v18.0/");
        });

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
        // BaseAddress and the API key are resolved by the service itself from Platform Settings
        // (with configuration as fallback), so nothing config-specific is baked in here.
        services.AddHttpClient<IMobileMoneyService, MobileMoneyService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60); // Mobile money can be slow
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
