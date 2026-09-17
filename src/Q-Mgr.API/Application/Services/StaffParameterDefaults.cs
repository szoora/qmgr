using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Entities.Staff;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// The one place the default parameter catalogue reaches a tenant's table. Two jobs, both idempotent:
///
///  - <see cref="SeedIfEmptyAsync"/>: a tenant with no parameters at all gets the MoES set, with the
///    offsets it names (Lesson Recovery → Lesson Attendance) linked in the same save. A tenant that
///    has retired every parameter is NOT re-seeded — "no active parameters" is a choice.
///  - <see cref="EnsureSystemSourceAsync"/>: the automatic-credit parameters ("Welfare record filed",
///    "Customer served", "Visitor hosted", "Positive feedback") are inserted when missing BY NAME,
///    active or not. Until 2026-09-17 nothing seeded them and the award matched by exact name, so a
///    tenant that switched SystemAwardsEnabled on got nothing at all until somebody guessed the
///    names. Called on the first read of the catalogue and whenever the policy switches awards on.
/// </summary>
public static class StaffParameterDefaults
{
    public static async Task SeedIfEmptyAsync(QMgrDbContext db, IStaffPerformancePolicyService policy, Guid organizationId, ILogger logger)
    {
        if (await db.PerformanceParameters.IgnoreQueryFilters().AnyAsync(p => p.OrganizationId == organizationId)) return;

        var created = new Dictionary<string, PerformanceParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in policy.DefaultParameters())
        {
            var parameter = new PerformanceParameter { OrganizationId = organizationId };
            StaffPerformanceMapping.Apply(parameter, request);
            db.PerformanceParameters.Add(parameter);
            created[parameter.Name] = parameter;
        }
        foreach (var (from, to) in policy.DefaultParameterOffsets())
            if (created.TryGetValue(from, out var f) && created.TryGetValue(to, out var t)) f.OffsetsParameterId = t.Id;

        try
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded {Count} default performance parameters for organization {OrganizationId}", created.Count, organizationId);
        }
        catch (DbUpdateException ex)
        {
            // Two first reads racing: the other one won. The catalogue is there either way.
            logger.LogWarning(ex, "Default parameter seed for {OrganizationId} collided with a concurrent seed; continuing", organizationId);
            db.ChangeTracker.Clear();
        }
    }

    public static async Task<int> EnsureSystemSourceAsync(QMgrDbContext db, IStaffPerformancePolicyService policy, Guid organizationId, ILogger logger)
    {
        var existing = (await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrganizationId == organizationId)
                .Select(p => p.Name)
                .ToListAsync())
            .Select(n => n.Trim().ToLowerInvariant())
            .ToHashSet();

        var added = 0;
        foreach (var request in policy.DefaultParameters().Where(r => r.IsSystemSource))
        {
            if (existing.Contains(request.Name.Trim().ToLowerInvariant())) continue;
            var parameter = new PerformanceParameter { OrganizationId = organizationId };
            StaffPerformanceMapping.Apply(parameter, request);
            db.PerformanceParameters.Add(parameter);
            added++;
        }
        if (added == 0) return 0;

        try
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Added {Count} automatic-credit parameter(s) for organization {OrganizationId}", added, organizationId);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Automatic-credit parameter seed for {OrganizationId} collided with a concurrent seed; continuing", organizationId);
            db.ChangeTracker.Clear();
            return 0;
        }
        return added;
    }
}
