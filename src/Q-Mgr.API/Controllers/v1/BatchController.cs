using System.Globalization;
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
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Jobs;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Bulk edits: preview one, run one, reverse one.
/// </summary>
/// <remarks>
/// <para>
/// Three endpoints for every operation in <see cref="BatchOperation"/>, because a batch is the
/// same shape whatever it touches — a set of ids, a resolver that says what would happen, a
/// background writer, and a record afterwards. Adding a new bulk action means adding a resolver
/// branch, not another controller.
/// </para>
/// <para>
/// <b>No new permission.</b> Each operation is gated on the permission that already governs doing
/// that thing one at a time — promoting a class is <c>students.manage</c>, whose own description
/// already reads "Create/edit/delete students and guardians, bulk import a roster". Inventing a
/// <c>batch.*</c> permission would mean re-seeding permissions and re-granting five default roles
/// for no behavioural difference, which is the reasoning AppointmentsController already records
/// for the same choice.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/batch")]
[Produces("application/json")]
[Authorize]
public class BatchController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly IBatchOperationService _resolver;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IStudentScopeService _scope;
    private readonly ILogger<BatchController> _logger;

    public BatchController(
        QMgrDbContext context,
        IBatchOperationService resolver,
        ITenantContextAccessor tenantAccessor,
        IStudentScopeService scope,
        ILogger<BatchController> logger)
    {
        _context = context;
        _resolver = resolver;
        _tenantAccessor = tenantAccessor;
        _scope = scope;
        _logger = logger;
    }

    /// <summary>
    /// The dry run. Returns exactly the outcomes the commit will produce, from the same resolver.
    /// </summary>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(BatchPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Preview(Guid branchId, [FromBody] BatchRequest request, CancellationToken ct)
    {
        var guard = await GuardAsync(branchId, request.Operation);
        if (guard != null) return guard;

        return Ok(await _resolver.ResolveAsync(branchId, request, ct));
    }

    /// <summary>
    /// Runs the batch in the background and returns immediately with a job id.
    /// </summary>
    /// <remarks>
    /// The batch is re-resolved inside the job rather than taking the preview's word for it — a
    /// colleague may have changed the same records in the seconds since. See
    /// <see cref="BatchOperationProcessorJob"/>.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(BatchAcceptedDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Run(Guid branchId, [FromBody] BatchRequest request, CancellationToken ct)
    {
        var guard = await GuardAsync(branchId, request.Operation);
        if (guard != null) return guard;

        var preview = await _resolver.ResolveAsync(branchId, request, ct);
        if (preview.BlockingError != null)
            return BadRequest(new ProblemDetails { Title = preview.BlockingError, Status = StatusCodes.Status400BadRequest });

        if (preview.WillChange == 0)
            return BadRequest(new ProblemDetails
            {
                Title = "Nothing would change.",
                Detail = "Every selected record is already in the state you asked for, or cannot be changed. Nothing was run.",
                Status = StatusCodes.Status400BadRequest
            });

        var organizationId = await _context.Branches.AsNoTracking()
            .Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstOrDefaultAsync(ct);

        var job = new RosterImportJob
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CreatedByUserId = CurrentUserId(),
            Kind = RosterImportKind.Batch,
            Status = RosterImportStatus.Pending,
            // The operation's own summary is what the history list shows, so a row reads
            // "Promote 731 students to their next class" rather than a file name.
            SourceFileName = preview.Summary,
            Source = "admin_ui",
            TotalRows = preview.Rows.Count,
            RowsJson = JsonSerializer.Serialize(request)
        };

        _context.RosterImportJobs.Add(job);
        await _context.SaveChangesAsync(ct);

        BackgroundJob.Enqueue<BatchOperationProcessorJob>(j => j.ProcessAsync(job.Id));

        _logger.LogInformation("Batch {JobId} queued on branch {BranchId}: {Summary}", job.Id, branchId, preview.Summary);

        return Accepted(new BatchAcceptedDto { JobId = job.Id, TotalRows = preview.Rows.Count, Summary = preview.Summary });
    }

    /// <summary>
    /// Puts every record in a finished batch back to the value it held before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refused outright if <b>any</b> record has been modified since the batch ran. A partial undo
    /// is worse than none: it leaves a roster in a state nobody chose and nobody can describe. The
    /// response names the records that moved so the operator can go and look at them.
    /// </para>
    /// <para>
    /// There is deliberately no time limit. A window would be arbitrary — the real question is
    /// whether anything has changed underneath, and that is what gets asked.
    /// </para>
    /// </remarks>
    [HttpPost("{jobId:guid}/undo")]
    [ProducesResponseType(typeof(BatchUndoResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Undo(Guid branchId, Guid jobId, CancellationToken ct)
    {
        // BEFORE the job lookup, not inside GuardAsync below: a scoped caller must not be able to
        // tell an existing batch (403) from one that never existed (404) by the status code.
        var scopeRefusal = await ScopedCallerRefusalAsync();
        if (scopeRefusal != null) return scopeRefusal;

        var job = await _context.RosterImportJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.BranchId == branchId && j.Kind == RosterImportKind.Batch, ct);

        if (job == null) return NotFound();

        var request = JsonSerializer.Deserialize<BatchRequest>(job.RowsJson);
        if (request == null)
            return Ok(new BatchUndoResultDto { Success = false, Message = "This batch's own record can't be read back, so it can't be reversed." });

        var guard = await GuardAsync(branchId, request.Operation);
        if (guard != null) return guard;

        if (job.Status != RosterImportStatus.Completed)
            return Ok(new BatchUndoResultDto { Success = false, Message = "Only a finished batch can be reversed." });

        var entries = await _context.RosterImportJobEntries
            .Where(e => e.RosterImportJobId == job.Id && e.Outcome == RosterImportRowOutcome.Updated && e.StudentId != null)
            .ToListAsync(ct);

        if (entries.Count == 0)
            return Ok(new BatchUndoResultDto { Success = false, Message = "This batch changed nothing, so there is nothing to reverse." });

        // Conflict check first, across the whole batch, before a single write.
        var conflicts = new List<string>();
        foreach (var e in entries)
        {
            var currentValue = await ReadCurrentAsync(request, e.StudentId!.Value, ct);
            if (currentValue == null)
            {
                conflicts.Add($"{e.StudentName} — no longer exists");
                continue;
            }
            if (!string.Equals(currentValue, e.NewValue, StringComparison.OrdinalIgnoreCase))
                conflicts.Add(currentValue.Length == 0
                    ? $"{e.StudentName} — now empty, not \"{e.NewValue}\""
                    : $"{e.StudentName} — now \"{currentValue}\", not \"{e.NewValue}\"");
        }

        if (conflicts.Count > 0)
        {
            return Ok(new BatchUndoResultDto
            {
                Success = false,
                Message = $"{conflicts.Count} of {entries.Count} record{(entries.Count == 1 ? "" : "s")} {(conflicts.Count == 1 ? "has" : "have")} changed since this batch ran, so reversing it would overwrite somebody else's work. Nothing was undone.",
                ConflictedRows = conflicts.Take(25).ToList()
            });
        }

        var reverted = 0;
        foreach (var e in entries)
        {
            await RevertAsync(request, e.StudentId!.Value, e.PreviousValue, ct);
            reverted++;
        }

        job.FailureReason = $"Reversed on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";
        await _context.SaveChangesAsync(ct);

        _logger.LogWarning("Batch {JobId} reversed: {Count} records restored", job.Id, reverted);

        return Ok(new BatchUndoResultDto
        {
            Success = true,
            RevertedRows = reverted,
            Message = $"{reverted:N0} record{(reverted == 1 ? "" : "s")} put back to what {(reverted == 1 ? "it was" : "they were")}."
        });
    }

    // =============================================================================================

    /// <summary>The current value of whatever field the batch touched, for the conflict check.</summary>
    private async Task<string?> ReadCurrentAsync(BatchRequest request, Guid id, CancellationToken ct) => request.Operation switch
    {
        BatchOperation.AdvanceClass => await _context.Students.AsNoTracking().Where(s => s.Id == id).Select(s => s.ClassName).FirstOrDefaultAsync(ct),
        BatchOperation.Deactivate => await _context.Students.AsNoTracking().Where(s => s.Id == id).Select(s => s.IsActive ? "Active" : "Inactive").FirstOrDefaultAsync(ct),
        BatchOperation.SetConsent => await _context.Students.AsNoTracking().Where(s => s.Id == id).Select(s => s.DataConsentGivenAt != null ? "Recorded" : "Not recorded").FirstOrDefaultAsync(ct),
        BatchOperation.SetField => await ReadStudentFieldAsync(request.Field, id, ct),
        BatchOperation.SetWelfareStatus => await _context.WelfareRecords.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status.ToString()).FirstOrDefaultAsync(ct),
        BatchOperation.AssignWelfareAction => await _context.WelfareRecords.AsNoTracking().Where(r => r.Id == id).Select(r => r.AssignedToUserId.ToString()).FirstOrDefaultAsync(ct),
        BatchOperation.SetUserActive => await _context.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.IsActive ? "Active" : "Inactive").FirstOrDefaultAsync(ct),
        BatchOperation.SetUserRole => await _context.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.RoleId.ToString()).FirstOrDefaultAsync(ct),

        BatchOperation.SetWelfareReviewDate => await _context.WelfareRecords.AsNoTracking().Where(r => r.Id == id)
            .Select(r => r.ActionDueDate == null ? "" : r.ActionDueDate.Value.ToString("yyyy-MM-dd")).FirstOrDefaultAsync(ct),

        BatchOperation.CancelTokens => await _context.Tokens.AsNoTracking().Where(t => t.Id == id)
            .Select(t => t.Status.ToString()).FirstOrDefaultAsync(ct),

        BatchOperation.CancelAppointments => await _context.Appointments.AsNoTracking().Where(a => a.Id == id)
            .Select(a => a.Status.ToString()).FirstOrDefaultAsync(ct),

        // "Checked out" / "On site since HH:mm" — the same two strings ResolveVisitorCheckOutAsync
        // writes, because the conflict check compares against what it recorded, not a status enum.
        BatchOperation.CheckOutVisitors => await _context.Visitors.AsNoTracking().Where(v => v.Id == id && v.DeletedAt == null)
            .Select(v => v.CheckedOutAt != null ? "Checked out" : "On site since " + (v.CheckedInAt == null ? "" : v.CheckedInAt.Value.ToString("HH:mm")))
            .FirstOrDefaultAsync(ct),

        _ => null
    };

    private async Task<string?> ReadStudentFieldAsync(BatchField? field, Guid id, CancellationToken ct)
    {
        var s = await _context.Students.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.ClassName, x.House, x.DormitoryOrStream, x.Residency, x.FeesStatus, x.TransportMode, x.SponsorName })
            .FirstOrDefaultAsync(ct);
        if (s == null) return null;

        return field switch
        {
            BatchField.ClassName => s.ClassName,
            BatchField.House => s.House,
            BatchField.DormitoryOrStream => s.DormitoryOrStream,
            BatchField.Residency => s.Residency?.ToString(),
            BatchField.FeesStatus => s.FeesStatus?.ToString(),
            BatchField.TransportMode => s.TransportMode?.ToString(),
            BatchField.SponsorName => s.SponsorName,
            _ => null
        };
    }

    private async Task RevertAsync(BatchRequest request, Guid id, string? previous, CancellationToken ct)
    {
        switch (request.Operation)
        {
            case BatchOperation.AdvanceClass:
            {
                var s = await _context.Students.FirstAsync(x => x.Id == id, ct);
                s.ClassName = previous; s.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.Deactivate:
            {
                var s = await _context.Students.FirstAsync(x => x.Id == id, ct);
                s.IsActive = string.Equals(previous, "Active", StringComparison.OrdinalIgnoreCase);
                s.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.SetConsent:
            {
                var s = await _context.Students.FirstAsync(x => x.Id == id, ct);
                s.DataConsentGivenAt = string.Equals(previous, "Recorded", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : null;
                s.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.SetField:
            {
                var s = await _context.Students.FirstAsync(x => x.Id == id, ct);
                switch (request.Field)
                {
                    case BatchField.ClassName: s.ClassName = previous; break;
                    case BatchField.House: s.House = previous; break;
                    case BatchField.DormitoryOrStream: s.DormitoryOrStream = previous; break;
                    case BatchField.SponsorName: s.SponsorName = previous; break;
                    case BatchField.Residency: s.Residency = Enum.TryParse<StudentResidency>(previous, true, out var r) ? r : null; break;
                    case BatchField.FeesStatus: s.FeesStatus = Enum.TryParse<StudentFeesStatus>(previous, true, out var f) ? f : null; break;
                    case BatchField.TransportMode: s.TransportMode = Enum.TryParse<StudentTransportMode>(previous, true, out var t) ? t : null; break;
                }
                s.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.SetWelfareStatus:
            {
                var r = await _context.WelfareRecords.FirstAsync(x => x.Id == id, ct);
                if (Enum.TryParse<WelfareStatus>(previous, true, out var st)) r.Status = st;
                r.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.AssignWelfareAction:
            {
                var r = await _context.WelfareRecords.FirstAsync(x => x.Id == id, ct);
                r.AssignedToUserId = Guid.TryParse(previous, out var uid) ? uid : null;
                r.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.SetUserActive:
            {
                var u = await _context.Users.FirstAsync(x => x.Id == id, ct);
                u.IsActive = string.Equals(previous, "Active", StringComparison.OrdinalIgnoreCase);
                u.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.SetUserRole:
            {
                var u = await _context.Users.FirstAsync(x => x.Id == id, ct);
                if (Guid.TryParse(previous, out var rid)) u.RoleId = rid;
                u.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.SetWelfareReviewDate:
            {
                var r = await _context.WelfareRecords.FirstAsync(x => x.Id == id, ct);
                r.ActionDueDate = DateTime.TryParse(previous, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var due)
                    ? DateTime.SpecifyKind(due, DateTimeKind.Utc)
                    : null;
                r.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.CancelTokens:
            {
                var t = await _context.Tokens.FirstAsync(x => x.Id == id, ct);
                if (Enum.TryParse<TokenStatus>(previous, true, out var ts)) t.Status = ts;
                break;
            }
            case BatchOperation.CancelAppointments:
            {
                var a = await _context.Appointments.FirstAsync(x => x.Id == id, ct);
                if (Enum.TryParse<AppointmentStatus>(previous, true, out var ast)) a.Status = ast;
                // The reason belonged to the cancellation; putting the booking back should not
                // leave "Cancelled in bulk" sitting on a live appointment.
                a.CancellationReason = null;
                a.UpdatedAt = DateTime.UtcNow; break;
            }
            case BatchOperation.CheckOutVisitors:
            {
                // Undoing a check-out puts the visitor back on site; the check-in time was never
                // touched, so clearing the departure is the whole reversal.
                var v = await _context.Visitors.FirstAsync(x => x.Id == id, ct);
                v.CheckedOutAt = null;
                v.UpdatedAt = DateTime.UtcNow; break;
            }
        }
    }

    /// <summary>
    /// Branch ownership plus the permission that already governs doing this one record at a time.
    /// </summary>
    private async Task<IActionResult?> GuardAsync(Guid branchId, BatchOperation operation)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails { Title = "Tenant not resolved", Status = StatusCodes.Status401Unauthorized });

        var isSuperAdmin = RoleCodes.IsSuperAdmin(tenantContext.UserRole);

        var branchExists = await _context.Branches
            .AnyAsync(b => b.Id == branchId && (isSuperAdmin || b.OrganizationId == tenantContext.OrganizationId));
        if (!branchExists)
            return NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });

        if (isSuperAdmin) return null;

        // A class-scoped caller cannot run ANY batch, and the reason is structural rather than a
        // judgement about this particular operation.
        //
        // Every endpoint here takes a bare list of record IDs and hands them to a resolver that
        // filters on BranchId alone, then to BatchOperationProcessorJob -- a Hangfire worker, where
        // IStudentScopeService does not exist because it reads the caller's HTTP context. There is
        // nowhere downstream to apply the row filter every welfare and roster READ respects, so the
        // preview would name students outside the caller's classes and the run would write to them.
        // This is the same rule the welfare bulk import already follows: a bulk operation carried
        // out by a background job cannot be row-scoped later, so it is refused here.
        //
        // It matters today and not only in theory: the class-teacher role holds welfare.edit, which
        // is the gate on the three welfare batch operations.
        //
        // 403 with a reason, not the 404 used for out-of-scope records: no record's existence is
        // being confirmed, the caller's own role is the whole answer, and they need to be told which
        // so they ask an administrator instead of retrying.
        var scopeRefusal = await ScopedCallerRefusalAsync();
        if (scopeRefusal != null) return scopeRefusal;

        var required = RequiredPermission(operation);
        if (!await HasPermissionAsync(required))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "FORBIDDEN",
                message = $"This needs the '{required}' permission — the same one that governs doing this one record at a time."
            });

        return null;
    }

    /// <summary>
    /// A batch is never allowed to do something the caller could not do singly. Undo is gated on
    /// the same permission as the operation it reverses, for the same reason.
    /// </summary>
    /// <summary>
    /// Refuses a class-scoped caller, or null for everyone else.
    ///
    /// Called from <see cref="GuardAsync"/> and, separately, as the FIRST thing Undo does. Undo
    /// has to read the job before it knows which operation to check the permission for, so if the
    /// scope check waited for GuardAsync a scoped caller could tell an existing job (403) from a
    /// missing one (404) by the status code alone. Cheap to run twice; the scope service memoises
    /// per request.
    /// </summary>
    private async Task<IActionResult?> ScopedCallerRefusalAsync()
    {
        if (await _scope.IsUnscopedAsync()) return null;

        return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
        {
            Title = "A class teacher cannot run bulk operations",
            Detail = "A batch is applied by a background job, which cannot narrow it to your classes. Change these records one at a time, or ask an administrator to run the batch.",
            Status = StatusCodes.Status403Forbidden
        });
    }

    private static string RequiredPermission(BatchOperation operation) => operation switch
    {
        BatchOperation.AdvanceClass or BatchOperation.SetField or BatchOperation.Deactivate or BatchOperation.SetConsent
            => Permissions.StudentsManage,

        BatchOperation.AssignWelfareAction or BatchOperation.SetWelfareReviewDate or BatchOperation.SetWelfareStatus
            => Permissions.WelfareEdit,

        BatchOperation.CheckOutVisitors => Permissions.VisitorsCheckOut,
        BatchOperation.CancelTokens => Permissions.TokensCancel,
        BatchOperation.CancelAppointments => Permissions.TokensCancel,

        BatchOperation.SetUserActive or BatchOperation.SetUserRole => Permissions.UsersEdit,

        _ => Permissions.SettingsView
    };

    /// <summary>The same inline check StudentsController does — one query against the caller's role.</summary>
    private async Task<bool> HasPermissionAsync(string permissionCode)
    {
        var userId = CurrentUserId();
        if (!userId.HasValue) return false;

        return await _context.Users
            .Where(u => u.Id == userId.Value && u.IsActive)
            .SelectMany(u => u.Role!.RolePermissions)
            .AnyAsync(rp => rp.Permission!.Code == permissionCode);
    }

    private Guid? CurrentUserId()
    {
        var id = _tenantAccessor.TenantContext?.UserId;
        return id == Guid.Empty ? null : id;
    }
}
