using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Platform;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

public class PlatformSettingsService : IPlatformSettingsService
{
    private readonly QMgrDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlatformSettingsService> _logger;
    private const string CacheKeyPrefix = "PlatformSettings_";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public PlatformSettingsService(
        QMgrDbContext context,
        IMemoryCache cache,
        ILogger<PlatformSettingsService> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<T?> GetSettingsAsync<T>(string category) where T : class
    {
        var cacheKey = $"{CacheKeyPrefix}{category}";

        // Try cache first
        if (_cache.TryGetValue<T>(cacheKey, out var cachedSettings))
        {
            return cachedSettings;
        }

        // Load from database
        var setting = await _context.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Category == category && s.IsEnabled);

        if (setting == null)
        {
            _logger.LogWarning("Platform setting not found: {Category}", category);
            return null;
        }

        var settings = setting.GetSettings<T>();

        // Cache for future use
        if (settings != null)
        {
            _cache.Set(cacheKey, settings, CacheDuration);
        }

        return settings;
    }

    public async Task<bool> UpdateSettingsAsync<T>(string category, T settings) where T : class
    {
        try
        {
            var setting = await _context.PlatformSettings
                .FirstOrDefaultAsync(s => s.Category == category);

            if (setting == null)
            {
                _logger.LogWarning("Cannot update non-existent setting: {Category}", category);
                return false;
            }

            if (!setting.IsEditable)
            {
                _logger.LogWarning("Setting is not editable: {Category}", category);
                return false;
            }

            setting.SetSettings(settings);
            setting.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Invalidate cache
            _cache.Remove($"{CacheKeyPrefix}{category}");

            _logger.LogInformation("Platform setting updated: {Category}", category);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating platform setting: {Category}", category);
            return false;
        }
    }

    public async Task<List<PlatformSetting>> GetAllSettingsAsync()
    {
        return await _context.PlatformSettings
            .AsNoTracking()
            .OrderBy(s => s.DisplayOrder)
            .ToListAsync();
    }

    public async Task<PlatformSetting?> GetSettingByCategoryAsync(string category)
    {
        return await _context.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Category == category);
    }

    public async Task ReloadCacheAsync()
    {
        // Clear all cached settings
        var allSettings = await GetAllSettingsAsync();
        foreach (var setting in allSettings)
        {
            _cache.Remove($"{CacheKeyPrefix}{setting.Category}");
        }

        _logger.LogInformation("Platform settings cache cleared");
    }

    public async Task InitializeDefaultSettingsAsync()
    {
        // Check if settings already exist
        if (await _context.PlatformSettings.AnyAsync())
        {
            _logger.LogInformation("Platform settings already initialized");
            return;
        }

        _logger.LogInformation("Initializing default platform settings...");

        var defaultSettings = new List<PlatformSetting>
        {
            new()
            {
                Category = "JWT",
                DisplayName = "JWT Authentication",
                Description = "JSON Web Token configuration for API authentication",
                DisplayOrder = 1,
                Icon = "shield-lock",
                IsEditable = true,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new JwtSettings
                {
                    Secret = "YourSuperSecretKeyThatShouldBeAtLeast32CharactersLong!",
                    Issuer = "qmgr-api",
                    Audience = "qmgr-clients",
                    ExpiryMinutes = 60
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            },
            new()
            {
                Category = "CORS",
                DisplayName = "CORS Settings",
                Description = "Cross-Origin Resource Sharing configuration",
                DisplayOrder = 2,
                Icon = "globe",
                IsEditable = true,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new CorsSettings
                {
                    AllowedOrigins = new List<string>
                    {
                        "http://localhost:5002",
                        "https://localhost:5002",
                        "http://localhost:5003",
                        "https://localhost:5003"
                    },
                    AllowCredentials = true
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            },
            new()
            {
                Category = "RateLimiting",
                DisplayName = "Rate Limiting",
                Description = "API rate limiting and throttling configuration",
                DisplayOrder = 3,
                Icon = "speedometer",
                IsEditable = true,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new RateLimitSettings
                {
                    EnableEndpointRateLimiting = true,
                    StackBlockedRequests = false,
                    RealIpHeader = "X-Real-IP",
                    ClientIdHeader = "X-ClientId",
                    HttpStatusCode = 429,
                    GeneralRules = new List<RateLimitRule>
                    {
                        new() { Endpoint = "*", Period = "1m", Limit = 100 },
                        new() { Endpoint = "post:/api/v1/*/tokens", Period = "1m", Limit = 50 }
                    }
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            },
            new()
            {
                Category = "SaaS",
                DisplayName = "SaaS Platform",
                Description = "Multi-tenant SaaS platform configuration",
                DisplayOrder = 4,
                Icon = "cloud",
                IsEditable = true,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new SaasSettings
                {
                    BaseDomain = "cashbook.ug",
                    BaseUrl = "https://cashbook.ug",
                    TrialDays = 14,
                    DefaultPlanCode = "free",
                    AllowCustomDomains = true,
                    RequireEmailVerification = true,
                    MaxOrganizationsPerUser = 5
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            },
            new()
            {
                Category = "Stripe",
                DisplayName = "Stripe Billing",
                Description = "Stripe payment gateway configuration",
                DisplayOrder = 5,
                Icon = "credit-card",
                IsEditable = true,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new StripeSettings
                {
                    SecretKey = "",
                    PublishableKey = "",
                    WebhookSecret = "",
                    TestMode = true,
                    Enabled = true
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            },
            new()
            {
                Category = "MobileMoney",
                DisplayName = "Mobile Money",
                Description = "Mobile money payment integration",
                DisplayOrder = 6,
                Icon = "phone",
                IsEditable = true,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new MobileMoneySettings
                {
                    CrmApiUrl = "",
                    ApiKey = "",
                    Enabled = false
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            },
            new()
            {
                Category = "Ads",
                DisplayName = "Advertising",
                Description = "Advertisement display configuration",
                DisplayOrder = 7,
                Icon = "badge-ad",
                IsEditable = true,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new AdsSettings
                {
                    Provider = "internal",
                    GoogleAdSenseClientId = "",
                    InternalAdsApiUrl = "/api/v1/ads",
                    ShowAdsOnFreePlan = true
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            },
            new()
            {
                Category = "Email",
                DisplayName = "Email/SMTP",
                Description = "Email and SMTP server configuration",
                DisplayOrder = 8,
                Icon = "envelope",
                IsEditable = true,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new EmailSettings
                {
                    SmtpHost = "",
                    SmtpPort = 587,
                    SmtpUsername = "",
                    SmtpPassword = "",
                    FromEmail = "noreply@qmgr.app",
                    FromName = "Q-Mgr",
                    UseSsl = true
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            }
        };

        _context.PlatformSettings.AddRange(defaultSettings);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Platform settings initialized successfully");
    }
}
