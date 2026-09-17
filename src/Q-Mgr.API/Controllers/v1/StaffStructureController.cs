using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Departments, the staff directory, who reports to whom, and the coverage report that surfaces
/// the silent failures of all three. Departments are org-scoped with an optional branch (a multi-
/// campus tenant with one Maths department) and are FK'd by <c>User.DepartmentIds</c>, so they are
/// retired, never deleted.
///
/// The directory is the one place the module answers "who are the staff of this branch": every
/// active user of the organization assigned to the branch or to no branch, excluding the platform
/// SuperAdmin, narrowed by <see cref="IStaffScopeService"/> so a head of department sees their own
/// department and the page can say so.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff/structure")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action also carries its own [RequirePermission]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffStructureController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly IStaffScoringService _scoring;
    private readonly INotificationService _notifications;
    private readonly ILogger<StaffStructureController> _logger;

    public StaffStructureController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        IStaffScoringService scoring,
        INotificationService notifications,
        ILogger<StaffStructureController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _scoring = scoring;
        _notifications = notifications;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Departments
    // ---------------------------------------------------------------------

    [HttpGet("departments")]
    [RequirePermission(Permissions.StaffRecordsView)]
    [ProducesResponseType(typeof(List<DepartmentDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDepartments(Guid branchId, [FromQuery] bool includeInactive = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var query = Db.Departments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && (d.BranchId == null || d.BranchId == branchId));
        if (!includeInactive) query = query.Where(d => d.IsActive);

        var departments = await query.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync();
        var counts = await MemberCountsAsync(organizationId, departments.Select(d => d.Id));
        var names = await BuildNamesAsync(departments.SelectMany(d => new[] { d.HeadUserId, d.DeputyHeadUserId }));

        return Ok(departments.Select(d => StaffPerformanceMapping.ToDto(d, names, counts.GetValueOrDefault(d.Id))).ToList());
    }

    [HttpPost("departments")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateDepartment(Guid branchId, [FromBody] SaveDepartmentRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var validation = await ValidateDepartmentAsync(organizationId, branchId, request, excludeId: null);
        if (validation != null) return validation;

        var department = new Department
        {
            OrganizationId = organizationId,
            BranchId = request.BranchId,
            Name = request.Name.Trim(),
            Code = request.Code.Trim().ToUpperInvariant(),
            HeadUserId = request.HeadUserId,
            DeputyHeadUserId = request.DeputyHeadUserId,
            SortOrder = request.SortOrder,
            CreatedBy = CurrentUserId()
        };
        Db.Departments.Add(department);
        await Db.SaveChangesAsync();

        if (department.HeadUserId is { } newHead)
            await NotifyProfileChangedAsync(newHead, organizationId, branchId, "You are now head of department", $"You have been made head of {department.Name}. Its staff now appear on your staff pages.");
        if (department.DeputyHeadUserId is { } newDeputy && newDeputy != department.HeadUserId)
            await NotifyProfileChangedAsync(newDeputy, organizationId, branchId, "You are now deputy head of department", $"You have been made deputy head of {department.Name}. Its staff now appear on your staff pages.");

        var names = await BuildNamesAsync(new[] { department.HeadUserId, department.DeputyHeadUserId });
        await Activity.RecordAsync(ActivityActions.StructureDepartmentSaved, nameof(Department), department.Id, null,
            $"Department '{department.Name}' ({department.Code}) created" + (department.HeadUserId.HasValue ? $", head {names[department.HeadUserId]}" : ""),
            new { department.Code, department.HeadUserId, department.DeputyHeadUserId, department.BranchId }, branchId, organizationId);

        return CreatedAtAction(nameof(GetDepartments), new { branchId }, StaffPerformanceMapping.ToDto(department, names, 0));
    }

    [HttpPut("departments/{id:guid}")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDepartment(Guid branchId, Guid id, [FromBody] SaveDepartmentRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var department = await Db.Departments.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == organizationId);
        if (department == null) return NotFoundProblem("Department not found");

        var validation = await ValidateDepartmentAsync(organizationId, branchId, request, excludeId: id);
        if (validation != null) return validation;

        var before = new { department.Name, department.Code, department.HeadUserId, department.DeputyHeadUserId, department.BranchId };
        department.Name = request.Name.Trim();
        department.Code = request.Code.Trim().ToUpperInvariant();
        department.BranchId = request.BranchId;
        department.HeadUserId = request.HeadUserId;
        department.DeputyHeadUserId = request.DeputyHeadUserId;
        department.SortOrder = request.SortOrder;
        department.UpdatedAt = DateTime.UtcNow;
        department.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();

        var names = await BuildNamesAsync(new[] { department.HeadUserId, department.DeputyHeadUserId, before.HeadUserId });
        var headChanged = before.HeadUserId != department.HeadUserId;
        await Activity.RecordAsync(ActivityActions.StructureDepartmentSaved, nameof(Department), department.Id, department.HeadUserId,
            $"Department '{department.Name}' updated" + (headChanged ? $"; head is now {(department.HeadUserId.HasValue ? names[department.HeadUserId] : "nobody")}" : ""),
            new { Before = before, After = new { department.Name, department.Code, department.HeadUserId, department.DeputyHeadUserId, department.BranchId } },
            branchId, organizationId);

        if (headChanged && department.HeadUserId.HasValue)
            await NotifyProfileChangedAsync(department.HeadUserId.Value, organizationId, branchId, "You are now head of department", $"You have been made head of {department.Name}. Its staff now appear on your staff pages.");
        // The deputy's scope changes exactly as the head's does (StaffScopeService resolves both), so they
        // are told the same way. Until 2026-09-17 a deputy was assigned in silence.
        if (before.DeputyHeadUserId != department.DeputyHeadUserId && department.DeputyHeadUserId is { } deputy && deputy != department.HeadUserId)
            await NotifyProfileChangedAsync(deputy, organizationId, branchId, "You are now deputy head of department", $"You have been made deputy head of {department.Name}. Its staff now appear on your staff pages.");
        // The people a head change affects most: the department's own staff, whose head (and so whose
        // appraiser, by default) has changed.
        if (headChanged)
        {
            var headName = department.HeadUserId.HasValue ? names[department.HeadUserId] : "nobody";
            var members = await Db.Users.AsNoTracking()
                .Where(u => u.OrganizationId == organizationId && u.IsActive && u.DepartmentIds != null && u.DepartmentIds.Contains(department.Id)
                            && u.Id != department.HeadUserId && u.Id != before.HeadUserId)
                .Select(u => u.Id)
                .ToListAsync();
            foreach (var member in members)
                await NotifyProfileChangedAsync(member, organizationId, branchId, $"{department.Name} has a new head",
                    $"The head of {department.Name} is now {headName}.");
        }

        var counts = await MemberCountsAsync(organizationId, new[] { department.Id });
        return Ok(StaffPerformanceMapping.ToDto(department, names, counts.GetValueOrDefault(department.Id)));
    }

    /// <summary>Retire or reinstate. A retired department keeps its members' history; the scope service ignores it, so a head of a retired department sees nobody through it.</summary>
    [HttpPatch("departments/{id:guid}/toggle")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(DepartmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleDepartment(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var department = await Db.Departments.FirstOrDefaultAsync(d => d.Id == id && d.OrganizationId == organizationId);
        if (department == null) return NotFoundProblem("Department not found");

        department.IsActive = !department.IsActive;
        department.UpdatedAt = DateTime.UtcNow;
        department.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.StructureDepartmentSaved, nameof(Department), department.Id, null,
            $"Department '{department.Name}' {(department.IsActive ? "reinstated" : "retired")}", new { department.IsActive }, branchId, organizationId);

        var names = await BuildNamesAsync(new[] { department.HeadUserId, department.DeputyHeadUserId });
        var counts = await MemberCountsAsync(organizationId, new[] { department.Id });
        return Ok(StaffPerformanceMapping.ToDto(department, names, counts.GetValueOrDefault(department.Id)));
    }

    // ---------------------------------------------------------------------
    // Members (the directory)
    // ---------------------------------------------------------------------

    /// <summary>
    /// The staff of the branch as this caller may see them, with this period's band and the date of
    /// the last record about each. LastRecordAt is computed over the rungs the caller can read, so a
    /// head without the Confidential rung does not learn that a confidential record exists from a date.
    /// </summary>
    [HttpGet("members")]
    [RequirePermission(Permissions.StaffRecordsView)]
    [ProducesResponseType(typeof(StaffDirectoryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMembers(Guid branchId, [FromQuery] string? period = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var periodDto = _policy.FindPeriod(policy, period) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));

        var staffQuery = await StaffScope.ApplyToStaffAsync(BranchStaffQuery(organizationId, branchId), branchId);
        var staff = await staffQuery.OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ToListAsync();
        var ids = staff.Select(u => u.Id).ToList();

        var scores = (await _scoring.ComputeBranchAsync(organizationId, branchId, periodDto)).ToDictionary(s => s.SubjectUserId);
        var levels = await VisibleLevelsAsync();
        var lastRecord = await Db.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.BranchId == branchId && ids.Contains(r.SubjectUserId) && r.Status == StaffRecordStatus.Final && levels.Contains(r.Visibility))
            .GroupBy(r => r.SubjectUserId)
            .Select(g => new { g.Key, Last = g.Max(r => r.CreatedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.Last);

        var names = await BuildNamesAsync(staff.Select(u => u.LineManagerUserId));
        var departmentNames = await DepartmentNamesAsync(organizationId);

        return Ok(new StaffDirectoryDto
        {
            Items = staff.Select(u => StaffPerformanceMapping.ToDto(u, names, departmentNames,
                scores.GetValueOrDefault(u.Id), lastRecord.TryGetValue(u.Id, out var at) ? at : null)).ToList(),
            ScopedToDepartments = (await StaffScope.GetScopedDepartmentNamesAsync()).ToList()
        });
    }

    /// <summary>
    /// Sets a person's departments and line manager. The person is told: a change to who supervises
    /// them changes who is alerted about their records, and they should know that before the first
    /// alert goes out.
    /// </summary>
    [HttpPut("members/{userId:guid}")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMember(Guid branchId, Guid userId, [FromBody] UpdateStaffStructureRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var user = await Db.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == organizationId && u.Role.Code != RoleCodes.SuperAdmin);
        if (user == null) return StaffMemberNotFound();

        var departmentIds = (request.DepartmentIds ?? new List<Guid>()).Distinct().ToList();
        if (departmentIds.Count > 0)
        {
            var known = await Db.Departments.Where(d => d.OrganizationId == organizationId && departmentIds.Contains(d.Id)).CountAsync();
            if (known != departmentIds.Count)
                return BadRequestProblem("One of the departments was not found", "Every department must belong to this organization.");
        }

        if (request.LineManagerUserId is { } lm)
        {
            if (lm == userId)
                return BadRequestProblem("A person cannot be their own line manager");
            var managerOk = await Db.Users.AnyAsync(u => u.Id == lm && u.OrganizationId == organizationId && u.IsActive && u.Role.Code != RoleCodes.SuperAdmin);
            if (!managerOk)
                return BadRequestProblem("The line manager was not found", "The line manager must be an active member of staff in this organization.");
        }

        var before = new { DepartmentIds = user.DepartmentIds?.ToList() ?? new List<Guid>(), user.LineManagerUserId };
        var lineManagerChanged = before.LineManagerUserId != request.LineManagerUserId;
        var departmentsChanged = !before.DepartmentIds.OrderBy(x => x).SequenceEqual(departmentIds.OrderBy(x => x));

        user.DepartmentIds = departmentIds.Count == 0 ? null : departmentIds.ToArray();
        user.LineManagerUserId = request.LineManagerUserId;
        user.UpdatedAt = DateTime.UtcNow;
        user.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();

        var departmentNames = await DepartmentNamesAsync(organizationId);
        var names = await BuildNamesAsync(new[] { user.LineManagerUserId, before.LineManagerUserId });
        var deptText = departmentIds.Count == 0 ? "no department" : string.Join(", ", departmentIds.Select(id => departmentNames.GetValueOrDefault(id, "?")));
        var lmText = user.LineManagerUserId.HasValue ? names[user.LineManagerUserId] : "nobody";

        await Activity.RecordAsync(ActivityActions.StructureMemberUpdated, nameof(User), user.Id, user.Id,
            $"{StaffPerformanceMapping.FullName(user)}: departments {deptText}; line manager {lmText}",
            new { Before = before, After = new { DepartmentIds = departmentIds, user.LineManagerUserId } }, branchId, organizationId);

        if (departmentsChanged || lineManagerChanged)
        {
            var what = new List<string>();
            if (departmentsChanged) what.Add($"your department is now {deptText}");
            if (lineManagerChanged) what.Add($"your line manager is now {lmText}");
            await NotifyProfileChangedAsync(user.Id, organizationId, branchId, "Your staff profile changed",
                char.ToUpperInvariant(what[0][0]) + string.Join("; ", what)[1..] + ".");
        }

        return Ok(StaffPerformanceMapping.ToDto(user, names, departmentNames));
    }

    // ---------------------------------------------------------------------
    // Coverage
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every way the structure fails silently, made visible (plan §4): departments with no head,
    /// staff with no department or line manager, staff with no appraisal or no observed lesson this
    /// period, parameters nobody has logged against this period. The staff-side twin of
    /// class-teachers/coverage. Unscoped by design — it is the administrator's list.
    /// </summary>
    [HttpGet("coverage")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(StructureCoverageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCoverage(Guid branchId, [FromQuery] string? period = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var periodDto = _policy.FindPeriod(policy, period) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));

        // ONE computation of "uncovered", shared with the reports page and the monthly summary.
        return Ok(await StaffCoverageBuilder.BuildAsync(Db, organizationId, branchId, periodDto, _policy));
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>The staff of a branch, by the module's one definition (StaffLookups), so the directory and a report's denominator agree.</summary>
    private IQueryable<User> BranchStaffQuery(Guid organizationId, Guid branchId) => StaffLookups.BranchStaff(Db, organizationId, branchId);

    private async Task<Dictionary<Guid, int>> MemberCountsAsync(Guid organizationId, IEnumerable<Guid> departmentIds)
    {
        var ids = departmentIds.ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, int>();

        var memberships = await Db.Users.AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive && u.DepartmentIds != null && u.DepartmentIds.Any(id => ids.Contains(id)))
            .Select(u => u.DepartmentIds!)
            .ToListAsync();

        return memberships.SelectMany(m => m).Where(ids.Contains).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
    }

    private async Task<IActionResult?> ValidateDepartmentAsync(Guid organizationId, Guid branchId, SaveDepartmentRequest request, Guid? excludeId)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequestProblem("Department name is required");
        if (string.IsNullOrWhiteSpace(request.Code)) return BadRequestProblem("Department code is required", "A short stable code such as MATH; the staff import uses it to place people.");
        if (request.Code.Trim().Length > 20) return BadRequestProblem("Department code is too long", "20 characters at most.");
        if (request.HeadUserId.HasValue && request.HeadUserId == request.DeputyHeadUserId)
            return BadRequestProblem("The head and deputy head must be different people");

        if (request.BranchId.HasValue && !await Db.Branches.AnyAsync(b => b.Id == request.BranchId.Value && b.OrganizationId == organizationId))
            return BadRequestProblem("The branch was not found");

        var code = request.Code.Trim().ToUpperInvariant();
        if (await Db.Departments.AnyAsync(d => d.OrganizationId == organizationId && d.Id != excludeId && d.Code.ToUpper() == code))
            return ConflictProblem($"A department with code '{code}' already exists");

        foreach (var (who, id) in new[] { ("head", request.HeadUserId), ("deputy head", request.DeputyHeadUserId) })
        {
            if (id == null) continue;
            var ok = await Db.Users.AnyAsync(u => u.Id == id.Value && u.OrganizationId == organizationId && u.IsActive && u.Role.Code != RoleCodes.SuperAdmin);
            if (!ok) return BadRequestProblem($"The {who} was not found", $"The {who} must be an active member of staff in this organization.");
        }

        return null;
    }

    private async Task NotifyProfileChangedAsync(Guid userId, Guid organizationId, Guid branchId, string title, string message)
    {
        try
        {
            await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = userId,
                OrganizationId = organizationId,
                BranchId = branchId,
                Title = title,
                Message = message,
                Type = NotificationType.StaffPerformance,
                Priority = NotificationPriority.Normal,
                Channels = NotificationChannel.InApp | NotificationChannel.Email,
                EventKey = NotificationEventKeys.StaffProfileChanged,
                ActionUrl = "/portal",
                IconClass = "diagram-3"
            });
        }
        catch (Exception ex)
        {
            // The structure change is committed; a notification that did not land is a degraded success.
            _logger.LogError(ex, "Profile-changed notification to {UserId} failed", userId);
        }
    }
}
