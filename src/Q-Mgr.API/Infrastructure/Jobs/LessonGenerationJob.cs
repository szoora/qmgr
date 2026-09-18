using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Lesson materialisation (duty rota plan §7.1): nightly for every branch with a published timetable in force or about to
/// be, and once for a branch right after a publish. The work is <see cref="StaffLessons.MaterialiseAsync"/>, which is
/// idempotent, so a publish landing during the nightly run changes nothing but timing.
/// </summary>
public class LessonGenerationJob
{
    public const string JobId = "staff-lesson-generation";

    private readonly QMgrDbContext _context;
    private readonly ITimetableSettingsService _settings;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly INotificationService _notifications;
    private readonly IModuleAccessService _modules;
    private readonly ILogger<LessonGenerationJob> _logger;

    public LessonGenerationJob(QMgrDbContext context, ITimetableSettingsService settings, IStaffPerformancePolicyService policy,
        INotificationService notifications, IModuleAccessService modules, ILogger<LessonGenerationJob> logger)
    {
        _context = context;
        _settings = settings;
        _policy = policy;
        _notifications = notifications;
        _modules = modules;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task RunAsync()
    {
        var soon = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(StaffLessons.WindowDays + 1));
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var branches = await _context.Timetables.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Status == TimetableStatus.Published && t.EffectiveFrom <= soon && t.EffectiveTo >= yesterday)
            .Select(t => new { t.BranchId, t.OrganizationId }).Distinct().ToListAsync();

        var moduleActive = new Dictionary<Guid, bool>();
        foreach (var b in branches)
        {
            try
            {
                if (!moduleActive.TryGetValue(b.OrganizationId, out var active))
                    active = moduleActive[b.OrganizationId] = await _modules.IsModuleActiveAsync(b.OrganizationId, ModuleCodes.StudentWelfare);
                if (!active) continue;
                await StaffLessons.MaterialiseAsync(_context, _settings, _policy, _notifications, _logger, b.BranchId, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lesson generation failed for branch {BranchId}", b.BranchId);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Enqueued by a publish. Also removes the replaced version's future, unflagged lessons.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task RunForBranchAsync(Guid branchId)
        => await StaffLessons.MaterialiseAsync(_context, _settings, _policy, _notifications, _logger, branchId, DateTime.UtcNow);
}

public static class LessonGenerationJobRegistration
{
    /// <summary>01:00 UTC — 04:00 in Kampala, well before the morning My Day digest.</summary>
    public static void RegisterRecurringJobs()
        => RecurringJob.AddOrUpdate<LessonGenerationJob>(LessonGenerationJob.JobId, job => job.RunAsync(), "0 1 * * *");
}
