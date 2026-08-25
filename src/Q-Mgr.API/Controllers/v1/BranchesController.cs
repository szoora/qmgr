using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/branches")]
[Authorize]
[Produces("application/json")]
public class BranchesController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<BranchesController> _logger;

    public BranchesController(
        QMgrDbContext dbContext,
        ITenantContextAccessor tenantAccessor,
        ILogger<BranchesController> logger)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    #region Branch CRUD

    /// <summary>
    /// Gets all branches (including inactive for admin)
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.BranchesView)]
    [ProducesResponseType(typeof(List<BranchDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBranches([FromQuery] bool includeInactive = false)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        var query = _dbContext.Branches
            .Where(b => b.OrganizationId == tenantContext.OrganizationId);

        if (!includeInactive)
            query = query.Where(b => b.IsActive);

        var branches = await query
            .Select(b => new BranchDto
            {
                Id = b.Id,
                Name = b.Name,
                Code = b.Code,
                Address = b.Address,
                Timezone = b.Timezone,
                IsActive = b.IsActive,
                CounterCount = b.Counters.Count(c => c.IsActive),
                ServiceTypeCount = b.ServiceTypes.Count(s => s.IsActive)
            })
            .ToListAsync();

        return Ok(branches);
    }

    /// <summary>
    /// Gets a specific branch by ID
    /// </summary>
    [HttpGet("{branchId:guid}")]
    [RequirePermission(Permissions.BranchesView)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBranch(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        var branch = await _dbContext.Branches
            .Where(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId)
            .Select(b => new BranchDto
            {
                Id = b.Id,
                Name = b.Name,
                Code = b.Code,
                Address = b.Address,
                Timezone = b.Timezone,
                IsActive = b.IsActive,
                CounterCount = b.Counters.Count(c => c.IsActive),
                ServiceTypeCount = b.ServiceTypes.Count(s => s.IsActive)
            })
            .FirstOrDefaultAsync();

        if (branch == null)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        return Ok(branch);
    }

    /// <summary>
    /// Creates a new branch
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.BranchesCreate)]
    [CheckLimit("branches")]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateBranch([FromBody] CreateBranchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Branch name is required.",
                Status = StatusCodes.Status400BadRequest
            });

        if (string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Branch code is required.",
                Status = StatusCodes.Status400BadRequest
            });

        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // Check for duplicate code within the organization
        var existingCode = await _dbContext.Branches
            .AnyAsync(b => b.Code == request.Code && b.OrganizationId == tenantContext.OrganizationId);
        if (existingCode)
            return BadRequest(new ProblemDetails
            {
                Title = "Duplicate branch code",
                Detail = $"A branch with code '{request.Code}' already exists in your organization.",
                Status = StatusCodes.Status400BadRequest
            });

        var branch = new Branch
        {
            Id = Guid.NewGuid(),
            OrganizationId = tenantContext.OrganizationId, // SECURITY: Always use tenant context
            Name = request.Name,
            Code = request.Code.ToUpperInvariant(),
            Address = request.Address,
            Timezone = request.Timezone ?? "UTC",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Branches.Add(branch);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created branch: {BranchId} - {BranchName}", branch.Id, branch.Name);

        var dto = new BranchDto
        {
            Id = branch.Id,
            Name = branch.Name,
            Code = branch.Code,
            Address = branch.Address,
            Timezone = branch.Timezone,
            IsActive = branch.IsActive,
            CounterCount = 0,
            ServiceTypeCount = 0
        };

        return CreatedAtAction(nameof(GetBranch), new { branchId = branch.Id }, dto);
    }

    /// <summary>
    /// Updates an existing branch
    /// </summary>
    [HttpPut("{branchId:guid}")]
    [RequirePermission(Permissions.BranchesEdit)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateBranch(Guid branchId, [FromBody] UpdateBranchRequest request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        var branch = await _dbContext.Branches
            .FirstOrDefaultAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (branch == null)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Branch name is required.",
                Status = StatusCodes.Status400BadRequest
            });

        // Check for duplicate code if changed within the organization
        if (!string.IsNullOrWhiteSpace(request.Code) && request.Code != branch.Code)
        {
            var existingCode = await _dbContext.Branches
                .AnyAsync(b => b.Code == request.Code && b.Id != branchId && b.OrganizationId == tenantContext.OrganizationId);
            if (existingCode)
                return BadRequest(new ProblemDetails
                {
                    Title = "Duplicate branch code",
                    Detail = $"A branch with code '{request.Code}' already exists.",
                    Status = StatusCodes.Status400BadRequest
                });
            branch.Code = request.Code.ToUpperInvariant();
        }

        branch.Name = request.Name;
        branch.Address = request.Address;
        branch.Timezone = request.Timezone ?? branch.Timezone;
        branch.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated branch: {BranchId} - {BranchName}", branch.Id, branch.Name);

        return Ok(new BranchDto
        {
            Id = branch.Id,
            Name = branch.Name,
            Code = branch.Code,
            Address = branch.Address,
            Timezone = branch.Timezone,
            IsActive = branch.IsActive
        });
    }

    /// <summary>
    /// Toggles a branch's active status
    /// </summary>
    [HttpPatch("{branchId:guid}/toggle")]
    [RequirePermission(Permissions.BranchesEdit)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleBranch(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        var branch = await _dbContext.Branches
            .FirstOrDefaultAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (branch == null)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        branch.IsActive = !branch.IsActive;
        branch.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Toggled branch {BranchId} active status to {IsActive}", branchId, branch.IsActive);

        return Ok(new BranchDto
        {
            Id = branch.Id,
            Name = branch.Name,
            Code = branch.Code,
            Address = branch.Address,
            Timezone = branch.Timezone,
            IsActive = branch.IsActive
        });
    }

    /// <summary>
    /// Deletes a branch (soft delete)
    /// </summary>
    [HttpDelete("{branchId:guid}")]
    [RequirePermission(Permissions.BranchesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteBranch(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        var branch = await _dbContext.Branches
            .Include(b => b.Counters)
            .Include(b => b.ServiceTypes)
            .FirstOrDefaultAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (branch == null)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        // Check if branch has active counters or service types
        var activeCounters = branch.Counters.Count(c => c.IsActive);
        var activeServices = branch.ServiceTypes.Count(s => s.IsActive);

        if (activeCounters > 0 || activeServices > 0)
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot delete branch",
                Detail = $"Branch has {activeCounters} active counter(s) and {activeServices} active service type(s). Please deactivate or delete them first.",
                Status = StatusCodes.Status400BadRequest
            });

        // Soft delete
        branch.IsActive = false;
        branch.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted (soft) branch: {BranchId}", branchId);

        return NoContent();
    }

    #endregion

    #region Counters

    /// <summary>
    /// Gets all counters for a branch
    /// </summary>
    [HttpGet("{branchId:guid}/counters")]
    [RequirePermission(Permissions.CountersView)]
    [ProducesResponseType(typeof(List<CounterDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCounters(Guid branchId, [FromQuery] bool includeInactive = false)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // Verify branch belongs to organization
        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        var query = _dbContext.Counters
            .Where(c => c.BranchId == branchId);

        if (!includeInactive)
            query = query.Where(c => c.IsActive);

        var counters = await query
            .Include(c => c.CurrentToken)
            .Select(c => new CounterDto
            {
                Id = c.Id,
                CounterNumber = c.CounterNumber,
                DisplayName = c.DisplayName ?? $"Counter {c.CounterNumber}",
                Status = c.Status,
                IsActive = c.IsActive,
                CurrentToken = c.CurrentToken != null ? new TokenDto
                {
                    Id = c.CurrentToken.Id,
                    TokenNumber = c.CurrentToken.TokenNumber,
                    DisplayNumber = c.CurrentToken.DisplayNumber,
                    Status = c.CurrentToken.Status,
                    Priority = c.CurrentToken.Priority,
                    Source = c.CurrentToken.Source,
                    BranchId = c.CurrentToken.BranchId,
                    ServiceTypeId = c.CurrentToken.ServiceTypeId,
                    CounterId = c.CurrentToken.CounterId,
                    CreatedAt = c.CurrentToken.CreatedAt,
                    CalledAt = c.CurrentToken.CalledAt
                } : null,
                ServiceTypes = _dbContext.CounterServiceTypes
                    .Where(cst => cst.CounterId == c.Id && cst.IsActive && cst.ServiceType != null)
                    .Select(cst => new ServiceTypeDto
                    {
                        Id = cst.ServiceType!.Id,
                        Name = cst.ServiceType.Name,
                        Code = cst.ServiceType.Code,
                        Prefix = cst.ServiceType.Prefix,
                        Color = cst.ServiceType.Color
                    })
                    .ToList()
            })
            .ToListAsync();

        return Ok(counters);
    }

    /// <summary>
    /// Creates a new counter for a branch
    /// </summary>
    [HttpPost("{branchId:guid}/counters")]
    [RequirePermission(Permissions.CountersCreate)]
    [ProducesResponseType(typeof(CounterDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateCounter(Guid branchId, [FromBody] CreateCounterRequest request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        var branch = await _dbContext.Branches
            .FirstOrDefaultAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (branch == null)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        if (string.IsNullOrWhiteSpace(request.CounterNumber))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Counter number is required.",
                Status = StatusCodes.Status400BadRequest
            });

        // Check for duplicate counter number in branch
        var exists = await _dbContext.Counters.AnyAsync(c => c.BranchId == branchId && c.CounterNumber == request.CounterNumber);
        if (exists)
            return BadRequest(new ProblemDetails
            {
                Title = "Duplicate counter number",
                Detail = $"Counter number '{request.CounterNumber}' already exists in this branch.",
                Status = StatusCodes.Status400BadRequest
            });

        var isEnabled = request.IsEnabled ?? true;
        var counter = new Counter
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            CounterNumber = request.CounterNumber,
            DisplayName = request.DisplayName ?? $"Counter {request.CounterNumber}",
            Status = isEnabled ? CounterStatus.Closed : CounterStatus.Inactive,
            IsActive = isEnabled,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Counters.Add(counter);

        // Add service type associations
        if (request.ServiceTypeIds?.Any() == true)
        {
            foreach (var serviceTypeId in request.ServiceTypeIds)
            {
                _dbContext.CounterServiceTypes.Add(new CounterServiceType
                {
                    Id = Guid.NewGuid(),
                    CounterId = counter.Id,
                    ServiceTypeId = serviceTypeId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created counter: {CounterId} - {CounterNumber} in branch {BranchId}", counter.Id, counter.CounterNumber, branchId);

        return CreatedAtAction(nameof(GetCounters), new { branchId }, new CounterDto
        {
            Id = counter.Id,
            CounterNumber = counter.CounterNumber,
            DisplayName = counter.DisplayName,
            Status = counter.Status,
            IsActive = counter.IsActive
        });
    }

    /// <summary>
    /// Updates a counter
    /// </summary>
    [HttpPut("{branchId:guid}/counters/{counterId:guid}")]
    [RequirePermission(Permissions.CountersEdit)]
    [ProducesResponseType(typeof(CounterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCounter(Guid branchId, Guid counterId, [FromBody] UpdateCounterRequest request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // Verify branch belongs to organization
        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        var counter = await _dbContext.Counters.FirstOrDefaultAsync(c => c.Id == counterId && c.BranchId == branchId);
        if (counter == null)
            return NotFound(new ProblemDetails
            {
                Title = "Counter not found",
                Detail = $"Counter with ID '{counterId}' was not found in branch '{branchId}'.",
                Status = StatusCodes.Status404NotFound
            });

        counter.DisplayName = request.DisplayName ?? counter.DisplayName;
        counter.UpdatedAt = DateTime.UtcNow;

        // Update service type associations
        if (request.ServiceTypeIds != null)
        {
            // Remove existing
            var existing = await _dbContext.CounterServiceTypes.Where(cst => cst.CounterId == counterId).ToListAsync();
            _dbContext.CounterServiceTypes.RemoveRange(existing);

            // Add new
            foreach (var serviceTypeId in request.ServiceTypeIds)
            {
                _dbContext.CounterServiceTypes.Add(new CounterServiceType
                {
                    Id = Guid.NewGuid(),
                    CounterId = counter.Id,
                    ServiceTypeId = serviceTypeId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated counter: {CounterId}", counterId);

        return Ok(new CounterDto
        {
            Id = counter.Id,
            CounterNumber = counter.CounterNumber,
            DisplayName = counter.DisplayName,
            Status = counter.Status,
            IsActive = counter.IsActive
        });
    }

    /// <summary>
    /// Toggles a counter's active status
    /// </summary>
    [HttpPatch("{branchId:guid}/counters/{counterId:guid}/toggle")]
    [RequirePermission(Permissions.CountersEdit)]
    [ProducesResponseType(typeof(CounterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleCounter(Guid branchId, Guid counterId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // Verify branch belongs to organization
        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        var counter = await _dbContext.Counters.FirstOrDefaultAsync(c => c.Id == counterId && c.BranchId == branchId);
        if (counter == null)
            return NotFound(new ProblemDetails
            {
                Title = "Counter not found",
                Detail = $"Counter with ID '{counterId}' was not found in branch '{branchId}'.",
                Status = StatusCodes.Status404NotFound
            });

        counter.IsActive = !counter.IsActive;
        // Update Status based on IsActive: Closed (available) when enabled, Inactive when disabled
        counter.Status = counter.IsActive ? CounterStatus.Closed : CounterStatus.Inactive;
        counter.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Toggled counter {CounterId} active status to {IsActive}", counterId, counter.IsActive);

        return Ok(new CounterDto
        {
            Id = counter.Id,
            CounterNumber = counter.CounterNumber,
            DisplayName = counter.DisplayName,
            Status = counter.Status,
            IsActive = counter.IsActive
        });
    }

    /// <summary>
    /// Deletes a counter
    /// </summary>
    [HttpDelete("{branchId:guid}/counters/{counterId:guid}")]
    [RequirePermission(Permissions.CountersDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCounter(Guid branchId, Guid counterId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // Verify branch belongs to organization
        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        var counter = await _dbContext.Counters.FirstOrDefaultAsync(c => c.Id == counterId && c.BranchId == branchId);
        if (counter == null)
            return NotFound(new ProblemDetails
            {
                Title = "Counter not found",
                Detail = $"Counter with ID '{counterId}' was not found in branch '{branchId}'.",
                Status = StatusCodes.Status404NotFound
            });

        // Soft delete
        counter.IsActive = false;
        counter.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted (soft) counter: {CounterId}", counterId);

        return NoContent();
    }

    #endregion

    #region Service Types

    /// <summary>
    /// Gets all service types for a branch
    /// </summary>
    [HttpGet("{branchId:guid}/service-types")]
    [RequirePermission(Permissions.ServiceTypesView)]
    [ProducesResponseType(typeof(List<ServiceTypeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetServiceTypes(Guid branchId, [FromQuery] bool includeInactive = false)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // Verify branch belongs to organization
        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        var query = _dbContext.ServiceTypes.Where(st => st.BranchId == branchId);

        if (!includeInactive)
            query = query.Where(st => st.IsActive);

        var serviceTypes = await query
            .Select(st => new ServiceTypeDto
            {
                Id = st.Id,
                Name = st.Name,
                Code = st.Code,
                Description = st.Description,
                Prefix = st.Prefix,
                AverageServiceTimeMinutes = st.AverageServiceTimeMinutes,
                IconUrl = st.IconUrl,
                Color = st.Color,
                IsActive = st.IsActive
            })
            .ToListAsync();

        return Ok(serviceTypes);
    }

    /// <summary>
    /// Creates a new service type for a branch
    /// </summary>
    [HttpPost("{branchId:guid}/service-types")]
    [RequirePermission(Permissions.ServiceTypesCreate)]
    [ProducesResponseType(typeof(ServiceTypeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateServiceType(Guid branchId, [FromBody] CreateServiceTypeRequest request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        var branch = await _dbContext.Branches
            .FirstOrDefaultAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (branch == null)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        if (string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Service type code is required.",
                Status = StatusCodes.Status400BadRequest
            });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Service type name is required.",
                Status = StatusCodes.Status400BadRequest
            });

        // Check for duplicate code in branch
        var exists = await _dbContext.ServiceTypes.AnyAsync(st => st.BranchId == branchId && st.Code == request.Code);
        if (exists)
            return BadRequest(new ProblemDetails
            {
                Title = "Duplicate service type code",
                Detail = $"Service type code '{request.Code}' already exists in this branch.",
                Status = StatusCodes.Status400BadRequest
            });

        var serviceType = new ServiceType
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            Code = request.Code.ToUpperInvariant(),
            Name = request.Name,
            Description = request.Description,
            Prefix = request.Prefix ?? request.Code.ToUpperInvariant(),
            AverageServiceTimeMinutes = request.EstimatedServiceTime ?? 10,
            Color = request.Color ?? "#299be8",
            IsActive = request.IsActive ?? true,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.ServiceTypes.Add(serviceType);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created service type: {ServiceTypeId} - {ServiceTypeName} in branch {BranchId}", serviceType.Id, serviceType.Name, branchId);

        return CreatedAtAction(nameof(GetServiceTypes), new { branchId }, new ServiceTypeDto
        {
            Id = serviceType.Id,
            Name = serviceType.Name,
            Code = serviceType.Code,
            Description = serviceType.Description,
            Prefix = serviceType.Prefix,
            AverageServiceTimeMinutes = serviceType.AverageServiceTimeMinutes,
            Color = serviceType.Color,
            IsActive = serviceType.IsActive
        });
    }

    /// <summary>
    /// Updates a service type
    /// </summary>
    [HttpPut("{branchId:guid}/service-types/{serviceTypeId:guid}")]
    [RequirePermission(Permissions.ServiceTypesEdit)]
    [ProducesResponseType(typeof(ServiceTypeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateServiceType(Guid branchId, Guid serviceTypeId, [FromBody] UpdateServiceTypeRequest request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // Verify branch belongs to organization
        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        var serviceType = await _dbContext.ServiceTypes.FirstOrDefaultAsync(st => st.Id == serviceTypeId && st.BranchId == branchId);
        if (serviceType == null)
            return NotFound(new ProblemDetails
            {
                Title = "Service type not found",
                Detail = $"Service type with ID '{serviceTypeId}' was not found in branch '{branchId}'.",
                Status = StatusCodes.Status404NotFound
            });

        if (!string.IsNullOrWhiteSpace(request.Code) && request.Code != serviceType.Code)
        {
            var exists = await _dbContext.ServiceTypes.AnyAsync(st => st.BranchId == branchId && st.Code == request.Code && st.Id != serviceTypeId);
            if (exists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Duplicate service type code",
                    Detail = $"Service type code '{request.Code}' already exists in this branch.",
                    Status = StatusCodes.Status400BadRequest
                });
            serviceType.Code = request.Code.ToUpperInvariant();
        }

        serviceType.Name = request.Name ?? serviceType.Name;
        serviceType.Description = request.Description;
        serviceType.Prefix = request.Prefix ?? serviceType.Prefix;
        serviceType.AverageServiceTimeMinutes = request.EstimatedServiceTime ?? serviceType.AverageServiceTimeMinutes;
        serviceType.Color = request.Color ?? serviceType.Color;
        serviceType.IsActive = request.IsActive ?? serviceType.IsActive;
        serviceType.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated service type: {ServiceTypeId}", serviceTypeId);

        return Ok(new ServiceTypeDto
        {
            Id = serviceType.Id,
            Name = serviceType.Name,
            Code = serviceType.Code,
            Description = serviceType.Description,
            Prefix = serviceType.Prefix,
            AverageServiceTimeMinutes = serviceType.AverageServiceTimeMinutes,
            Color = serviceType.Color,
            IsActive = serviceType.IsActive
        });
    }

    /// <summary>
    /// Deletes a service type
    /// </summary>
    [HttpDelete("{branchId:guid}/service-types/{serviceTypeId:guid}")]
    [RequirePermission(Permissions.ServiceTypesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteServiceType(Guid branchId, Guid serviceTypeId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // Verify branch belongs to organization
        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        var serviceType = await _dbContext.ServiceTypes.FirstOrDefaultAsync(st => st.Id == serviceTypeId && st.BranchId == branchId);
        if (serviceType == null)
            return NotFound(new ProblemDetails
            {
                Title = "Service type not found",
                Detail = $"Service type with ID '{serviceTypeId}' was not found in branch '{branchId}'.",
                Status = StatusCodes.Status404NotFound
            });

        // Soft delete
        serviceType.IsActive = false;
        serviceType.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted (soft) service type: {ServiceTypeId}", serviceTypeId);

        return NoContent();
    }

    #endregion
}

#region Request/Response DTOs

public record BranchDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string? Address { get; init; }
    public string Timezone { get; init; } = "UTC";
    public bool IsActive { get; init; }
    public int CounterCount { get; init; }
    public int ServiceTypeCount { get; init; }
}

public record CreateBranchRequest
{
    public Guid? OrganizationId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string? Address { get; init; }
    public string? Timezone { get; init; }
}

public record UpdateBranchRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? Address { get; init; }
    public string? Timezone { get; init; }
}

public record CreateCounterRequest
{
    public string CounterNumber { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public List<Guid>? ServiceTypeIds { get; init; }
    public bool? IsEnabled { get; init; } = true;
}

public record UpdateCounterRequest
{
    public string? DisplayName { get; init; }
    public List<Guid>? ServiceTypeIds { get; init; }
}

public record CreateServiceTypeRequest
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Prefix { get; init; }
    public int? EstimatedServiceTime { get; init; }
    public string? Color { get; init; }
    public bool? IsActive { get; init; }
}

public record UpdateServiceTypeRequest
{
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Prefix { get; init; }
    public int? EstimatedServiceTime { get; init; }
    public string? Color { get; init; }
    public bool? IsActive { get; init; }
}

#endregion
