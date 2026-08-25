using AspNetCoreRateLimit;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QMgr.Infrastructure.Data;
using System.Text.Json;
using DbRateLimitRule = QMgr.Domain.Entities.Platform.RateLimitRule;
using LiveRateLimitRule = AspNetCoreRateLimit.RateLimitRule;
using RateLimitSettings = QMgr.Domain.Entities.Platform.RateLimitSettings;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Makes the admin-editable RateLimiting settings (PlatformSetting, Category="RateLimiting")
/// take effect without an app restart. AspNetCoreRateLimit has no first-class hot-reload API for
/// its general/global rules — the library's own request-time components (e.g.
/// IpRateLimitProcessor) are constructed with the *unwrapped* IpRateLimitOptions instance, not
/// IOptionsMonitor, and services.Configure&lt;T&gt;() only binds it once at startup. But that
/// unwrapped instance is the SAME object every request resolves (IOptions&lt;T&gt;.Value is a
/// cached singleton) — so mutating its properties/list *in place* here is visible to every
/// subsequent request immediately, without needing the library to support reload at all. Confirmed
/// via reflection over the installed AspNetCoreRateLimit 5.0.0 assembly before writing this
/// (IpRateLimitProcessor's constructor takes `IpRateLimitOptions options` directly).
/// </summary>
public class RateLimitSyncJob
{
    private readonly QMgrDbContext _dbContext;
    private readonly IOptions<IpRateLimitOptions> _rateLimitOptions;
    private readonly ILogger<RateLimitSyncJob> _logger;

    public RateLimitSyncJob(
        QMgrDbContext dbContext,
        IOptions<IpRateLimitOptions> rateLimitOptions,
        ILogger<RateLimitSyncJob> logger)
    {
        _dbContext = dbContext;
        _rateLimitOptions = rateLimitOptions;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 1)]
    public async Task SyncAsync()
    {
        var settingsJson = await _dbContext.PlatformSettings
            .Where(s => s.Category == "RateLimiting")
            .Select(s => s.SettingsJson)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(settingsJson))
            return;

        RateLimitSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<RateLimitSettings>(settingsJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "RateLimiting settings JSON could not be parsed — leaving live config unchanged");
            return;
        }

        if (settings == null)
            return;

        var live = _rateLimitOptions.Value;

        var rulesChanged = !RulesEqual(live.GeneralRules, settings.GeneralRules);
        var scalarsChanged = live.EnableEndpointRateLimiting != settings.EnableEndpointRateLimiting
            || live.StackBlockedRequests != settings.StackBlockedRequests
            || live.HttpStatusCode != settings.HttpStatusCode;

        if (!rulesChanged && !scalarsChanged)
            return;

        live.EnableEndpointRateLimiting = settings.EnableEndpointRateLimiting;
        live.StackBlockedRequests = settings.StackBlockedRequests;
        live.HttpStatusCode = settings.HttpStatusCode;

        // THREAD SAFETY: build the replacement list off to the side, then do a single atomic
        // reference-assignment to the GeneralRules property, instead of the previous Clear() +
        // AddRange() in place. Clear()+AddRange() mutates a List<T> — which is not thread-safe —
        // while concurrent request threads may be enumerating it via IpRateLimitProcessor at the
        // exact same moment (this job runs on a Hangfire background thread, requests run
        // concurrently on their own); that combination risks "Collection was modified" exceptions
        // or a momentary empty rule set mid-Clear(). A single reference write is atomic per the
        // CLR spec, so any concurrent reader of live.GeneralRules sees either the fully-old or
        // fully-new list, never a partially-cleared one. This still preserves what actually needs
        // to stay stable — the shared IpRateLimitOptions instance itself (confirmed via reflection,
        // see the class doc comment) — readers resolve GeneralRules through that same object, they
        // don't cache the inner List<T> reference separately.
        live.GeneralRules = settings.GeneralRules.Select(r => new LiveRateLimitRule
        {
            Endpoint = r.Endpoint,
            Period = r.Period,
            Limit = r.Limit
        }).ToList();

        _logger.LogInformation(
            "Live-reloaded RateLimiting config from the database ({RuleCount} general rule(s)) — no restart required",
            live.GeneralRules.Count);
    }

    private static bool RulesEqual(List<LiveRateLimitRule> live, List<DbRateLimitRule> fromDb)
    {
        if (live.Count != fromDb.Count)
            return false;

        for (var i = 0; i < live.Count; i++)
        {
            if (live[i].Endpoint != fromDb[i].Endpoint ||
                live[i].Period != fromDb[i].Period ||
                Math.Abs(live[i].Limit - fromDb[i].Limit) > 0.0001)
            {
                return false;
            }
        }

        return true;
    }
}

public static class RateLimitJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Every minute — the shortest granularity Hangfire's cron scheduling supports. Trades a
        // small worst-case delay (up to ~60s after saving) for genuinely no-restart-required
        // reload, versus the previous "needs a full app restart" behavior.
        RecurringJob.AddOrUpdate<RateLimitSyncJob>(
            "sync-rate-limit-config",
            job => job.SyncAsync(),
            Cron.Minutely);
    }
}
