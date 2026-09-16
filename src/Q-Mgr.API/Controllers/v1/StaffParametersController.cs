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
[RequireModule(ModuleCodes.StaffPerformance)]
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

        await SeedDefaultsIfEmptyAsync(organizationId.Value);

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

        var before = new { parameter.Kind, parameter.Weight, parameter.DefaultPoints, parameter.MaxPointsPerEntry, parameter.MaxPointsPerPeriod, parameter.DefaultVisibility, parameter.AppliesTo };
        StaffPerformanceMapping.Apply(parameter, request);
        parameter.UpdatedAt = DateTime.UtcNow;
        parameter.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.ParameterSaved, nameof(PerformanceParameter), parameter.Id, null,
            $"Parameter '{parameter.Name}' updated",
            new { Before = before, After = new { parameter.Kind, parameter.Weight, parameter.DefaultPoints, parameter.MaxPointsPerEntry, parameter.MaxPointsPerPeriod, parameter.DefaultVisibility, parameter.AppliesTo } });

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

        parameter.IsActive = !parameter.IsActive;
        parameter.UpdatedAt = DateTime.UtcNow;
        parameter.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.ParameterSaved, nameof(PerformanceParameter), parameter.Id, null,
            $"Parameter '{parameter.Name}' {(parameter.IsActive ? "reinstated" : "retired")}", new { parameter.IsActive });

        return Ok(StaffPerformanceMapping.ToDto(parameter));
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Inserts the MoES default set when the organization has no parameters at all. Idempotent on
    /// that emptiness check; a tenant that has retired every parameter is NOT re-seeded, because
    /// "no active parameters" is a choice and "no parameters" is a fresh install.
    /// </summary>
    private async Task SeedDefaultsIfEmptyAsync(Guid organizationId)
    {
        if (await Db.PerformanceParameters.IgnoreQueryFilters().AnyAsync(p => p.OrganizationId == organizationId)) return;

        foreach (var request in _policy.DefaultParameters())
        {
            var parameter = new PerformanceParameter { OrganizationId = organizationId };
            StaffPerformanceMapping.Apply(parameter, request);
            Db.PerformanceParameters.Add(parameter);
        }

        try
        {
            await Db.SaveChangesAsync();
            _logger.LogInformation("Seeded {Count} default performance parameters for organization {OrganizationId}", _policy.DefaultParameters().Count, organizationId);
        }
        catch (DbUpdateException ex)
        {
            // Two first reads racing: the other one won. The catalogue is there either way.
            _logger.LogWarning(ex, "Default parameter seed for {OrganizationId} collided with a concurrent seed; continuing", organizationId);
            Db.ChangeTracker.Clear();
        }
    }

    private async Task<IActionResult?> ValidateAsync(Guid organizationId, SavePerformanceParameterRequest request, Guid? excludeId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequestProblem("Parameter name is required");
        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.AppliesTo) || !Enum.IsDefined(request.DefaultVisibility))
            return BadRequestProblem("Unrecognised kind, staff group or visibility");
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

        // The MET ceiling. Wellbeing carries no weight and is exempt; a lone weighted parameter is
        // necessarily 100% of the total and is allowed, or the first parameter could never be saved.
        var weight = request.Kind == ParameterKind.Wellbeing ? 0 : request.Weight;
        if (weight > 0)
        {
            var policy = await _policy.GetAsync(organizationId);
            var othersWeight = await Db.PerformanceParameters.IgnoreQueryFilters()
                .Where(p => p.OrganizationId == organizationId && p.IsActive && p.Id != excludeId)
                .SumAsync(p => p.Weight);
            if (othersWeight > 0)
            {
                var share = weight / (othersWeight + weight) * 100m;
                if (share > policy.MaxParameterWeightPercent)
                    return BadRequestProblem("This parameter would weigh too much",
                        $"A weight of {weight:0.##} would be {share:0}% of the total, and the scoring policy caps any one parameter at {policy.MaxParameterWeightPercent}%. Lower it, or raise the others.");
            }
        }

        return null;
    }
}
