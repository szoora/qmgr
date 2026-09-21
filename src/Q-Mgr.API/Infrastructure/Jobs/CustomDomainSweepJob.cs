using Hangfire;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Finishes off tenant domains that were claimed and are waiting on DNS.
///
/// Once daily, and NOT more often, which is the whole design rather than a schedule preference: a
/// failing domain retried in a loop spends the box's weekly Let's Encrypt budget and blocks
/// issuance for every other tenant on it. <c>CustomDomainService.SweepAsync</c> also skips anything
/// tried within the hour and gives up entirely after a week of failures, so the unattended path
/// always stops. A platform administrator pressing "Check now" is never throttled — it is the loop
/// that has to back off, not the person.
/// </summary>
public class CustomDomainSweepJob
{
    private readonly ICustomDomainService _domains;
    private readonly ILogger<CustomDomainSweepJob> _logger;

    public CustomDomainSweepJob(ICustomDomainService domains, ILogger<CustomDomainSweepJob> logger)
    {
        _domains = domains;
        _logger = logger;
    }

    public async Task SweepAsync()
    {
        try
        {
            await _domains.SweepAsync();
        }
        catch (Exception ex)
        {
            // A sweep that throws must not take the recurring job down; tomorrow's run retries.
            _logger.LogError(ex, "The custom-domain sweep failed");
        }
    }
}

public static class CustomDomainJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // 04:15 UTC — after the overnight purges, and inside the window where a DNS change made
        // during a working day has had time to propagate.
        RecurringJob.AddOrUpdate<CustomDomainSweepJob>(
            "sweep-custom-domains",
            job => job.SweepAsync(),
            "15 4 * * *");
    }
}
