using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Jobs;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Bulk staff onboarding (plan Phase 6): a list of people with a role, departments and a line
/// manager becomes User rows, each invited to set a password through the existing reset-link flow.
/// Rows go into <c>roster_import_jobs</c> as <see cref="RosterImportKind.Staff"/> and are processed
/// by RosterImportProcessorJob like every other bulk upload — same job table, same per-row entries
/// log, same live progress channel. Progress and the per-row log are read through
/// StudentsController's import-job endpoints (<c>GET branches/{b}/students/import-jobs?kind=Staff</c>,
/// <c>…/{jobId}</c>, <c>…/{jobId}/entries</c>), which already carry Kind on the DTO.
///
/// Gated on BOTH users.create and staff.structure.manage, and REFUSED for a staff-scoped caller:
/// the processor runs in a Hangfire worker where IStaffScopeService does not exist, so a bulk
/// write it carries out cannot be row-scoped downstream (the BatchController rule).
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StaffPerformance)]
public class StaffImportController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IStaffScopeService _scope;
    private readonly IActivityLogger _activity;
    private readonly ILogger<StaffImportController> _logger;

    private const int MaxRows = 2000;

    public StaffImportController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService scope,
        IActivityLogger activity,
        ILogger<StaffImportController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _scope = scope;
        _activity = activity;
        _logger = logger;
    }

    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails { Title = "Tenant not resolved", Status = StatusCodes.Status401Unauthorized });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var superAdminBranchExists = await _context.Branches.AnyAsync(b => b.Id == branchId);
            return superAdminBranchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
        }

        var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        return branchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
    }

    private async Task<Guid> ResolveOrganizationIdAsync(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext!;
        return RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync()
            : tenantContext.OrganizationId;
    }

    private Guid CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : Guid.Empty;
    }

    /// <summary>
    /// Refuses a staff-scoped caller, or null for everyone else. A 403 with a reason rather than a
    /// 404: there is no record whose existence a 403 would confirm here — the caller's own role is
    /// the answer, and they need to know why.
    /// </summary>
    private async Task<IActionResult?> ScopedCallerRefusalAsync()
    {
        if (await _scope.IsUnscopedAsync()) return null;

        return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
        {
            Title = "A staff import can only be started by a caller with organization-wide staff scope",
            Detail = "Your role's staff scope is limited to your departments or reports. The import creates accounts across the whole branch in a background job that cannot apply that limit, so it is refused here rather than allowed to bypass it.",
            Status = StatusCodes.Status403Forbidden
        });
    }

    /// <summary>
    /// Starts the import. Returns 202 with the job; the RowsJson stashed on the job is the whole
    /// request (rows plus the SendInvites flag), because the worker has no other way to learn it.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/staff/import-jobs")]
    [RequirePermission(Permissions.UsersCreate)]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(RosterImportJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartImport(Guid branchId, [FromBody] StartStaffImportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var refusal = await ScopedCallerRefusalAsync();
        if (refusal != null) return refusal;

        if (request.Rows == null || request.Rows.Count == 0)
            return BadRequest(new ProblemDetails { Title = "No rows to import", Status = StatusCodes.Status400BadRequest });
        if (request.Rows.Count > MaxRows)
            return BadRequest(new ProblemDetails { Title = $"A single staff import is capped at {MaxRows:N0} rows — split larger files", Status = StatusCodes.Status400BadRequest });

        var me = CurrentUserId();
        if (me == Guid.Empty)
            return BadRequest(new ProblemDetails { Title = "A staff import must be started by a signed-in user", Detail = "Invitations and the activity log are attributed to the person who started the import; API-key callers cannot be attributed.", Status = StatusCodes.Status400BadRequest });

        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var job = new RosterImportJob
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CreatedByUserId = me,
            SourceFileName = null,
            Source = "admin_ui",
            Kind = RosterImportKind.Staff,
            Status = RosterImportStatus.Pending,
            TotalRows = request.Rows.Count,
            RowsJson = JsonSerializer.Serialize(request)
        };
        _context.RosterImportJobs.Add(job);
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.StaffImportStarted, nameof(RosterImportJob), job.Id, null,
            $"Staff import started: {request.Rows.Count} row(s){(request.SendInvites ? ", invitations on" : ", no invitations")}",
            new { Rows = request.Rows.Count, request.SendInvites }, branchId, organizationId);

        BackgroundJob.Enqueue<RosterImportProcessorJob>(j => j.ProcessAsync(job.Id));
        _logger.LogInformation("Staff import job {JobId} queued for branch {BranchId} with {Rows} row(s)", job.Id, branchId, request.Rows.Count);

        return AcceptedAtAction(nameof(StudentsController.GetImportJob), "Students", new { branchId, jobId = job.Id }, StudentsController.MapToDto(job));
    }

    /// <summary>
    /// The staff import's own read of its jobs. StudentsController has the same three reads for the
    /// roster and welfare kinds, but they are gated on students.view and the STUDENT scope, which a
    /// staff-structure manager need not hold and which is the wrong axis here. A scoped staff caller
    /// cannot start an import, so these show nothing to one (404, not an empty list).
    /// </summary>
    [HttpGet("branches/{branchId:guid}/staff/import-jobs")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(List<RosterImportJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImportJobs(Guid branchId, [FromQuery] int limit = 50)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await _scope.IsUnscopedAsync()) return NotFound();

        var jobs = await _context.RosterImportJobs
            .Where(j => j.BranchId == branchId && j.Kind == RosterImportKind.Staff)
            .OrderByDescending(j => j.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync();
        return Ok(jobs.Select(StudentsController.MapToDto).ToList());
    }

    [HttpGet("branches/{branchId:guid}/staff/import-jobs/{jobId:guid}")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(RosterImportJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImportJob(Guid branchId, Guid jobId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await _scope.IsUnscopedAsync()) return NotFound();

        var job = await _context.RosterImportJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.BranchId == branchId && j.Kind == RosterImportKind.Staff);
        return job == null ? NotFound() : Ok(StudentsController.MapToDto(job));
    }

    [HttpGet("branches/{branchId:guid}/staff/import-jobs/{jobId:guid}/entries")]
    [RequirePermission(Permissions.StaffStructureManage)]
    [ProducesResponseType(typeof(List<RosterImportJobEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImportJobEntries(Guid branchId, Guid jobId, [FromQuery] int limit = 500)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await _scope.IsUnscopedAsync()) return NotFound();

        var jobExists = await _context.RosterImportJobs.AnyAsync(j => j.Id == jobId && j.BranchId == branchId && j.Kind == RosterImportKind.Staff);
        if (!jobExists) return NotFound();

        var entries = await _context.RosterImportJobEntries
            .Where(e => e.RosterImportJobId == jobId)
            .OrderBy(e => e.RowNumber)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToListAsync();
        return Ok(entries.Select(StudentsController.MapToDto).ToList());
    }
}
