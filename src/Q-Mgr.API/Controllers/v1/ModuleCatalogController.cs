using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The platform's own editor for the module catalog: what each module is called, what it says it
/// does, and what it costs.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed, the catalog had exactly one writer, <c>DbSeeder.SeedModulesAsync</c>, and it
/// only ever inserted rows that were missing. Editing a price in that seeder and redeploying
/// changed nothing on a database that had already been seeded, because the row was found and
/// skipped, so a live price could only be changed by hand-written SQL.
/// </para>
/// <para>
/// Modules can be edited but never created or deleted here. A module code is not data: it is
/// referenced by <see cref="ModuleCodes"/>, by the route map that decides which pages a
/// subscription unlocks, and by the middleware that enforces it. Inventing a sixth code in the
/// database would produce a module nobody could ever use, and deleting one would strip access from
/// everybody holding it. Retiring a module is what <c>IsActive</c> is for.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/module-catalog")]
[Authorize]
[RequirePermission(Permissions.PlatformAdmin)]
[Produces("application/json")]
public class ModuleCatalogController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ILogger<ModuleCatalogController> _logger;

    public ModuleCatalogController(QMgrDbContext dbContext, ILogger<ModuleCatalogController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Every module in the catalog, including any that have been retired.
    /// </summary>
    /// <remarks>
    /// Deliberately unlike <c>GET api/v1/admin/plans</c>, which filters module rows out, and unlike
    /// <c>GET api/v1/modules</c>, which hides inactive ones. An administrator has to be able to see
    /// a module precisely when it has been switched off, or it could never be switched back on.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(List<ModuleCatalogAdminDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalog(CancellationToken cancellationToken = default)
    {
        var modules = await _dbContext.SubscriptionPlans
            .AsNoTracking()
            .Where(p => ModuleCodes.All.Contains(p.Code))
            .OrderBy(p => p.SortOrder)
            .ToListAsync(cancellationToken);

        // How many organizations hold each module right now, counted once rather than per row.
        var moduleIds = modules.Select(m => m.Id).ToList();
        var subscriberCounts = await _dbContext.OrganizationModules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(om => moduleIds.Contains(om.ModuleId) &&
                         (om.Status == OrganizationModuleStatus.Active ||
                          om.Status == OrganizationModuleStatus.Trialing))
            .GroupBy(om => om.ModuleId)
            .Select(g => new { ModuleId = g.Key, Count = g.Select(x => x.OrganizationId).Distinct().Count() })
            .ToDictionaryAsync(x => x.ModuleId, x => x.Count, cancellationToken);

        return Ok(modules.Select(m => ToDto(m, subscriberCounts.GetValueOrDefault(m.Id))).ToList());
    }

    /// <summary>
    /// Updates one module's presentation, pricing and limits.
    /// </summary>
    [HttpPut("{code}")]
    [ProducesResponseType(typeof(ModuleCatalogAdminDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateModule(
        string code,
        [FromBody] UpdateModuleCatalogRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ModuleCodes.All.Contains(code))
        {
            return NotFound(new { error = "UNKNOWN_MODULE", message = $"There is no module with the code '{code}'." });
        }

        var validationError = Validate(request);
        if (validationError != null)
        {
            return BadRequest(new { error = "INVALID_MODULE", message = validationError });
        }

        var module = await _dbContext.SubscriptionPlans
            .FirstOrDefaultAsync(p => p.Code == code, cancellationToken);

        if (module == null)
        {
            return NotFound(new
            {
                error = "NOT_SEEDED",
                message = $"The module '{code}' is not in this database yet. It is created at start-up, so restarting the API will add it."
            });
        }

        // Worth a line in the log on its own: this is the number every existing subscriber's next
        // invoice is calculated from, and nothing else records that a change happened.
        if (module.MonthlyPriceUgx != request.MonthlyPriceUgx || module.AnnualPriceUgx != request.AnnualPriceUgx ||
            module.MonthlyPriceUsd != request.MonthlyPriceUsd || module.AnnualPriceUsd != request.AnnualPriceUsd)
        {
            _logger.LogWarning(
                "Module {Code} repriced by a platform administrator: UGX {OldMonthlyUgx}/{OldAnnualUgx} -> {NewMonthlyUgx}/{NewAnnualUgx}, USD {OldMonthlyUsd}/{OldAnnualUsd} -> {NewMonthlyUsd}/{NewAnnualUsd}",
                code,
                module.MonthlyPriceUgx, module.AnnualPriceUgx, request.MonthlyPriceUgx, request.AnnualPriceUgx,
                module.MonthlyPriceUsd, module.AnnualPriceUsd, request.MonthlyPriceUsd, request.AnnualPriceUsd);
        }

        module.Name = request.Name.Trim();
        module.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        module.Badge = string.IsNullOrWhiteSpace(request.Badge) ? null : request.Badge.Trim();
        module.SortOrder = request.SortOrder;
        module.TrialDays = request.TrialDays;

        module.MonthlyPriceUsd = request.MonthlyPriceUsd;
        module.AnnualPriceUsd = request.AnnualPriceUsd;
        module.MonthlyPriceUgx = request.MonthlyPriceUgx;
        module.AnnualPriceUgx = request.AnnualPriceUgx;

        module.MaxBranches = request.MaxBranches;
        module.MaxDisplays = request.MaxDisplays;
        module.MaxUsersPerBranch = request.MaxUsersPerBranch;
        module.MaxCountersPerBranch = request.MaxCountersPerBranch;
        module.MaxTokensPerMonth = request.MaxTokensPerMonth;
        module.MaxApiCallsPerMonth = request.MaxApiCallsPerMonth;
        module.MaxStorageMb = request.MaxStorageMb;

        module.IsActive = request.IsActive;
        module.IsPublic = request.IsPublic;
        module.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var subscribers = await CountSubscribersAsync(module.Id, cancellationToken);
        _logger.LogInformation("Module {Code} updated by a platform administrator", code);

        return Ok(ToDto(module, subscribers));
    }

    private async Task<int> CountSubscribersAsync(Guid moduleId, CancellationToken cancellationToken) =>
        await _dbContext.OrganizationModules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(om => om.ModuleId == moduleId &&
                         (om.Status == OrganizationModuleStatus.Active ||
                          om.Status == OrganizationModuleStatus.Trialing))
            .Select(om => om.OrganizationId)
            .Distinct()
            .CountAsync(cancellationToken);

    /// <summary>
    /// Rejects values that would misprice or misrepresent a module rather than merely look odd.
    /// </summary>
    private static string? Validate(UpdateModuleCatalogRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return "A module needs a name.";

        if (request.Name.Trim().Length > 100)
            return "That name is too long — keep it under 100 characters.";

        if (request.Description is { Length: > 1000 })
            return "That description is too long — keep it under 1000 characters.";

        if (request.Badge is { Length: > 40 })
            return "That badge is too long — keep it to a few words.";

        // A negative price would be charged as a negative invoice line.
        if (request.MonthlyPriceUsd < 0 || request.AnnualPriceUsd < 0 ||
            request.MonthlyPriceUgx < 0 || request.AnnualPriceUgx < 0)
            return "Prices cannot be negative.";

        if (request.TrialDays is < 0 or > 365)
            return "The trial length has to be between 0 and 365 days.";

        if (request.MaxBranches < 0 || request.MaxDisplays < 0 || request.MaxUsersPerBranch < 0 ||
            request.MaxCountersPerBranch < 0 || request.MaxTokensPerMonth < 0 ||
            request.MaxApiCallsPerMonth < 0 || request.MaxStorageMb < 0)
            return "Limits cannot be negative.";

        return null;
    }

    private static ModuleCatalogAdminDto ToDto(QMgr.Domain.Entities.Billing.SubscriptionPlan m, int subscriberCount) => new()
    {
        Id = m.Id,
        Code = m.Code,
        Name = m.Name,
        Description = m.Description,
        Badge = m.Badge,
        SortOrder = m.SortOrder,
        TrialDays = m.TrialDays,
        MonthlyPriceUsd = m.MonthlyPriceUsd,
        AnnualPriceUsd = m.AnnualPriceUsd,
        MonthlyPriceUgx = m.MonthlyPriceUgx,
        AnnualPriceUgx = m.AnnualPriceUgx,
        MaxBranches = m.MaxBranches,
        MaxDisplays = m.MaxDisplays,
        MaxUsersPerBranch = m.MaxUsersPerBranch,
        MaxCountersPerBranch = m.MaxCountersPerBranch,
        MaxTokensPerMonth = m.MaxTokensPerMonth,
        MaxApiCallsPerMonth = m.MaxApiCallsPerMonth,
        MaxStorageMb = m.MaxStorageMb,
        IsActive = m.IsActive,
        IsPublic = m.IsPublic,
        SubscriberCount = subscriberCount
    };
}
