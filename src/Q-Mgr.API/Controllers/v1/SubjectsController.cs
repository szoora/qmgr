using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The subject catalogue (duty rota plan §5.2). Organization-wide, read by everybody on the staff because
/// "who teaches what" is operational (plan §13.5), written by <c>staff.structure.manage</c>. Seeded on the
/// first read with the Uganda lower-secondary set so a school sees a working catalogue before configuring
/// anything; retired, never deleted, because assignments, timetable lessons and lesson duties point at it.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff/subjects")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class SubjectsController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly ILogger<SubjectsController> _logger;

    public SubjectsController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        ILogger<SubjectsController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Every subject, with its live teacher count in this branch. No permission code: the catalogue carries no
    /// personal data, and a teacher's own "My teaching" and every timetable view read names from it.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<SubjectDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubjects(Guid branchId, [FromQuery] bool includeInactive = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        await SeedIfEmptyAsync(organizationId);

        var query = Db.Subjects.AsNoTracking().Where(s => s.OrganizationId == organizationId);
        if (!includeInactive) query = query.Where(s => s.IsActive);
        var subjects = await query.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();

        var departments = await DepartmentNamesAsync(organizationId);
        var counts = await Db.ClassTeacherAssignments.AsNoTracking()
            .Where(a => a.BranchId == branchId && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher && a.SubjectId != null)
            .GroupBy(a => a.SubjectId!.Value)
            .Select(g => new { SubjectId = g.Key, Teachers = g.Select(a => a.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.SubjectId, x => x.Teachers);

        return Ok(subjects.Select(s => ToDto(s, departments, counts.GetValueOrDefault(s.Id))).ToList());
    }

    [HttpPost]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(SubjectDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateSubject(Guid branchId, [FromBody] SaveSubjectRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        await SeedIfEmptyAsync(organizationId);
        var problem = await ValidateAsync(organizationId, request, excludeId: null);
        if (problem != null) return problem;

        var subject = new Subject { OrganizationId = organizationId, CreatedBy = CurrentUserId() };
        Apply(subject, request);
        Db.Subjects.Add(subject);
        try
        {
            await Db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // ux_subjects_org_code_active: two saves of the same code at once. One wins.
            return ConflictProblem("Code already in use", $"Another live subject already uses the code {subject.Code}.");
        }

        await Activity.RecordAsync(ActivityActions.SubjectSaved, nameof(Subject), subject.Id, null,
            $"Subject '{subject.Name}' ({subject.Code}) created", new { subject.Code, subject.DepartmentId }, branchId, organizationId);

        return CreatedAtAction(nameof(GetSubjects), new { branchId }, ToDto(subject, await DepartmentNamesAsync(organizationId), 0));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(SubjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSubject(Guid branchId, Guid id, [FromBody] SaveSubjectRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var subject = await Db.Subjects.FirstOrDefaultAsync(s => s.Id == id && s.OrganizationId == organizationId);
        if (subject == null) return NotFoundProblem("Subject not found");

        var problem = await ValidateAsync(organizationId, request, excludeId: id);
        if (problem != null) return problem;

        var before = new { subject.Name, subject.Code, subject.DepartmentId };
        Apply(subject, request);
        subject.UpdatedAt = DateTime.UtcNow;
        subject.UpdatedBy = CurrentUserId();
        try
        {
            await Db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictProblem("Code already in use", $"Another live subject already uses the code {subject.Code}.");
        }

        await Activity.RecordAsync(ActivityActions.SubjectSaved, nameof(Subject), subject.Id, null,
            $"Subject '{subject.Name}' ({subject.Code}) updated", new { Before = before, After = new { subject.Name, subject.Code, subject.DepartmentId } }, branchId, organizationId);

        return Ok(ToDto(subject, await DepartmentNamesAsync(organizationId), await TeacherCountAsync(branchId, subject.Id)));
    }

    /// <summary>
    /// Retire or reinstate. Retiring a subject with live teachers is refused: the assignments would keep
    /// pointing at a subject nobody can pick, and the timetable would keep placing it. End them first.
    /// </summary>
    [HttpPatch("{id:guid}/toggle")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(SubjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleSubject(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var subject = await Db.Subjects.FirstOrDefaultAsync(s => s.Id == id && s.OrganizationId == organizationId);
        if (subject == null) return NotFoundProblem("Subject not found");

        if (subject.IsActive)
        {
            var liveTeachers = await Db.ClassTeacherAssignments.CountAsync(a => a.OrganizationId == organizationId && a.SubjectId == id && a.EndedAt == null);
            if (liveTeachers > 0)
                return ConflictProblem("Subject still taught", $"{subject.Name} has {liveTeachers} live teaching assignment(s). End them on the class teachers page before retiring it.");
        }
        else if (await Db.Subjects.AnyAsync(s => s.OrganizationId == organizationId && s.IsActive && s.Code == subject.Code && s.Id != id))
        {
            return ConflictProblem("Code already in use", $"A live subject already uses the code {subject.Code}. Change one of them first.");
        }

        subject.IsActive = !subject.IsActive;
        subject.UpdatedAt = DateTime.UtcNow;
        subject.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.SubjectSaved, nameof(Subject), subject.Id, null,
            $"Subject '{subject.Name}' {(subject.IsActive ? "reinstated" : "retired")}", new { subject.IsActive }, branchId, organizationId);

        return Ok(ToDto(subject, await DepartmentNamesAsync(organizationId), await TeacherCountAsync(branchId, subject.Id)));
    }

    // ---------------------------------------------------------------------

    private Task SeedIfEmptyAsync(Guid organizationId) => SubjectDefaults.SeedIfEmptyAsync(Db, organizationId, _logger);

    private async Task<IActionResult?> ValidateAsync(Guid organizationId, SaveSubjectRequest request, Guid? excludeId)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequestProblem("Name required");
        if (string.IsNullOrWhiteSpace(request.Code)) return BadRequestProblem("Code required");
        var code = request.Code.Trim().ToUpperInvariant();
        if (code.Contains(':') || code.Contains(';') || code.Contains(','))
            return BadRequestProblem("Invalid code", "A code cannot contain ':', ';' or ',' — the staff import's Teaches column uses them as separators.");
        if (!string.IsNullOrWhiteSpace(request.Color) && !System.Text.RegularExpressions.Regex.IsMatch(request.Color.Trim(), "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$"))
            return BadRequestProblem("Invalid colour", "Use a hex colour such as #7a2847.");
        if (request.DepartmentId is { } departmentId
            && !await Db.Departments.AnyAsync(d => d.Id == departmentId && d.OrganizationId == organizationId))
            return BadRequestProblem("Department not found");
        if (await Db.Subjects.AnyAsync(s => s.OrganizationId == organizationId && s.IsActive && s.Code == code && s.Id != excludeId))
            return ConflictProblem("Code already in use", $"Another live subject already uses the code {code}.");
        return null;
    }

    private static void Apply(Subject subject, SaveSubjectRequest request)
    {
        subject.Name = request.Name.Trim();
        subject.Code = request.Code.Trim().ToUpperInvariant();
        subject.DepartmentId = request.DepartmentId;
        subject.Color = string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.Trim();
        subject.SortOrder = request.SortOrder;
    }

    private Task<int> TeacherCountAsync(Guid branchId, Guid subjectId)
        => Db.ClassTeacherAssignments.Where(a => a.BranchId == branchId && a.EndedAt == null && a.SubjectId == subjectId)
            .Select(a => a.UserId).Distinct().CountAsync();

    private static SubjectDto ToDto(Subject s, Dictionary<Guid, string> departments, int teachers) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Code = s.Code,
        DepartmentId = s.DepartmentId,
        DepartmentName = s.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null,
        Color = s.Color,
        SortOrder = s.SortOrder,
        IsActive = s.IsActive,
        TeacherCount = teachers
    };
}
