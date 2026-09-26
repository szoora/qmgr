using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The Import inbox (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E11, 2026-09-26): send a routed document for approval,
/// see what waits for you, open a section, reject or complete it. APPROVING a programme section is not here — it is the
/// programme import's own commit (<c>POST …/calendar/import</c> with <c>InboxJobId</c>), so the one path that writes
/// events, meetings and rota slots stays one path.
///
/// No permission attribute on the class: who may act is decided per section, from the permission that owns it
/// (<see cref="ImportInbox.PermissionsFor"/>). A document a caller neither uploaded nor approves any part of is 404.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
public class ImportsController : StaffPerformanceControllerBase
{
    private readonly IImportInbox _inbox;
    private readonly ILogger<ImportsController> _logger;

    public ImportsController(QMgrDbContext db, ITenantContextAccessor tenantAccessor, IStaffScopeService staffScope, IActivityLogger activity,
        IImportInbox inbox, ILogger<ImportsController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _inbox = inbox;
        _logger = logger;
    }

    [HttpGet("branches/{branchId:guid}/imports")]
    [ProducesResponseType(typeof(List<ImportInboxJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid branchId, [FromQuery] bool awaiting = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        return Ok(await _inbox.ListAsync(organizationId, branchId, CurrentUserId(), HasPermissionAsync, awaiting));
    }

    [HttpPost("branches/{branchId:guid}/imports")]
    [ProducesResponseType(typeof(ImportInboxJobDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit(Guid branchId, [FromBody] SubmitImportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var (jobId, refusal) = await _inbox.SubmitAsync(organizationId, branchId, CurrentUserId(), request ?? new SubmitImportRequest(), HasPermissionAsync);
        if (refusal != null) return BadRequestProblem(refusal);

        await Activity.RecordAsync(ActivityActions.ImportSubmitted, "RosterImportJob", jobId, null,
            $"Document sent for approval: {string.Join(", ", request!.SourceFiles.Take(3))} ({request.Sections.Count} part(s))",
            new { sections = request.Sections.Select(s => s.Kind).ToList() }, branchId, organizationId);

        var job = (await _inbox.ListAsync(organizationId, branchId, CurrentUserId(), HasPermissionAsync, false)).FirstOrDefault(j => j.Id == jobId);
        return StatusCode(StatusCodes.Status201Created, job);
    }

    [HttpGet("branches/{branchId:guid}/imports/{jobId:guid}/sections/{sectionKey}")]
    [ProducesResponseType(typeof(ImportInboxStagedDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Staged(Guid branchId, Guid jobId, string sectionKey)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var staged = await _inbox.StagedAsync(organizationId, branchId, jobId, sectionKey, HasPermissionAsync);
        return staged == null ? NotFoundProblem("Not found") : Ok(staged);
    }

    /// <summary>Rejects a section — or, from the person who uploaded it, withdraws it. A note says why.</summary>
    [HttpPost("branches/{branchId:guid}/imports/{jobId:guid}/sections/{sectionKey}/reject")]
    public Task<IActionResult> Reject(Guid branchId, Guid jobId, string sectionKey, [FromBody] DecideImportSectionRequest request)
        => DecideAsync(branchId, jobId, sectionKey, ImportSectionStates.Rejected, request, ActivityActions.ImportSectionRejected);

    /// <summary>
    /// Marks a HANDED-OFF section (a staff list, a roll, a timetable) approved once its own importer has run. Programme
    /// sections are approved by committing them through the programme import, never here.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/imports/{jobId:guid}/sections/{sectionKey}/complete")]
    public async Task<IActionResult> Complete(Guid branchId, Guid jobId, string sectionKey, [FromBody] DecideImportSectionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var staged = await _inbox.StagedAsync(organizationId, branchId, jobId, sectionKey, HasPermissionAsync);
        if (staged == null) return NotFoundProblem("Not found");
        if (!ImportSectionKinds.IsHandoff(staged.Kind))
            return BadRequestProblem("Approve this part by importing it.", "Events, meetings and rota slots are approved on the import page, which checks them with the server first.");
        return await DecideAsync(branchId, jobId, sectionKey, ImportSectionStates.Approved, request, ActivityActions.ImportSectionApproved);
    }

    private async Task<IActionResult> DecideAsync(Guid branchId, Guid jobId, string sectionKey, string state, DecideImportSectionRequest request, string action)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        string? refusal = null;

        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await Db.Database.BeginTransactionAsync();
            refusal = await _inbox.DecideInTransactionAsync(organizationId, branchId, jobId, sectionKey, me, state, request?.Note, request?.ResultJobId, HasPermissionAsync);
            if (refusal == null) await tx.CommitAsync();
        });
        if (refusal != null) return ConflictProblem(refusal);

        await Activity.RecordAsync(action, "RosterImportJob", jobId, null,
            $"{(state == ImportSectionStates.Rejected ? "Rejected" : "Approved")} part {sectionKey} of a document" + (string.IsNullOrWhiteSpace(request?.Note) ? "" : $": {request!.Note}"),
            null, branchId, organizationId);
        return Ok((await _inbox.ListAsync(organizationId, branchId, me, HasPermissionAsync, false)).FirstOrDefault(j => j.Id == jobId));
    }

    // ---- The school's rule: does a second person have to approve? ---------------------------------------------

    [HttpGet("imports/settings")]
    [ProducesResponseType(typeof(ImportSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings()
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();
        return Ok(await _inbox.SettingsAsync(organizationId.Value));
    }

    [HttpPut("imports/settings")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(ImportSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveSettings([FromBody] ImportSettingsDto request)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();
        await _inbox.SaveSettingsAsync(organizationId.Value, request ?? new ImportSettingsDto());
        _logger.LogInformation("Import approval rule for {OrganizationId}: second approver {Required}", organizationId, request?.RequireSecondApprover);
        return Ok(await _inbox.SettingsAsync(organizationId.Value));
    }
}
