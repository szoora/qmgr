using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Automatic credit from another module's activity — a welfare record filed, a customer served, a
/// visitor hosted (plan §6 item 5, decision 4). OFF BY DEFAULT: nothing happens unless the tenant's
/// policy has <c>SystemAwardsEnabled</c> AND an active PerformanceParameter marked IsSystemSource
/// whose Name matches the hint the caller passes ("Welfare record filed", "Customer served",
/// "Visitor hosted"). Nothing is seeded; the tenant creates the parameter with exactly that name
/// when they want the credit, which is also how they choose its points, cap and weight.
///
/// The record it writes is Source = System, Visibility Standard, logged by the subject themselves,
/// so the timeline labels it "automatic" and nobody mistakes it for a colleague's judgement.
/// MaxPointsPerPeriod is respected: once the period's cap is reached the credit is silently skipped.
///
/// NEVER THROWS. Every call site is a side effect after another module's transaction has
/// committed, and a committed write must not be failed by its garnish.
/// </summary>
public interface IStaffSystemAwards
{
    Task CreditAsync(Guid organizationId, Guid branchId, Guid userId, string parameterNameOrKindHint, string description, CancellationToken cancellationToken = default);

    /// <summary>"Customer served": resolves the token's branch, organization and the serving counter's assigned user, then credits. A convenience so the queue controller need not join those itself.</summary>
    Task CreditTokenServedAsync(Guid tokenId, CancellationToken cancellationToken = default);
}

public class StaffSystemAwards : IStaffSystemAwards
{
    public const string WelfareRecordFiled = "Welfare record filed";
    public const string CustomerServed = "Customer served";
    public const string VisitorHosted = "Visitor hosted";

    private readonly QMgrDbContext _db;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly IStaffAlertService _alerts;
    private readonly ILogger<StaffSystemAwards> _logger;

    public StaffSystemAwards(QMgrDbContext db, IStaffPerformancePolicyService policy, IStaffAlertService alerts, ILogger<StaffSystemAwards> logger)
    {
        _db = db;
        _policy = policy;
        _alerts = alerts;
        _logger = logger;
    }

    public async Task CreditTokenServedAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _db.Tokens.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == tokenId)
                .Select(t => new
                {
                    t.BranchId,
                    OrganizationId = t.Branch!.OrganizationId,
                    ServedBy = t.Counter != null ? t.Counter.AssignedUserId : null,
                    t.DisplayNumber
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (token?.ServedBy == null) return;

            await CreditAsync(token.OrganizationId, token.BranchId, token.ServedBy.Value, CustomerServed,
                $"Served ticket {token.DisplayNumber}. Credited automatically.", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System award for token {TokenId} could not be resolved", tokenId);
        }
    }

    public async Task CreditAsync(Guid organizationId, Guid branchId, Guid userId, string parameterNameOrKindHint, string description, CancellationToken cancellationToken = default)
    {
        try
        {
            if (userId == Guid.Empty || organizationId == Guid.Empty || branchId == Guid.Empty) return;

            var policy = await _policy.GetAsync(organizationId, cancellationToken);
            if (!policy.SystemAwardsEnabled) return;

            var hint = parameterNameOrKindHint.Trim().ToLowerInvariant();
            var parameter = await _db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrganizationId == organizationId && p.IsActive && p.IsSystemSource && p.Kind != ParameterKind.Wellbeing
                            && p.Name.ToLower() == hint)
                .FirstOrDefaultAsync(cancellationToken);
            if (parameter == null) return;

            // The subject must be a real, active member of this organization; a deactivated account
            // or a SuperAdmin serving a counter on a demo tenant earns nothing.
            var subjectOk = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(u => u.Id == userId && u.OrganizationId == organizationId && u.IsActive
                               && u.Role.Code != Domain.Constants.RoleCodes.SuperAdmin, cancellationToken);
            if (!subjectOk) return;

            var points = Math.Abs(parameter.DefaultPoints ?? 1);
            if (parameter.MaxPointsPerEntry > 0) points = Math.Min(points, parameter.MaxPointsPerEntry);
            if (points == 0) return;

            if (parameter.MaxPointsPerPeriod is { } cap)
            {
                var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));
                var start = period.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var end = period.End.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var already = await _db.StaffPerformanceRecords.AsNoTracking()
                    .Where(r => r.SubjectUserId == userId && r.ParameterId == parameter.Id && r.Status == StaffRecordStatus.Final
                                && r.OccurredAt >= start && r.OccurredAt < end)
                    .SumAsync(r => r.Points ?? 0, cancellationToken);
                if (already + points > cap)
                {
                    _logger.LogDebug("System award \"{Parameter}\" for {UserId} skipped: period cap {Cap} reached", parameter.Name, userId, cap);
                    return;
                }
            }

            var record = new StaffPerformanceRecord
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                SubjectUserId = userId,
                ParameterId = parameter.Id,
                Outcome = parameter.Kind is ParameterKind.Attendance or ParameterKind.Duty ? DutyOutcome.Completed : DutyOutcome.NotApplicable,
                Points = parameter.Kind == ParameterKind.Conduct ? -points : points,
                Description = description.Length > 2000 ? description[..2000] : description,
                OccurredAt = DateTime.UtcNow,
                Source = RecordSource.System,
                Status = StaffRecordStatus.Final,
                Visibility = WelfareVisibility.Standard,
                LoggedByUserId = userId,
                CreatedBy = userId
            };
            _db.StaffPerformanceRecords.Add(record);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("System award: {Points} point(s) on \"{Parameter}\" credited to {UserId}", record.Points, parameter.Name, userId);

            await _alerts.NotifyScoreUpdatedAsync(organizationId, branchId, userId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System award \"{Hint}\" for {UserId} failed", parameterNameOrKindHint, userId);
        }
    }
}
