using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// The structure coverage report — every way the staff structure fails silently, made visible:
/// departments with no head, staff in no department, staff with no line manager, staff with no
/// appraisal or no observation this period, parameters nobody has logged against this period.
///
/// ONE computation, called from StaffStructureController (GET structure/coverage), from
/// StaffReportsController (the Coverage panel) and from the monthly administrator summary. It
/// exists as a class so those three cannot drift into three definitions of "uncovered".
/// Every query uses IgnoreQueryFilters plus the explicit organization, so it answers the same
/// inside a request and inside a Hangfire worker.
/// </summary>
public static class StaffCoverageBuilder
{
    public static async Task<StructureCoverageDto> BuildAsync(
        QMgrDbContext db,
        Guid organizationId,
        Guid branchId,
        PerformancePeriodDto period,
        IStaffPerformancePolicyService policy,
        CancellationToken ct = default)
    {
        var start = period.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = period.End.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var staff = await StaffLookups.BranchStaff(db, organizationId, branchId)
            .OrderBy(u => u.LastName).ThenBy(u => u.FirstName)
            .ToListAsync(ct);
        var staffIds = staff.Select(s => s.Id).ToList();

        var departments = await db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.IsActive && (d.BranchId == null || d.BranchId == branchId))
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .ToListAsync(ct);
        var departmentNames = await StaffLookups.LoadDepartmentNamesAsync(db, organizationId, ct);

        var parameters = await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);
        var observationIds = parameters.Where(p => p.Kind == ParameterKind.Observation).Select(p => p.Id).ToHashSet();

        var periodRecords = await db.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.BranchId == branchId
                        && r.Status == StaffRecordStatus.Final
                        && r.OccurredAt >= start && r.OccurredAt < end)
            .Select(r => new { r.SubjectUserId, r.ParameterId })
            .ToListAsync(ct);

        var appraised = await db.StaffAppraisals.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.BranchId == branchId && a.PeriodKey == period.Key && a.IsActive)
            .Select(a => a.SubjectUserId)
            .Distinct()
            .ToListAsync(ct);
        var appraisedSet = appraised.ToHashSet();

        var observed = periodRecords.Where(r => observationIds.Contains(r.ParameterId)).Select(r => r.SubjectUserId).ToHashSet();
        var parametersUsed = periodRecords.Select(r => r.ParameterId).ToHashSet();

        var memberCounts = staff
            .Where(u => u.DepartmentIds != null)
            .SelectMany(u => u.DepartmentIds!)
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var names = await StaffLookups.LoadNamesAsync(db,
            departments.Select(d => d.HeadUserId).Concat(departments.Select(d => d.DeputyHeadUserId)).Concat(staff.Select(s => (Guid?)s.LineManagerUserId)), ct);

        StaffMemberDto Member(User u) => StaffPerformanceMapping.ToDto(u, names, departmentNames);

        var teaching = staff.Where(u => policy.GroupFor(u.Role?.Code) == StaffGroup.TeachingStaff).ToList();

        return new StructureCoverageDto
        {
            PeriodKey = period.Key,
            DepartmentsWithoutHead = departments.Where(d => d.HeadUserId == null)
                .Select(d => StaffPerformanceMapping.ToDto(d, names, memberCounts.GetValueOrDefault(d.Id))).ToList(),
            StaffWithoutDepartment = staff.Where(u => u.DepartmentIds == null || u.DepartmentIds.Length == 0).Select(Member).ToList(),
            StaffWithoutLineManager = staff.Where(u => u.LineManagerUserId == null).Select(Member).ToList(),
            StaffWithoutAppraiserThisPeriod = staff.Where(u => !appraisedSet.Contains(u.Id)).Select(Member).ToList(),
            // Observation is a teaching-staff parameter; a bursar with no lesson observation is not a gap.
            StaffWithoutObservationThisPeriod = teaching.Where(u => !observed.Contains(u.Id)).Select(Member).ToList(),
            ParametersWithNoRecordsThisPeriod = parameters.Where(p => p.Kind != ParameterKind.Wellbeing && !parametersUsed.Contains(p.Id))
                .Select(StaffPerformanceMapping.ToDto).ToList()
        };
    }
}
