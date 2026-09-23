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
/// The parameter catalogue: what is measured, its points, weight, cap and rubric. Org-scoped like
/// WelfareCategory (one taxonomy across campuses), retired with IsActive, never deleted — a record
/// points at it. Seeded with the MoES set on the first read for a tenant, so a school sees a
/// working catalogue before anybody has configured anything; the seed is idempotent on "the table
/// is empty for this organization".
///
/// Wellbeing is forced unscored in <see cref="StaffPerformanceMapping.Apply"/>: no points, no
/// weight, whatever the client sent. The weight cap is the MET ceiling from policy: no single
/// parameter above N% of the total active weight, so one duty cannot dominate the composite.
/// </summary>
[ApiController]
[Route("api/v1/staff/parameters")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every write also carries its own [RequirePermission]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffParametersController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly ILogger<StaffParametersController> _logger;

    public StaffParametersController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        ILogger<StaffParametersController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Every parameter of the organization, active only unless asked otherwise. Any authenticated
    /// member of the module may read it: the log-a-record form, the recognition picker and the
    /// portal's breakdown all need the names.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<PerformanceParameterDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetParameters([FromQuery] bool includeInactive = false)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();

        await StaffParameterDefaults.SeedIfEmptyAsync(Db, _policy, organizationId.Value, _logger);
        await StaffParameterDefaults.EnsureSystemSourceAsync(Db, _policy, organizationId.Value, _logger);

        var query = Db.PerformanceParameters.AsNoTracking().Where(p => p.OrganizationId == organizationId.Value);
        if (!includeInactive) query = query.Where(p => p.IsActive);

        var parameters = await query.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync();
        return Ok(parameters.Select(StaffPerformanceMapping.ToDto).ToList());
    }

    [HttpPost]
    [RequirePermission(Permissions.StaffParametersManage)]
    [ProducesResponseType(typeof(PerformanceParameterDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateParameter([FromBody] SavePerformanceParameterRequest request)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();

        var validation = await ValidateAsync(organizationId.Value, request, excludeId: null);
        if (validation != null) return validation;

        if (await Db.PerformanceParameters.AnyAsync(p => p.OrganizationId == organizationId.Value && p.IsActive && p.Name.ToLower() == request.Name.Trim().ToLower()))
            return ConflictProblem($"A parameter named '{request.Name.Trim()}' already exists");

        var parameter = new PerformanceParameter { OrganizationId = organizationId.Value, CreatedBy = CurrentUserId() };
        StaffPerformanceMapping.Apply(parameter, request);
        Db.PerformanceParameters.Add(parameter);
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.ParameterSaved, nameof(PerformanceParameter), parameter.Id, null,
            $"Parameter '{parameter.Name}' created ({parameter.Kind}, weight {parameter.Weight:0.##})",
            new { parameter.Kind, parameter.Weight, parameter.DefaultPoints, parameter.MaxPointsPerEntry, parameter.MaxPointsPerPeriod, parameter.DefaultVisibility });

        return CreatedAtAction(nameof(GetParameters), null, StaffPerformanceMapping.ToDto(parameter));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.StaffParametersManage)]
    [ProducesResponseType(typeof(PerformanceParameterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateParameter(Guid id, [FromBody] SavePerformanceParameterRequest request)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();

        var parameter = await Db.PerformanceParameters.FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == organizationId.Value);
        if (parameter == null) return NotFoundProblem("Parameter not found");

        var validation = await ValidateAsync(organizationId.Value, request, excludeId: id);
        if (validation != null) return validation;

        // The kind decides the sign rule on every record already filed against it. Changing it
        // would silently re-interpret history, so it is fixed once a record exists.
        if (parameter.Kind != request.Kind && await Db.StaffPerformanceRecords.AnyAsync(r => r.ParameterId == id))
            return BadRequestProblem("The kind of a parameter cannot change once records exist",
                "Records have already been logged against this parameter. Retire it and create a new one instead.");

        var before = new { parameter.Kind, parameter.Weight, parameter.DefaultPoints, parameter.MaxPointsPerEntry, parameter.MaxPointsPerPeriod, parameter.DefaultVisibility, parameter.AppliesToGroup };
        StaffPerformanceMapping.Apply(parameter, request);
        parameter.UpdatedAt = DateTime.UtcNow;
        parameter.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.ParameterSaved, nameof(PerformanceParameter), parameter.Id, null,
            $"Parameter '{parameter.Name}' updated",
            new { Before = before, After = new { parameter.Kind, parameter.Weight, parameter.DefaultPoints, parameter.MaxPointsPerEntry, parameter.MaxPointsPerPeriod, parameter.DefaultVisibility, parameter.AppliesToGroup } });

        return Ok(StaffPerformanceMapping.ToDto(parameter));
    }

    /// <summary>Retire or reinstate. A retired parameter keeps its records and drops out of forms and scoring.</summary>
    [HttpPatch("{id:guid}/toggle")]
    [RequirePermission(Permissions.StaffParametersManage)]
    [ProducesResponseType(typeof(PerformanceParameterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleParameter(Guid id)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();

        var parameter = await Db.PerformanceParameters.FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == organizationId.Value);
        if (parameter == null) return NotFoundProblem("Parameter not found");

        if (!parameter.IsActive)
        {
            // Reinstating puts its weight back into the total: the cap applies exactly as on save.
            var capProblem = await WeightCapProblemAsync(organizationId.Value, parameter.Weight, parameter.Id);
            if (capProblem != null) return capProblem;
        }
        else
        {
            // Retiring shrinks the total, which raises every other parameter's share. Refuse a retirement
            // that would push the heaviest remaining parameter over the cap, naming it.
            var policy = await _policy.GetAsync(organizationId.Value);
            var remaining = await Db.PerformanceParameters.AsNoTracking()
                .Where(p => p.OrganizationId == organizationId.Value && p.IsActive && p.Id != parameter.Id && p.Weight > 0)
                .Select(p => new { p.Name, p.Weight })
                .ToListAsync();
            var total = remaining.Sum(p => p.Weight);
            if (remaining.Count > 1 && total > 0)
            {
                var heaviest = remaining.OrderByDescending(p => p.Weight).First();
                var share = heaviest.Weight / total * 100m;
                if (share > policy.MaxParameterWeightPercent)
                    return BadRequestProblem($"Retiring '{parameter.Name}' would make '{heaviest.Name}' weigh too much",
                        $"'{heaviest.Name}' would become {share:0}% of the total, above the policy's {policy.MaxParameterWeightPercent}% cap. Lower its weight first.");
            }
        }

        parameter.IsActive = !parameter.IsActive;
        parameter.UpdatedAt = DateTime.UtcNow;
        parameter.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.ParameterSaved, nameof(PerformanceParameter), parameter.Id, null,
            $"Parameter '{parameter.Name}' {(parameter.IsActive ? "reinstated" : "retired")}", new { parameter.IsActive });

        return Ok(StaffPerformanceMapping.ToDto(parameter));
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private async Task<IActionResult?> ValidateAsync(Guid organizationId, SavePerformanceParameterRequest request, Guid? excludeId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequestProblem("Parameter name is required");
        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.DefaultVisibility))
            return BadRequestProblem("Unrecognised scoring method or visibility");
        if (request.Weight < 0)
            return BadRequestProblem("Weight cannot be negative", "Use 0 for a parameter that is evidence only and never scored.");
        if (request.MaxPointsPerEntry < 0 || request.MaxPointsPerPeriod is < 0)
            return BadRequestProblem("Point caps cannot be negative");
        if (request.Kind != ParameterKind.Wellbeing && request.DefaultPoints is { } dp && Math.Abs(dp) > Math.Max(request.MaxPointsPerEntry, 0))
            return BadRequestProblem("Default points exceed the per-entry cap", $"Default points of {dp} cannot exceed the maximum of {request.MaxPointsPerEntry} per record.");
        if (request.Kind == ParameterKind.Observation && request.RatingScale is { } scale && (scale < 2 || scale > 10))
            return BadRequestProblem("An observation scale must have between 2 and 10 levels");
        if (request.Kind == ParameterKind.Observation && request.Rubric.Count > 0 && request.Rubric.Count != (request.RatingScale is > 1 ? request.RatingScale : 4))
            return BadRequestProblem("The rubric must have one descriptor per level", $"The scale has {(request.RatingScale is > 1 ? request.RatingScale : 4)} levels but {request.Rubric.Count} descriptors were given.");

        switch (request.Kind)
        {
            case ParameterKind.Contribution or ParameterKind.Recognition when request.DefaultPoints is < 0:
                return BadRequestProblem($"{request.Kind} points must be zero or positive");
            case ParameterKind.Conduct when request.DefaultPoints is > 0:
                return BadRequestProblem("Conduct points must be zero or negative");
        }

        // An offset points at an active Attendance or Duty parameter of the same organization, never
        // at itself: "a record here is a recovered occasion there" means nothing for any other kind.
        if (request.OffsetsParameterId is { } offsetsId)
        {
            if (offsetsId == excludeId) return BadRequestProblem("A parameter cannot offset itself");
            if (request.Kind is ParameterKind.Wellbeing or ParameterKind.Attendance or ParameterKind.Duty)
                return BadRequestProblem("Only a Contribution, Recognition, Conduct or Observation parameter can offset another",
                    "An attendance parameter records its own Recovered outcome; use that instead.");
            var target = await Db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == offsetsId && p.OrganizationId == organizationId);
            if (target == null) return BadRequestProblem("The parameter to offset was not found");
            if (target.Kind is not (ParameterKind.Attendance or ParameterKind.Duty))
                return BadRequestProblem($"'{target.Name}' is not an Attendance or Duty parameter", "A recovery can only offset a missed attendance or duty.");
        }

        var weight = request.Kind == ParameterKind.Wellbeing ? 0 : request.Weight;
        return await WeightCapProblemAsync(organizationId, weight, excludeId);
    }

    /// <summary>
    /// The MET ceiling. Wellbeing carries no weight and is exempt; a lone weighted parameter is
    /// necessarily 100% of the total and is allowed, or the first parameter could never be saved.
    /// Checked on save AND on reinstating a retired parameter — before 2026-09-17 reinstating one (or
    /// retiring the others) could leave a single parameter far above the cap with nothing refusing it.
    /// </summary>
    private async Task<IActionResult?> WeightCapProblemAsync(Guid organizationId, decimal weight, Guid? excludeId)
    {
        if (weight <= 0) return null;

        var policy = await _policy.GetAsync(organizationId);
        var othersWeight = await Db.PerformanceParameters.IgnoreQueryFilters()
            .Where(p => p.OrganizationId == organizationId && p.IsActive && p.Id != excludeId)
            .SumAsync(p => p.Weight);
        if (othersWeight <= 0) return null;

        var share = weight / (othersWeight + weight) * 100m;
        if (share > policy.MaxParameterWeightPercent)
            return BadRequestProblem("This parameter would weigh too much",
                $"A weight of {weight:0.##} would be {share:0}% of the total, and the scoring policy caps any one parameter at {policy.MaxParameterWeightPercent}%. Lower it, or raise the others.");
        return null;
    }
}
