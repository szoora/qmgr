using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// The "Meeting Attendance" parameter a meeting's register scores against — found, or seeded once. One copy, because
/// the programme import and "Give this a register" (calendar) both make meetings, and two seeders racing would make two
/// parameters of one name.
/// </summary>
public static class MeetingParameter
{
    public const string Name = "Meeting Attendance";

    public static async Task<Guid> EnsureAsync(QMgrDbContext db, Guid organizationId, ILogger logger, CancellationToken ct = default)
    {
        var existing = await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.Name.ToLower() == Name.ToLower())
            .OrderByDescending(p => p.IsActive).Select(p => new { p.Id }).FirstOrDefaultAsync(ct);
        if (existing != null) return existing.Id;

        var maxSort = await db.PerformanceParameters.IgnoreQueryFilters().Where(p => p.OrganizationId == organizationId).MaxAsync(p => (int?)p.SortOrder, ct) ?? 0;
        var parameter = new PerformanceParameter { OrganizationId = organizationId };
        StaffPerformanceMapping.Apply(parameter, new SavePerformanceParameterRequest
        {
            Name = Name,
            Kind = ParameterKind.Attendance,
            DefaultPoints = 1,
            MaxPointsPerEntry = 1,
            Weight = 1,
            Purpose = "Attendance at staff, departmental and committee meetings, from the register taken by the named recorder.",
            Color = "#3f8a80",
            SortOrder = maxSort + 1
        });
        db.PerformanceParameters.Add(parameter);
        try
        {
            await db.SaveChangesAsync(ct);
            return parameter.Id;
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "{Parameter} seed for {OrganizationId} collided with a concurrent seed; reading it back", Name, organizationId);
            db.Entry(parameter).State = EntityState.Detached;
            return await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrganizationId == organizationId && p.Name.ToLower() == Name.ToLower())
                .Select(p => p.Id).FirstAsync(ct);
        }
    }
}
