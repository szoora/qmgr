using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Onboarding housekeeping (duty rota plan §12.4): rejected and expired join requests are deleted after
/// the organization's window — an applicant's details are not kept without a purpose (Uganda DPPA s.3,
/// s.12). The activity event that recorded the request carries no applicant details, and stays.
/// </summary>
public class StaffOnboardingJobs
{
    private readonly QMgrDbContext _db;
    private readonly IStaffOnboardingPolicyService _policy;
    private readonly IActivityLogger _activity;
    private readonly ILogger<StaffOnboardingJobs> _logger;

    public StaffOnboardingJobs(QMgrDbContext db, IStaffOnboardingPolicyService policy, IActivityLogger activity, ILogger<StaffOnboardingJobs> logger)
    {
        _db = db;
        _policy = policy;
        _activity = activity;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 2)]
    public async Task PurgeJoinRequestsAsync()
    {
        var organizations = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => !u.IsActive && u.PendingApprovalAt != null)
            .Select(u => u.OrganizationId)
            .Distinct()
            .ToListAsync();

        var total = 0;
        foreach (var orgId in organizations)
        {
            try
            {
                var policy = await _policy.GetAsync(orgId);
                var now = DateTime.UtcNow;
                var rejectedBefore = now.AddDays(-policy.PurgeAfterDays);
                // An unanswered request expires after RequestExpiryDays and is purged PurgeAfterDays after that.
                var requestedBefore = now.AddDays(-(policy.RequestExpiryDays + policy.PurgeAfterDays));

                var deleted = await _db.Users.IgnoreQueryFilters()
                    .Where(u => u.OrganizationId == orgId && !u.IsActive && u.PendingApprovalAt != null
                                && ((u.JoinRequestRejectedAt != null && u.JoinRequestRejectedAt < rejectedBefore)
                                    || (u.JoinRequestRejectedAt == null && u.PendingApprovalAt < requestedBefore)))
                    .ExecuteDeleteAsync();

                if (deleted > 0)
                {
                    total += deleted;
                    await _activity.RecordAsync(ActivityActions.JoinRequestsPurged, "User", null, null,
                        $"{deleted} rejected or expired join request(s) deleted after {policy.PurgeAfterDays} days",
                        new { Deleted = deleted }, organizationId: orgId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Join request purge failed for organization {OrganizationId}", orgId);
            }
        }

        if (total > 0) _logger.LogInformation("Join request purge: {Count} request(s) deleted", total);
    }
}

public static class StaffOnboardingJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // 03:40 UTC daily, beside the other retention purges.
        RecurringJob.AddOrUpdate<StaffOnboardingJobs>("staff-join-request-purge", job => job.PurgeJoinRequestsAsync(), "40 3 * * *");
    }
}
