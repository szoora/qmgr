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

        // Whose name an outbound email carries. One reader, so the twenty-two senders do not each decide it.
        services.AddScoped<QMgr.Infrastructure.Email.IEmailBrandService, QMgr.Infrastructure.Email.EmailBrandService>();

        // A tenant's own domain. IDnsTxtLookup is an interface with one method so the verification
        // flow can be exercised with no zone to publish in; ITenantDomainActivator so it can be
        // exercised with no host to touch.
        services.AddScoped<ICustomDomainService, QMgr.Infrastructure.Services.Domains.CustomDomainService>();

        // Tenant lifecycle and purge. The purge derives its own plan from the EF model; the only
        // hand-maintained part is TenantDataManifest, and TenantPurgeModelGuard refuses to start
        // the application when a table in the model is missing from it.
        services.AddScoped<ITenantPurgeService, QMgr.Infrastructure.Data.Purge.TenantPurgeService>();
        services.AddScoped<ITenantLifecycleService, QMgr.Infrastructure.Data.Purge.TenantLifecycleService>();
        // The stub answers from memory and is DEVELOPMENT ONLY — the environment is checked here
        // as well as the key, so a Dns:Stub left in a production config file does nothing. It
        // exists so the verification flow can be exercised against a live database without owning
        // a zone; everything else in CustomDomainService runs for real either way.
        // No IHostEnvironment here, so the environment is read the way this method already reads
        // configuration: ASPNETCORE_ENVIRONMENT is what sets it, and both have to agree.
        if (string.Equals(configuration["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase) && configuration.GetValue("Dns:Stub", false))
            services.AddSingleton<IDnsTxtLookup, QMgr.Infrastructure.Services.Domains.StubDnsTxtLookup>();
        else
            services.AddSingleton<IDnsTxtLookup, QMgr.Infrastructure.Services.Domains.DnsTxtLookup>();
        services.AddSingleton<QMgr.Infrastructure.Services.Domains.ITenantDomainActivator, QMgr.Infrastructure.Services.Domains.NginxTenantDomainActivator>();
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
        // Changing a PUBLISHED timetable: copy, trade, check, publish over. Scoped, because it takes the same
        // advisory locks the publish endpoint does and must share the request's DbContext with it.
        services.AddScoped<QMgr.API.Application.Services.ITimetableRepublishService, QMgr.API.Application.Services.TimetableRepublishService>();
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
        services.AddScoped<QMgr.Infrastructure.Jobs.AccountLifecycleJobs>();
        // Tells a class teacher when one of their students has a (non-confidential) case logged.
        services.AddScoped<IWelfareAlertService, WelfareAlertService>();
        // Queue-side customer messaging: ticket issued, nearly your turn, called to counter.
        services.AddScoped<IQueueCustomerNotifier, QueueCustomerNotifier>();
        // Duplicate-registration detection and phone ownership proof.
        services.AddScoped<IRegistrationGuardService, RegistrationGuardService>();
        // "Does this email domain already belong to a tenant that invites people at it?" — the
        // registration form asks before somebody creates a second copy of their own school.
        services.AddScoped<IOrganizationHintService, OrganizationHintService>();
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

        // ── The mobile shell (2026-09-22) ─────────────────────────────────────────────────────
        //
        // Scoped, like everything else that touches the DbContext. IDeviceSessionService in
        // particular must NOT be a singleton: it reads and rotates rows, and a shared DbContext
        // across requests is how two concurrent refreshes from one handset corrupt each other.
        services.AddScoped<Services.Mobile.IDeviceSessionService, Services.Mobile.DeviceSessionService>();
        services.AddScoped<Services.Mobile.IPushSender, Services.Mobile.PushSender>();
        services.AddScoped<Services.Mobile.IAppDistributionService, Services.Mobile.AppDistributionService>();

        // The handoff code store is in-memory and holds no DbContext, so a singleton is right — and
        // it has to be, or each request would get an empty cache and no code could ever be redeemed.
        services.AddSingleton<Services.Mobile.IMobileHandoffService, Services.Mobile.MobileHandoffService>();

        // Reads the published-build manifest from the central distribution host. Short timeout on
        // purpose: this is called on the app's start-up path, and a slow answer must degrade to
        // "no update" quickly rather than hold the splash screen.
        services.AddHttpClient(Services.Mobile.AppDistributionService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Firebase Cloud Messaging, plus Google's OAuth token endpoint. One client for both because
        // they are the same hop to the same operator and share the same failure mode.
        services.AddHttpClient(Services.Mobile.PushSender.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
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

        // The sacc.ug payment gateway (2026-09-19). A named client with NO retry policy, deliberately:
        // a retried collect can prompt the payer twice (the gateway's own rule, CRM-4.6). The address
        // and key are read from Platform Settings on every call, so nothing is baked in here. The
        // collect call answers 202 at once; 30 seconds is ample and keeps a slow gateway from holding
        // a customer's request open.
        services.AddHttpClient(SaccGateway.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<ISaccGateway, SaccGateway>();
        services.AddScoped<IPaymentLedger, PaymentLedger>();

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
