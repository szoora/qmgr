using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Filters;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;
using QMgr.Infrastructure.Services.Storage;
using QMgr.Domain.Entities.Notification;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// MINUTES OF A MEETING. The standards this implements are set out at the top of
/// Q-Mgr.Shared/Application/DTOs/MinutesDto.cs; the rules that are easy to break are here.
///
/// <para><b>ATTENDANCE IS THE REGISTER, NEVER RETYPED.</b> <see cref="BuildAttendanceAsync"/> reads
/// the duty's own Final performance records — the marks somebody already took — and turns them into
/// present / late / absent / apologies. Excused IS "apologies received" in the language of minutes.
/// Nothing anywhere writes an attendance figure into the minutes document, so the minutes and the
/// register can never disagree.</para>
///
/// <para><b>ADOPTION IS THE BOUNDARY.</b> Draft and Circulated are editable; Approved is not, ever,
/// by anyone. A change after adoption is an append-only <see cref="MinutesCorrectionDto"/> carrying
/// who, when and what — the same shape as a welfare visibility change and a reopened register, and
/// ISO 15489's integrity property in this codebase's own idiom. There is deliberately no "unapprove".</para>
///
/// <para><b>WHO MAY DO WHAT.</b> Writing and adopting need <c>staff.duties.manage</c> or membership
/// of the meeting's own recorder list — the minutes-taker delegation the duty already carries.
/// READING is wider and deliberately so: a circulated or adopted set of minutes is readable by
/// everybody who was expected at the meeting, because circulation for correction is the point of
/// the Circulated rung. A draft is readable only by the people who may write it.</para>
///
/// <para><b>A CLOSED PERIOD REFUSES ADOPTION, NOT A DRAFT.</b> Same call as the register: writing
/// the record of a meeting that happened is never blocked, but the act that makes it official is.</para>
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffMinutesController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly QMgr.Application.Interfaces.INotificationService _notifications;
    private readonly ILogger<StaffMinutesController> _logger;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public StaffMinutesController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        QMgr.Application.Interfaces.INotificationService notifications,
        ILogger<StaffMinutesController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>The document half of the minutes — everything that is not an action point.</summary>
    private sealed record MinutesDocument
    {
        public List<MinutesSectionAnswerDto> Sections { get; set; } = new();
        public List<MinutesDecisionDto> Decisions { get; set; } = new();
        public List<MinutesCorrectionDto> Corrections { get; set; } = new();
    }

    // =============================================================================================
    // Read
    // =============================================================================================

    [HttpGet("branches/{branchId:guid}/staff/duties/{dutyId:guid}/minutes")]
    [ProducesResponseType(typeof(DutyMinutesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMinutes(Guid branchId, Guid dutyId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await Db.StaffDuties.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundProblem("Meeting not found");

        var dto = await BuildAsync(duty, organizationId, branchId);

        // 404, never 403: whether a meeting exists is itself information (the rule VerifyStudentAccess set).
        if (!dto.CanRead && !dto.CanWrite) return NotFoundProblem("Minutes not found");
        return Ok(dto);
    }

    /// <summary>
    /// The caller's own open action points, across every meeting. The portal's to-do line and the
    /// "matters arising" list both read this, so there is one definition of "my actions".
    /// </summary>
    [HttpGet("branches/{branchId:guid}/staff/minutes/my-actions")]
    [ProducesResponseType(typeof(List<MinuteActionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyActions(Guid branchId, [FromQuery] bool includeDone = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var rows = await Db.StaffMinuteActions.AsNoTracking()
            .Include(a => a.Duty)
            .Where(a => a.OrganizationId == organizationId && a.BranchId == branchId && a.AssignedUserId == me
                        && (includeDone || a.Status == MinuteActionStatus.Open))
            .OrderBy(a => a.DueAt == null).ThenBy(a => a.DueAt).ThenByDescending(a => a.CreatedAt)
            .Take(200)
            .ToListAsync();

        var names = await StaffLookups.LoadNamesAsync(Db, rows.SelectMany(r => new[] { r.AssignedUserId, r.CompletedByUserId }));
        return Ok(rows.Select(r => ToDto(r, names)).ToList());
    }

    // =============================================================================================
    // Write
    // =============================================================================================

    /// <summary>Saves the draft. Sections, motions and actions are replaced wholesale — until adoption the minutes ARE this document.</summary>
    // NOT PUT .../minutes — StaffDutiesController already answers that, attaching the Library
    // document. Two actions on one method and path is an AmbiguousMatchException at runtime, and the
    // build says nothing about it.
    [HttpPut("branches/{branchId:guid}/staff/duties/{dutyId:guid}/minutes/content")]
    [ProducesResponseType(typeof(DutyMinutesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SaveMinutes(Guid branchId, Guid dutyId, [FromBody] SaveMinutesRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundProblem("Meeting not found");
        if (!await CanWriteAsync(duty)) return NotFoundProblem("Minutes not found");

        if (duty.MinutesStatus == MinutesStatus.Approved)
            return ConflictProblem("These minutes have been adopted",
                "An adopted record is never edited. Add a correction instead — it is kept beside the adopted text, with who made it and why.");

        foreach (var a in request.Actions ?? new())
            if (string.IsNullOrWhiteSpace(a.Text))
                return BadRequestProblem("An action needs wording", "Say what is to be done, or remove the line.");

        var me = CurrentUserId();
        var doc = ReadDocument(duty);
        doc.Sections = (request.Sections ?? new()).Where(s => !string.IsNullOrWhiteSpace(s.Key)).ToList();
        doc.Decisions = (request.Decisions ?? new()).Where(d => !string.IsNullOrWhiteSpace(d.Text)).ToList();
        foreach (var d in doc.Decisions) if (d.Id == Guid.Empty) d.Id = Guid.NewGuid();

        duty.MinutesJson = JsonSerializer.Serialize(doc, Json);
        if (duty.MinutesStatus == MinutesStatus.None) duty.MinutesStatus = MinutesStatus.Draft;
        duty.MinutesUpdatedAt = DateTime.UtcNow;
        duty.MinutesUpdatedByUserId = me;

        await SyncActionsAsync(duty, organizationId, branchId, request.Actions ?? new(), me);
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.MinutesSaved, nameof(StaffDuty), duty.Id, null,
            $"Minutes drafted for \"{duty.Title}\"",
            new { Sections = doc.Sections.Count, Decisions = doc.Decisions.Count, Actions = (request.Actions ?? new()).Count },
            branchId, organizationId);

        return Ok(await BuildAsync(duty, organizationId, branchId));
    }

    /// <summary>
    /// Circulates the draft to everybody who was expected, for correction. This is the step that
    /// makes a draft readable by the meeting — before it, only its writers can see it.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/staff/duties/{dutyId:guid}/minutes/circulate")]
    [ProducesResponseType(typeof(DutyMinutesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Circulate(Guid branchId, Guid dutyId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundProblem("Meeting not found");
        if (!await CanWriteAsync(duty)) return NotFoundProblem("Minutes not found");

        if (duty.MinutesStatus == MinutesStatus.None)
            return BadRequestProblem("There is nothing to circulate", "Write the minutes first.");
        if (duty.MinutesStatus == MinutesStatus.Approved)
            return ConflictProblem("These minutes have already been adopted");

        duty.MinutesStatus = MinutesStatus.Circulated;
        duty.MinutesCirculatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.MinutesCirculated, nameof(StaffDuty), duty.Id, null,
            $"Minutes circulated for \"{duty.Title}\"", null, branchId, organizationId);

        // Everyone expected is told, because circulation exists so that they can correct it.
        await NotifyExpectedAsync(duty, organizationId, branchId,
            "Minutes to check",
            $"The draft minutes of \"{duty.Title}\" have been circulated. Read them and say if anything is wrong before they are adopted.",
            $"/admin/staff/duties/{duty.Id}/minutes");

        return Ok(await BuildAsync(duty, organizationId, branchId));
    }

    /// <summary>
    /// Adoption. After this the record is official and immutable; the only further change is a
    /// correction. The adopting meeting is recorded, because "adopted at the meeting of 4 October"
    /// is what the record has to be able to state.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/staff/duties/{dutyId:guid}/minutes/approve")]
    [ProducesResponseType(typeof(DutyMinutesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(Guid branchId, Guid dutyId, [FromBody] ApproveMinutesRequest? request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundProblem("Meeting not found");

        // Adopting is a manager act, not a minute-taker's: the body adopts its own record.
        if (!await HasPermissionAsync(Permissions.StaffDutiesManage)) return NotFoundProblem("Minutes not found");

        if (duty.MinutesStatus == MinutesStatus.None)
            return BadRequestProblem("There is nothing to adopt", "Write the minutes first.");
        if (duty.MinutesStatus == MinutesStatus.Approved)
            return ConflictProblem("These minutes have already been adopted");
        // G4 (2026-09-26): whoever last wrote the minutes does not adopt them; the check below compares MEETINGS, never people.
        if (DutySeparation.Refusal(CurrentUserId(), duty.MinutesUpdatedByUserId, "adopt minutes you wrote") is { } wrote)
            return DutySeparation.Problem(wrote);

        var policy = await _policy.GetAsync(organizationId);
        if (_policy.ClosureFor(policy, duty.StartsAt) is { } closure)
            return ConflictProblem($"{_policy.FindPeriod(policy, closure.Key)?.Name ?? closure.Key} is closed",
                "Adopting minutes dated in a closed period would change a record that has been signed off. An approver can reopen the period first.");

        if (request?.ApprovedAtDutyId is { } atId)
        {
            var adopting = await Db.StaffDuties.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == atId && d.BranchId == branchId && d.OrganizationId == organizationId && d.IsActive);
            if (adopting == null)
                return BadRequestProblem("That meeting was not found", "Name a meeting of this branch, or leave it blank.");
            if (adopting.Id == duty.Id)
                return BadRequestProblem("A meeting cannot adopt its own minutes", "Minutes are adopted by the NEXT meeting.");
            duty.MinutesApprovedAtDutyId = adopting.Id;
        }

        duty.MinutesStatus = MinutesStatus.Approved;
        duty.MinutesApprovedAt = DateTime.UtcNow;
        duty.MinutesApprovedByUserId = CurrentUserId();
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.MinutesApproved, nameof(StaffDuty), duty.Id, null,
            $"Minutes adopted for \"{duty.Title}\"", new { duty.MinutesApprovedAtDutyId }, branchId, organizationId);

        await NotifyExpectedAsync(duty, organizationId, branchId,
            "Minutes adopted",
            $"The minutes of \"{duty.Title}\" have been adopted and are now the record of that meeting.",
            $"/admin/staff/duties/{duty.Id}/minutes");

        return Ok(await BuildAsync(duty, organizationId, branchId));
    }

    /// <summary>A correction to an adopted record. Appended, never applied — the adopted text stands.</summary>
    [HttpPost("branches/{branchId:guid}/staff/duties/{dutyId:guid}/minutes/corrections")]
    [ProducesResponseType(typeof(DutyMinutesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid branchId, Guid dutyId, [FromBody] CorrectMinutesRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundProblem("Meeting not found");
        if (!await CanWriteAsync(duty)) return NotFoundProblem("Minutes not found");

        if (duty.MinutesStatus != MinutesStatus.Approved)
            return BadRequestProblem("These minutes have not been adopted", "Until they are adopted, edit them directly.");
        if (string.IsNullOrWhiteSpace(request?.Text) || request.Text.Trim().Length < 10)
            return BadRequestProblem("Say what the correction is", "Ten characters or more — the correction IS the record of what was wrong.");

        var me = CurrentUserId();
        var names = await StaffLookups.LoadNamesAsync(Db, new Guid?[] { me });
        var doc = ReadDocument(duty);
        doc.Corrections.Add(new MinutesCorrectionDto
        {
            Id = Guid.NewGuid(),
            At = DateTime.UtcNow,
            ByUserId = me,
            ByName = names[me],
            Text = request.Text.Trim()
        });
        duty.MinutesJson = JsonSerializer.Serialize(doc, Json);
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.MinutesCorrected, nameof(StaffDuty), duty.Id, null,
            $"Correction added to the adopted minutes of \"{duty.Title}\"", null, branchId, organizationId);

        return Ok(await BuildAsync(duty, organizationId, branchId));
    }

    /// <summary>Closes an action point. The assignee or anyone who may write the minutes.</summary>
    [HttpPost("branches/{branchId:guid}/staff/minutes/actions/{actionId:guid}/complete")]
    [ProducesResponseType(typeof(MinuteActionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CompleteAction(Guid branchId, Guid actionId, [FromBody] CompleteMinuteActionRequest? request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var action = await Db.StaffMinuteActions.Include(a => a.Duty)
            .FirstOrDefaultAsync(a => a.Id == actionId && a.BranchId == branchId && a.OrganizationId == organizationId);
        if (action == null) return NotFoundProblem("Action not found");

        var me = CurrentUserId();
        var mine = action.AssignedUserId == me;
        if (!mine && !(action.Duty != null && await CanWriteAsync(action.Duty))) return NotFoundProblem("Action not found");

        if (action.Status == MinuteActionStatus.Open)
        {
            action.Status = MinuteActionStatus.Done;
            action.CompletedAt = DateTime.UtcNow;
            action.CompletedByUserId = me;
            action.CompletionNote = string.IsNullOrWhiteSpace(request?.Note) ? null : request!.Note!.Trim();
            await Db.SaveChangesAsync();

            await Activity.RecordAsync(ActivityActions.MinuteActionCompleted, nameof(StaffMinuteAction), action.Id, action.AssignedUserId,
                $"Action closed: \"{Shorten(action.Text)}\"", null, branchId, organizationId);
        }

        var names = await StaffLookups.LoadNamesAsync(Db, new Guid?[] { action.AssignedUserId, action.CompletedByUserId });
        return Ok(ToDto(action, names));
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "…";

    private MinutesDocument ReadDocument(StaffDuty duty)
    {
        if (string.IsNullOrWhiteSpace(duty.MinutesJson)) return new MinutesDocument();
        try { return JsonSerializer.Deserialize<MinutesDocument>(duty.MinutesJson, Json) ?? new MinutesDocument(); }
        catch (JsonException ex)
        {
            // A blob we cannot read must never take the page down; it is reported and treated as empty.
            _logger.LogError(ex, "Minutes JSON on duty {DutyId} could not be read", duty.Id);
            return new MinutesDocument();
        }
    }

    /// <summary>staff.duties.manage, or a named recorder of this meeting — the minutes-taker delegation.</summary>
    private async Task<bool> CanWriteAsync(StaffDuty duty)
        => await HasPermissionAsync(Permissions.StaffDutiesManage) || duty.RecorderUserIds.Contains(CurrentUserId());

    /// <summary>
    /// Replaces the action list. An existing id is UPDATED rather than deleted and re-inserted, so
    /// its reminder stage, its completion and its history survive an edit of the minutes around it.
    /// A line that disappears from the document is Cancelled, not deleted: the minutes said it.
    /// </summary>
    private async Task SyncActionsAsync(StaffDuty duty, Guid organizationId, Guid branchId, List<SaveMinuteActionRequest> wanted, Guid me)
    {
        var existing = await Db.StaffMinuteActions.Where(a => a.DutyId == duty.Id).ToListAsync();
        var keep = new HashSet<Guid>();

        foreach (var w in wanted)
        {
            var row = w.Id is { } id ? existing.FirstOrDefault(a => a.Id == id) : null;
            if (row == null)
            {
                row = new StaffMinuteAction
                {
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    DutyId = duty.Id,
                    CreatedBy = me
                };
                Db.StaffMinuteActions.Add(row);
            }

            // A due date that moves earlier should be chased again, so the claimed stage is released.
            if (row.DueAt != w.DueAt) row.ReminderStage = 0;

            row.Text = w.Text.Trim();
            row.AssignedUserId = w.AssignedUserId;
            row.DueAt = w.DueAt;
            if (w.Status != MinuteActionStatus.Done || row.Status == MinuteActionStatus.Done) row.Status = w.Status;
            if (row.Status != MinuteActionStatus.Done) { row.CompletedAt = null; row.CompletedByUserId = null; }
            row.UpdatedBy = me;
            if (row.Id != Guid.Empty) keep.Add(row.Id);
        }

        foreach (var gone in existing.Where(a => !keep.Contains(a.Id) && a.Status == MinuteActionStatus.Open))
            gone.Status = MinuteActionStatus.Cancelled;
    }

    private static MinuteActionDto ToDto(StaffMinuteAction a, StaffPerformanceMapping.NameLookup names) => new()
    {
        Id = a.Id,
        DutyId = a.DutyId,
        DutyTitle = a.Duty?.Title ?? string.Empty,
        Text = a.Text,
        AssignedUserId = a.AssignedUserId,
        AssignedName = a.AssignedUserId is { } u ? names[u] : null,
        DueAt = a.DueAt,
        Status = a.Status,
        CompletedAt = a.CompletedAt,
        CompletedByName = a.CompletedByUserId is { } c ? names[c] : null,
        CompletionNote = a.CompletionNote,
        IsOverdue = a.Status == MinuteActionStatus.Open && a.DueAt is { } due && due < DateTime.UtcNow
    };

    private async Task<DutyMinutesDto> BuildAsync(StaffDuty duty, Guid organizationId, Guid branchId)
    {
        var policy = await _policy.GetAsync(organizationId);
        var doc = ReadDocument(duty);
        var me = CurrentUserId();

        var canWrite = await CanWriteAsync(duty);
        var expected = duty.ExpectedUserIds?.ToHashSet();
        var expectedOfMe = expected == null || expected.Contains(me);
        var canRead = canWrite
                      || (duty.MinutesStatus == MinutesStatus.Approved && (expectedOfMe || await HasPermissionAsync(Permissions.StaffRecordsView)))
                      || (duty.MinutesStatus == MinutesStatus.Circulated && expectedOfMe);

        var actions = await Db.StaffMinuteActions.AsNoTracking()
            .Where(a => a.DutyId == duty.Id)
            .OrderBy(a => a.Status).ThenBy(a => a.DueAt == null).ThenBy(a => a.DueAt).ThenBy(a => a.CreatedAt)
            .ToListAsync();

        var names = await StaffLookups.LoadNamesAsync(Db, actions
            .SelectMany(a => new[] { a.AssignedUserId, a.CompletedByUserId })
            .Append(duty.MinutesApprovedByUserId)
            .Append(duty.MinutesUpdatedByUserId));

        string? adoptingTitle = null;
        if (duty.MinutesApprovedAtDutyId is { } adoptedAt)
            adoptingTitle = await Db.StaffDuties.AsNoTracking().Where(d => d.Id == adoptedAt).Select(d => d.Title).FirstOrDefaultAsync();

        string? fileUrl = null;
        if (duty.MinutesMediaContentId is { } mediaId)
        {
            var raw = await Db.MediaContents.AsNoTracking().Where(m => m.Id == mediaId).Select(m => m.FileUrl).FirstOrDefaultAsync();
            fileUrl = raw == null ? null : UploadLinks.Sign(raw) ?? raw;
        }

        return new DutyMinutesDto
        {
            DutyId = duty.Id,
            DutyTitle = duty.Title,
            Location = duty.Location,
            StartsAt = duty.StartsAt,
            EndsAt = duty.EndsAt,
            Status = duty.MinutesStatus,
            CirculatedAt = duty.MinutesCirculatedAt,
            ApprovedAt = duty.MinutesApprovedAt,
            ApprovedByName = duty.MinutesApprovedByUserId is { } ab ? names[ab] : null,
            ApprovedAtDutyId = duty.MinutesApprovedAtDutyId,
            ApprovedAtDutyTitle = adoptingTitle,
            UpdatedAt = duty.MinutesUpdatedAt,
            UpdatedByName = duty.MinutesUpdatedByUserId is { } ub ? names[ub] : null,
            Template = _policy.MinutesTemplate(policy).ToList(),
            Sections = doc.Sections,
            Decisions = doc.Decisions,
            Corrections = doc.Corrections.OrderBy(c => c.At).ToList(),
            Actions = actions.Select(a => ToDto(a, names)).ToList(),
            Attendance = await BuildAttendanceAsync(duty, organizationId, branchId, policy),
            MinutesMediaContentId = duty.MinutesMediaContentId,
            MinutesFileUrl = fileUrl,
            CanWrite = canWrite && duty.MinutesStatus != MinutesStatus.Approved,
            CanApprove = await HasPermissionAsync(Permissions.StaffDutiesManage) && duty.MinutesStatus is MinutesStatus.Draft or MinutesStatus.Circulated
                         && DutySeparation.Allows(CurrentUserId(), duty.MinutesUpdatedByUserId),
            CanRead = canRead
        };
    }

    /// <summary>
    /// THE REGISTER IS THE ATTENDANCE. Reads the duty's own Final records — the marks somebody
    /// already took — and names them in the language of minutes. Nothing here is stored, so the
    /// minutes cannot drift from the register, and a correction to the register shows up here.
    /// </summary>
    private async Task<MinutesAttendanceDto> BuildAttendanceAsync(StaffDuty duty, Guid organizationId, Guid branchId, StaffPerformancePolicyDto policy)
    {
        var expectedIds = duty.ExpectedUserIds?.ToList()
                          ?? await StaffLookups.BranchStaff(Db, organizationId, branchId).Select(u => u.Id).ToListAsync();

        var marks = await Db.StaffPerformanceRecords.AsNoTracking()
            // NOT `r.Outcome != null`: Outcome is a non-nullable DutyOutcome, so that comparison is
            // always true (CS0472 on every build) and every Final record on the duty entered the
            // attendance — including ones carrying no mark at all, which default to NotApplicable.
            // Attendance IS the register and nothing else, so a record that is not a mark is not a row.
            .Where(r => r.DutyId == duty.Id && r.Status == StaffRecordStatus.Final && r.Outcome != DutyOutcome.NotApplicable)
            .Select(r => new { r.SubjectUserId, r.Outcome })
            .ToListAsync();

        // A person marked more than once (a reopened register annuls and rewrites) keeps their last mark.
        var latest = marks.GroupBy(m => m.SubjectUserId).ToDictionary(g => g.Key, g => g.Last().Outcome);
        var names = await StaffLookups.LoadNamesAsync(Db, expectedIds.Select(id => (Guid?)id));

        var dto = new MinutesAttendanceDto { Expected = expectedIds.Count, RegisterClosed = duty.RegisterClosedAt.HasValue };
        foreach (var id in expectedIds)
        {
            var name = names[id];
            DutyOutcome? mark = latest.TryGetValue(id, out var o) ? o : null;
            switch (mark)
            {
                case DutyOutcome.Present or DutyOutcome.Completed or DutyOutcome.Recovered:
                    dto.Present++; if (name.Length > 0) dto.PresentNames.Add(name); break;
                case DutyOutcome.Late:
                    // Late is still present at the meeting, and the minutes say both.
                    dto.Late++; dto.Present++; if (name.Length > 0) dto.PresentNames.Add(name); break;
                case DutyOutcome.Excused:
                    dto.Apologies++; if (name.Length > 0) dto.ApologyNames.Add(name); break;
                case DutyOutcome.Absent or DutyOutcome.NotCompleted:
                    dto.Absent++; if (name.Length > 0) dto.AbsentNames.Add(name); break;
                default:
                    dto.Unmarked++; break;
            }
        }
        dto.PresentNames.Sort(StringComparer.OrdinalIgnoreCase);
        dto.AbsentNames.Sort(StringComparer.OrdinalIgnoreCase);
        dto.ApologyNames.Sort(StringComparer.OrdinalIgnoreCase);

        // Quorum. Off unless the tenant set a percentage — a school staff meeting normally has none,
        // and a page announcing "quorum not met" at every meeting trains people to ignore it.
        var percent = policy.MinutesDefaults?.QuorumPercent ?? 0;
        if (percent > 0 && dto.Expected > 0)
        {
            dto.QuorumRequired = (int)Math.Ceiling(dto.Expected * percent / 100.0);
            dto.QuorumMet = dto.Present >= dto.QuorumRequired;
        }
        return dto;
    }

    /// <summary>
    /// Tells everybody the meeting expected. Never throws: a notification that did not land must not
    /// fail a transition that has already been written — the TryIssueVisitToken rule.
    /// </summary>
    private async Task NotifyExpectedAsync(StaffDuty duty, Guid organizationId, Guid branchId, string title, string message, string actionUrl)
    {
        try
        {
            var recipients = duty.ExpectedUserIds?.ToList()
                             ?? await StaffLookups.BranchStaff(Db, organizationId, branchId).Select(u => u.Id).ToListAsync();
            foreach (var userId in recipients.Distinct())
            {
                await _notifications.CreateInAppNotificationAsync(new QMgr.Application.Interfaces.CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    Title = title,
                    Message = message,
                    Type = NotificationType.StaffPerformance,
                    ActionUrl = actionUrl,
                    EventKey = NotificationEventKeys.StaffMinutes
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Minutes notification for duty {DutyId} could not be sent", duty.Id);
        }
    }
}
