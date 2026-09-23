using System.Globalization;
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
/// The staff ledger: records, their follow-up notes, evidence, the subject's right of reply, a
/// person's timeline and score, and peer recognition. The staff analogue of WelfareController and
/// the same rules — append-only rows, an "edit" is a note, a void is Annulled with a reason, the
/// visibility rung is the one mutable field and changing it writes a note.
///
/// Three things are different from the student side and are deliberate:
///  - THE SUBJECT IS A USER OF THIS SYSTEM. They read their own Standard and Confidential records
///    (never Restricted), respond to them, acknowledge them and add evidence to them. A child
///    cannot do any of that.
///  - THE AUTHOR KEEPS WHAT THEY WROTE. A lesson observation defaults to Confidential; a head of
///    department without staff.confidential.view must still see the observation they filed an hour
///    ago. Reading your own words is not a leak. Restricted stays administrator-only even for the
///    author, exactly as in welfare.
///  - SIGN RULES COME FROM THE PARAMETER'S KIND, not from a case type on the record: Contribution
///    and Recognition positive, Conduct negative, Attendance and Duty by outcome, Wellbeing never.
///
/// Every write is followed by an IActivityLogger call whose summary is written at the ACTOR's
/// visibility — never the description of a Confidential or Restricted record. Every path that
/// reaches another person's record goes through IStaffScopeService and answers 404, never 403.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — actions carry their own [RequirePermission] or an explicit self check
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffRecordsController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly IStaffScoringService _scoring;
    private readonly IStaffAlertService _alerts;
    private readonly INotificationService _notifications;
    private readonly IMediaStorageService _mediaStorage;
    private readonly ILogger<StaffRecordsController> _logger;

    // 25MB, the welfare figure, for the welfare reason: local disk, no CDN tier, and video at any
    // real length is a capacity conversation rather than a constant.
    private const long MaxAttachmentSizeBytes = 25 * 1024 * 1024;
    private static readonly string[] AllowedAttachmentMimePrefixes = { "image/", "application/pdf", "video/", "audio/" };
    private const int MaxPageSize = 200;
    private const int TimelineActivityRows = 500;

    public StaffRecordsController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        IStaffScoringService scoring,
        IStaffAlertService alerts,
        INotificationService notifications,
        IMediaStorageService mediaStorage,
        ILogger<StaffRecordsController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _scoring = scoring;
        _alerts = alerts;
        _notifications = notifications;
        _mediaStorage = mediaStorage;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Search and create
    // ---------------------------------------------------------------------

    /// <summary>
    /// Branch-wide search, narrowed three ways: the caller's staff scope, the rungs they may read,
    /// and drafts only to their author. The caller's own records and the ones they wrote are always
    /// in (below Restricted), since a head searching "my department" must find their own rows too.
    /// </summary>
    [HttpGet("records")]
    [RequirePermission(Permissions.StaffRecordsView)]
    [ProducesResponseType(typeof(StaffRecordSearchResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchRecords(
        Guid branchId,
        [FromQuery] Guid? subjectUserId = null,
        [FromQuery] Guid? parameterId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? status = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        StaffRecordStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<StaffRecordStatus>(status, true, out var parsed))
                return BadRequestProblem("Unrecognised status", "Use Draft, Final or Annulled.");
            statusFilter = parsed;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var me = CurrentUserId();
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);

        var query = RecordsWithIncludes().Where(r => r.BranchId == branchId);
        query = await StaffScope.ApplyAsync(query, branchId);
        query = await ApplyRungAsync(query, me);

        if (subjectUserId.HasValue) query = query.Where(r => r.SubjectUserId == subjectUserId.Value);
        if (parameterId.HasValue) query = query.Where(r => r.ParameterId == parameterId.Value);
        if (from.HasValue) query = query.Where(r => r.OccurredAt >= DateTime.SpecifyKind(from.Value, DateTimeKind.Utc));
        if (to.HasValue) query = query.Where(r => r.OccurredAt <= DateTime.SpecifyKind(to.Value, DateTimeKind.Utc));
        if (statusFilter.HasValue) query = query.Where(r => r.Status == statusFilter.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            // A person's name is searched in BOTH orders: whoever is typing does not know, and should not
            // have to know, which order this school shows names in (PersonNames).
            query = query.Where(r => r.Description.ToLower().Contains(term) || r.Parameter!.Name.ToLower().Contains(term)
                                     || (r.Subject!.FirstName + " " + r.Subject.LastName).ToLower().Contains(term)   // name-format: search
                                     || (r.Subject!.LastName + " " + r.Subject.FirstName).ToLower().Contains(term)); // name-format: search
        }

        var total = await query.CountAsync();
        var records = await query.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();

        var names = await NamesForAsync(records);
        return Ok(new StaffRecordSearchResultDto
        {
            Items = records.Select(r => StaffPerformanceMapping.ToDto(r, names, policy.LateEntryThresholdDays)).ToList(),
            TotalCount = total,
            ScopedToDepartments = (await StaffScope.GetScopedDepartmentNamesAsync()).ToList()
        });
    }

    /// <summary>
    /// Logs a record. The subject must be in the caller's scope (a scope that only covers reads is
    /// half a scope — Phase 77's lesson), the parameter must be active and apply to the subject's
    /// staff group, points obey the kind's sign rule and the per-entry cap, an observation needs a
    /// rating on its scale, and the visibility is decided here: a caller may ASK for a rung they
    /// can read; the parameter's default and the Wellbeing floor are applied on top regardless.
    ///
    /// Every rule lives in <see cref="ResolveSubjectForWriteAsync"/>, <see cref="ResolveParameterForWriteAsync"/>
    /// and <see cref="BuildRecordAsync"/>, and every side effect in <see cref="AfterRecordSavedAsync"/> — the group
    /// log calls the same four, so a rule added here binds both and neither can drift into a second copy.
    /// </summary>
    [HttpPost("records")]
    [RequirePermission(Permissions.StaffRecordsCreate)]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateRecord(Guid branchId, [FromBody] CreateStaffRecordRequest request, [FromQuery] bool acknowledgeLateEntry = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);

        // The subject: exists, is staff of this branch, and is in scope. One wording for all three
        // refusals, so a probe cannot learn which it was.
        var subject = await ResolveSubjectForWriteAsync(organizationId, branchId, request.SubjectUserId);
        if (subject == null) return StaffMemberNotFound();

        var (parameterError, parameter) = await ResolveParameterForWriteAsync(organizationId, request.ParameterId);
        if (parameterError != null) return parameterError;

        var policy = await _policy.GetAsync(organizationId);
        var (error, built) = await BuildRecordAsync(branchId, organizationId, request, acknowledgeLateEntry, subject, parameter!, policy);
        if (error != null) return error;

        AddBuilt(built!);
        await Db.SaveChangesAsync();

        await AfterRecordSavedAsync(built!, branchId, organizationId, alert: true);

        var dto = await LoadDtoAsync(built!.Record.Id, branchId);
        return CreatedAtAction(nameof(GetRecord), new { branchId, id = built.Record.Id }, dto);
    }

    /// <summary>
    /// Logs the same Contribution or Conduct record for several members of staff — "everyone who ran the open
    /// day", "the whole department missed the report deadline" (the Staff Directory's "Log a record", 2026-09-23).
    /// The staff twin of the welfare group log, and the rules are deliberate:
    /// <list type="bullet">
    /// <item>ONLY CONTRIBUTION AND CONDUCT. Attendance and duties come from registers; an observation and a
    /// wellbeing record are about one person; recognition has its own monthly allowance, which a group log would
    /// walk straight past.</item>
    /// <item>ONLY STANDARD. A confidential or restricted record is that person's own file.</item>
    /// <item>NOBODY LOGS ABOUT THEMSELVES IN BULK. The caller is taken out of the list and named back in
    /// <c>SkippedSelf</c> rather than refusing the batch: ticking "everyone in Maths" includes the head.</item>
    /// <item>SCOPE, THE WHOLE BATCH: every person passes the check the single create runs, and one that does not
    /// refuses the batch with the SAME not-found wording — a distinct "not in your department" would confirm the
    /// person exists.</item>
    /// <item>THE SAME CREATION CODE per record (<see cref="BuildRecordAsync"/>): points, the closed period and the
    /// late-entry 409 bind exactly as for one. ONE TRANSACTION, AT MOST 200, all or nothing.</item>
    /// <item>ALERTS COALESCED: each subject is told about their own record as the single create tells them; a
    /// supervisor gets ONE summary for the batch, not one per colleague.</item>
    /// <item>NO LOCK. Nothing here is an "at most N" or "exactly once" rule: two identical presses are two
    /// batches, each complete, which is what the annul path is for.</item>
    /// </list>
    /// </summary>
    [HttpPost("records/bulk")]
    [RequirePermission(Permissions.StaffRecordsCreate)]
    [ProducesResponseType(typeof(BulkStaffRecordResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateRecordsBulk(Guid branchId, [FromBody] BulkStaffRecordRequest request, [FromQuery] bool acknowledgeLateEntry = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (request?.Record == null) return BadRequestProblem("Describe the record to log");

        var me = CurrentUserId();
        var ids = (request.SubjectUserIds ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return BadRequestProblem("Choose at least one person");
        // The cap first, before a single row is read: 5,000 ids must cost one comparison, not 5,000 lookups.
        if (ids.Count > StaffBulkLimits.MaxPeople) return BadRequestProblem(StaffBulkLimits.CapMessage);

        var skippedSelf = ids.Where(id => id == me).ToList();
        ids = ids.Where(id => id != me).ToList();
        if (ids.Count == 0) return BadRequestProblem(StaffBulkLimits.OnlySelfRefusal);

        if (request.Record.SaveAsDraft) return BadRequestProblem(StaffBulkLimits.DraftRefusal);
        if (request.Record.Visibility != WelfareVisibility.Standard) return BadRequestProblem(StaffBulkLimits.VisibilityRefusal);

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var (parameterError, parameter) = await ResolveParameterForWriteAsync(organizationId, request.Record.ParameterId);
        if (parameterError != null) return parameterError;
        if (!StaffBulkLimits.AllowsKind(parameter!.Kind)) return BadRequestProblem(StaffBulkLimits.KindRefusal);
        // A parameter whose own default is above Standard would raise every record of the batch there.
        if (parameter.DefaultVisibility != WelfareVisibility.Standard) return BadRequestProblem(StaffBulkLimits.VisibilityRefusal);

        // --- SCOPE, THE WHOLE BATCH, BEFORE ANYTHING ELSE ---
        var subjects = new List<User>(ids.Count);
        foreach (var id in ids)
        {
            var subject = await ResolveSubjectForWriteAsync(organizationId, branchId, id);
            if (subject == null) return StaffMemberNotFound();
            subjects.Add(subject);
        }

        // Every person passed scope, so saying how many the parameter does not cover leaks nothing.
        var outsideGroup = subjects.Count(s => !StaffGroups.Applies(parameter.AppliesToGroup, s.Role?.StaffGroup));
        if (outsideGroup > 0)
            return BadRequestProblem($"'{parameter.Name}' applies to {parameter.AppliesToGroup} only",
                $"{outsideGroup} of the {subjects.Count} people chosen {(outsideGroup == 1 ? "is" : "are")} not in that group. Leave them out and log again.");

        // --- Build every record through the one creation path. Nothing is written until all have passed. ---
        var policy = await _policy.GetAsync(organizationId);
        var built = new List<BuiltStaffRecord>(subjects.Count);
        foreach (var subject in subjects)
        {
            var one = request.Record with { SubjectUserId = subject.Id, DutyId = null, PreObservationMeetingAt = null, FeedbackSessionAt = null };
            var (error, b) = await BuildRecordAsync(branchId, organizationId, one, acknowledgeLateEntry, subject, parameter, policy);
            if (error != null) return error;
            built.Add(b!);
        }

        // --- One transaction, through the execution strategy: all or nothing. ---
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await Db.Database.BeginTransactionAsync();
            foreach (var b in built)
                if (Db.Entry(b.Record).State == EntityState.Detached) AddBuilt(b);
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        var batchId = Guid.NewGuid();
        var recordIds = built.Select(b => b.Record.Id).ToList();
        _logger.LogInformation("Staff batch {BatchId}: {Records} {Parameter} record(s) logged in branch {BranchId}",
            batchId, recordIds.Count, parameter.Name, branchId);

        // --- After the commit: nothing below may fail the request. ---
        // The per-record lines the single create writes (each person's own trail reads them)...
        foreach (var b in built) await AfterRecordSavedAsync(b, branchId, organizationId, alert: false);

        // ...ONE line for the batch, naming nobody — who was in it is in DetailJson, which no endpoint returns...
        await Activity.RecordAsync(ActivityActions.RecordsBulkLogged, "staff-record-batch", batchId, null,
            $"{parameter.Name} logged for {recordIds.Count} {(recordIds.Count == 1 ? "person" : "people")}",
            new { BatchId = batchId, ParameterId = parameter.Id, Kind = parameter.Kind.ToString(), People = subjects.Select(s => s.Id).ToList(), RecordIds = recordIds, SkippedSelf = skippedSelf.Count > 0 },
            branchId, organizationId, visibility: WelfareVisibility.Standard);

        // ...and the alerts, coalesced: each subject once, each supervisor once for the whole batch.
        var peopleAlerted = await _alerts.NotifyRecordsLoggedAsync(recordIds);

        return StatusCode(StatusCodes.Status201Created, new BulkStaffRecordResultDto
        {
            Created = recordIds.Count,
            RecordIds = recordIds,
            BatchId = batchId,
            PeopleAlerted = peopleAlerted,
            SkippedSelf = skippedSelf
        });
    }

    /// <summary>A record that has passed every rule and has not been saved, with what an observation adds beside it.</summary>
    private sealed record BuiltStaffRecord(StaffPerformanceRecord Record, User Subject, PerformanceParameter Parameter, StaffDuty? FeedbackDuty, StaffPerformanceNote? Note);

    private void AddBuilt(BuiltStaffRecord built)
    {
        Db.StaffPerformanceRecords.Add(built.Record);
        if (built.FeedbackDuty != null) Db.StaffDuties.Add(built.FeedbackDuty);
        if (built.Note != null) Db.StaffPerformanceNotes.Add(built.Note);
    }

    /// <summary>
    /// The person a record may be written about, or null: exists, is active staff of this branch, and is in the
    /// caller's scope. One answer for all three, so a probe cannot learn which it was — every caller refuses a null
    /// with StaffMemberNotFound.
    /// </summary>
    private async Task<User?> ResolveSubjectForWriteAsync(Guid organizationId, Guid branchId, Guid userId)
    {
        var subject = await FindBranchStaffAsync(organizationId, branchId, userId);
        if (subject == null || !await StaffScope.CanSeeStaffAsync(branchId, subject.Id)) return null;
        return subject;
    }

    /// <summary>The parameter a record may be logged on: this organization's, active, and not written by the system alone.</summary>
    private async Task<(IActionResult? Error, PerformanceParameter? Parameter)> ResolveParameterForWriteAsync(Guid organizationId, Guid parameterId)
    {
        var parameter = await Db.PerformanceParameters.FirstOrDefaultAsync(p => p.Id == parameterId && p.OrganizationId == organizationId);
        if (parameter == null) return (BadRequestProblem("The parameter was not found"), null);
        if (!parameter.IsActive) return (BadRequestProblem($"'{parameter.Name}' has been retired", "Choose an active parameter."), null);
        // An automatic-credit parameter is written by the system alone, so its records can always be
        // labelled "automatic" and never mistaken for a colleague's judgement.
        if (parameter.IsSystemSource) return (BadRequestProblem($"'{parameter.Name}' is credited automatically", "Records on it are written by the system when the activity happens; it cannot be logged by hand."), null);
        return (null, parameter);
    }

    /// <summary>
    /// EVERY RULE ONE STAFF RECORD OBEYS once its subject and parameter are resolved: the staff group, the content,
    /// the closed period, late entry, points, rating, visibility, the linked duty — and the entity built from them.
    /// Adds NOTHING to the context; the caller does (<see cref="AddBuilt"/>). The single create and the group log both
    /// call it, so a rule added here binds both.
    /// </summary>
    private async Task<(IActionResult? Error, BuiltStaffRecord? Built)> BuildRecordAsync(
        Guid branchId, Guid organizationId, CreateStaffRecordRequest request, bool acknowledgeLateEntry,
        User subject, PerformanceParameter parameter, StaffPerformancePolicyDto policy)
    {
        var me = CurrentUserId();
        if (parameter.Kind == ParameterKind.Recognition && subject.Id == me) return (BadRequestProblem("You cannot recognise yourself"), null);

        var group = subject.Role?.StaffGroup;
        if (!StaffGroups.Applies(parameter.AppliesToGroup, group))
            return (BadRequestProblem($"'{parameter.Name}' applies to {parameter.AppliesToGroup} only"), null);

        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length < 10)
            return (BadRequestProblem("Describe what happened", "At least 10 characters — a record that says nothing is worth nothing to the person it is about."), null);
        if (request.OccurredAt > DateTime.UtcNow.AddDays(1))
            return (BadRequestProblem("A record cannot be dated in the future"), null);

        var occurredAt = DateTime.SpecifyKind(request.OccurredAt, DateTimeKind.Utc);

        // A closed period takes no new scored evidence. Wellbeing is never scored and a person's welfare
        // must never wait for a period to be reopened, so it is the one kind that passes.
        if (parameter.Kind != ParameterKind.Wellbeing)
        {
            var closed = ClosedPeriodProblem(_policy, policy, occurredAt, "A record");
            if (closed != null) return (closed, null);
        }

        // The late-entry confirmation, enforced HERE and at the policy's threshold (the welfare shape:
        // 409 with acknowledgeLateEntry=true to proceed). Before 2026-09-17 it was a browser-only prompt
        // with 14 days hard-coded, so an API caller — or a tenant with a different threshold — skipped it.
        if (!request.SaveAsDraft && policy.LateEntryThresholdDays > 0
            && occurredAt < DateTime.UtcNow.AddDays(-policy.LateEntryThresholdDays) && !acknowledgeLateEntry)
            return (LateEntryProblem(policy.LateEntryThresholdDays), null);

        if (!Enum.IsDefined(request.Outcome)) return (BadRequestProblem("Unrecognised outcome"), null);

        var pointsError = ResolvePoints(parameter, request.Outcome, request.Points, out var points);
        if (pointsError != null) return (BadRequestProblem(pointsError), null);

        var ratingError = ResolveRating(parameter, request.Rating, out var rating);
        if (ratingError != null) return (BadRequestProblem(ratingError), null);

        var visibilityError = await ResolveVisibilityAsync(parameter, request.Visibility);
        if (visibilityError.Error != null) return (visibilityError.Error, null);

        StaffDuty? duty = null;
        if (request.DutyId.HasValue)
        {
            duty = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == request.DutyId.Value && d.BranchId == branchId);
            if (duty == null) return (BadRequestProblem("The duty was not found"), null);
        }

        var isObservation = parameter.Kind == ParameterKind.Observation;
        var record = new StaffPerformanceRecord
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            SubjectUserId = subject.Id,
            ParameterId = parameter.Id,
            DutyId = duty?.Id,
            Outcome = request.Outcome,
            Points = points,
            Rating = rating,
            Description = request.Description.Trim(),
            OccurredAt = occurredAt,
            Source = isObservation ? RecordSource.Observation : RecordSource.Manual,
            Status = request.SaveAsDraft ? StaffRecordStatus.Draft : StaffRecordStatus.Final,
            Visibility = visibilityError.Visibility,
            LoggedByUserId = me,
            CreatedBy = me
        };

        StaffDuty? feedbackDuty = null;
        StaffPerformanceNote? note = null;
        if (isObservation && (request.PreObservationMeetingAt.HasValue || request.FeedbackSessionAt.HasValue))
        {
            var parts = new List<string>();
            if (request.PreObservationMeetingAt is { } pre) parts.Add(string.Create(CultureInfo.InvariantCulture, $"Pre-observation meeting on {pre.ToUniversalTime():dd MMM yyyy HH:mm} UTC."));
            if (request.FeedbackSessionAt is { } fb) parts.Add(string.Create(CultureInfo.InvariantCulture, $"Feedback session on {fb.ToUniversalTime():dd MMM yyyy HH:mm} UTC."));

            // "Schedules the feedback session" (plan §6.3), not just remembers it: a future feedback session
            // becomes a duty on the roster, expecting the observed teacher and the observer, with the observer
            // as recorder — so both get the Coming-up entry and the duty reminder, and the meeting's own
            // attendance is taken like any other. The title names no one; the expected list is the only link.
            if (!request.SaveAsDraft && request.FeedbackSessionAt is { } session && session.ToUniversalTime() > DateTime.UtcNow)
            {
                var meetingParameter = await Db.PerformanceParameters.AsNoTracking()
                    .Where(p => p.OrganizationId == organizationId && p.IsActive && !p.IsSystemSource
                                && p.Kind == ParameterKind.Attendance && p.AppliesToGroup == null)
                    .OrderByDescending(p => p.Name == "Meeting Attendance").ThenBy(p => p.SortOrder)
                    .FirstOrDefaultAsync();
                if (meetingParameter != null)
                {
                    var startsAt = DateTime.SpecifyKind(session.ToUniversalTime(), DateTimeKind.Utc);
                    feedbackDuty = new StaffDuty
                    {
                        OrganizationId = organizationId,
                        BranchId = branchId,
                        ParameterId = meetingParameter.Id,
                        Title = "Lesson observation feedback",
                        Description = "The feedback session that follows a lesson observation.",
                        StartsAt = startsAt,
                        EndsAt = startsAt.AddMinutes(30),
                        ExpectedUserIds = subject.Id == me ? new[] { me } : new[] { subject.Id, me },
                        RecorderUserIds = new[] { me },
                        CreatedByUserId = me,
                        CreatedBy = me
                    };
                    parts.Add("The feedback session is on the duty roster, with a reminder to both of you.");
                }
            }

            note = new StaffPerformanceNote { RecordId = record.Id, Body = string.Join(" ", parts), AuthorUserId = me, Kind = StaffNoteKind.Note };
        }

        return (null, new BuiltStaffRecord(record, subject, parameter, feedbackDuty, note));
    }

    /// <summary>
    /// What follows a committed record: its RecordCreated line (at the record's rung), the feedback-session line,
    /// and — when <paramref name="alert"/> — the fan-out. The group log passes false and coalesces the alerts
    /// itself. Never throws: both the logger and the alert service swallow their own failures.
    /// </summary>
    private async Task AfterRecordSavedAsync(BuiltStaffRecord built, Guid branchId, Guid organizationId, bool alert)
    {
        var record = built.Record;
        var subjectName = StaffPerformanceMapping.FullName(built.Subject);
        await Activity.RecordAsync(ActivityActions.RecordCreated, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            RecordSummary(record.Status == StaffRecordStatus.Draft ? "drafted" : "created", record, built.Parameter.Name, subjectName),
            new { record.ParameterId, record.Outcome, record.Points, record.Rating, record.Visibility, record.Status, record.Source, record.DutyId },
            branchId, organizationId, visibility: record.Visibility);
        if (built.FeedbackDuty is { } feedbackDuty)
            await Activity.RecordAsync(ActivityActions.DutyCreated, nameof(StaffDuty), feedbackDuty.Id, null,
                string.Create(CultureInfo.InvariantCulture, $"Observation feedback session scheduled for {feedbackDuty.StartsAt:dd MMM yyyy HH:mm} UTC"),
                new { feedbackDuty.StartsAt, FromRecord = record.Id }, branchId, organizationId);

        // A committed record's fan-out must not fail the request; the service never throws.
        if (alert && record.Status == StaffRecordStatus.Final)
            await _alerts.NotifyRecordLoggedAsync(record.Id);
    }

    // ---------------------------------------------------------------------
    // One record
    // ---------------------------------------------------------------------

    [HttpGet("records/{id:guid}")]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecord(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await LoadAsync(id, branchId);
        if (record == null || !await CanReadRecordAsync(record, branchId)) return RecordNotFound();

        var me = CurrentUserId();
        if (record.SubjectUserId != me)
        {
            // The subject-access trail: who looked at my file. Reading your own is not an event on it.
            await Activity.RecordAsync(ActivityActions.RecordViewed, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
                RecordSummary("viewed", record, record.Parameter?.Name, SubjectName(record)), null, branchId, record.OrganizationId, visibility: record.Visibility);
        }

        return Ok(await ToDtoAsync(record));
    }

    /// <summary>Draft → Final, by the author only. The alert fan-out runs now, not when the draft was saved.</summary>
    [HttpPost("records/{id:guid}/finalize")]
    [RequirePermission(Permissions.StaffRecordsCreate)]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FinalizeRecord(Guid branchId, Guid id, [FromQuery] bool acknowledgeLateEntry = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        // Only the caller's own draft is findable here, so there is nothing to leak about anyone else's.
        var record = await LoadAsync(id, branchId, r => r.LoggedByUserId == me && r.Status == StaffRecordStatus.Draft);
        if (record == null) return RecordNotFound();

        var policy = await _policy.GetAsync(record.OrganizationId);
        if (record.Parameter?.Kind != ParameterKind.Wellbeing)
        {
            var closed = ClosedPeriodProblem(_policy, policy, record.OccurredAt, "Finalising a record");
            if (closed != null) return closed;
        }
        if (policy.LateEntryThresholdDays > 0 && record.OccurredAt < DateTime.UtcNow.AddDays(-policy.LateEntryThresholdDays) && !acknowledgeLateEntry)
            return LateEntryProblem(policy.LateEntryThresholdDays);

        record.Status = StaffRecordStatus.Final;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = me;
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.RecordFinalized, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            RecordSummary("finalised", record, record.Parameter?.Name, SubjectName(record)), null, branchId, record.OrganizationId, visibility: record.Visibility);
        await _alerts.NotifyRecordLoggedAsync(record.Id);

        return Ok(await ToDtoAsync(record));
    }

    [HttpPost("records/{id:guid}/notes")]
    [RequirePermission(Permissions.StaffRecordsEdit)]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddNote(Guid branchId, Guid id, [FromBody] AddStaffNoteRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await LoadAsync(id, branchId);
        if (record == null || !await CanActOnRecordAsync(record, branchId)) return RecordNotFound();
        if (string.IsNullOrWhiteSpace(request.Body)) return BadRequestProblem("The note is empty");

        var me = CurrentUserId();
        Db.StaffPerformanceNotes.Add(new StaffPerformanceNote { RecordId = record.Id, Body = request.Body.Trim(), AuthorUserId = me, Kind = StaffNoteKind.Note });
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.RecordNoteAdded, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            RecordSummary("annotated", record, record.Parameter?.Name, SubjectName(record)), null, branchId, record.OrganizationId, visibility: record.Visibility);

        return Ok(await LoadDtoAsync(record.Id, branchId));
    }

    /// <summary>
    /// The right of reply. The SUBJECT only, on any record they can see (so never Restricted, never
    /// a draft). The person who logged the record is told there is a response to read.
    /// </summary>
    [HttpPost("records/{id:guid}/respond")]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Respond(Guid branchId, Guid id, [FromBody] AddStaffNoteRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var record = await LoadAsync(id, branchId, r => r.SubjectUserId == me && r.Status != StaffRecordStatus.Draft && r.Visibility != WelfareVisibility.Restricted);
        if (record == null) return RecordNotFound();
        if (string.IsNullOrWhiteSpace(request.Body)) return BadRequestProblem("The response is empty");

        Db.StaffPerformanceNotes.Add(new StaffPerformanceNote { RecordId = record.Id, Body = request.Body.Trim(), AuthorUserId = me, Kind = StaffNoteKind.Response });
        if (record.AcknowledgedAt == null) record.AcknowledgedAt = DateTime.UtcNow; // responding is seeing
        await Db.SaveChangesAsync();

        var subjectName = SubjectName(record);
        await Activity.RecordAsync(ActivityActions.RecordResponded, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            RecordSummary("responded to by the subject", record, record.Parameter?.Name, subjectName), null, branchId, record.OrganizationId, visibility: record.Visibility);

        if (record.LoggedByUserId != me)
        {
            await SendAsync(new CreateNotificationRequest
            {
                UserId = record.LoggedByUserId,
                OrganizationId = record.OrganizationId,
                BranchId = record.BranchId,
                Title = $"{subjectName} responded to your record",
                Message = $"{record.Parameter?.Name ?? "A record"}: {Truncate(request.Body.Trim(), 160)}",
                Type = NotificationType.StaffPerformance,
                Priority = NotificationPriority.Normal,
                Channels = NotificationChannel.InApp | NotificationChannel.Email,
                EventKey = NotificationEventKeys.StaffRecordLogged,
                ActionUrl = $"/admin/staff/{record.SubjectUserId}/timeline",
                IconClass = "chat-left-text"
            });
        }

        return Ok(await LoadDtoAsync(record.Id, branchId));
    }

    /// <summary>The subject marks a record as seen. Idempotent: the first timestamp stands.</summary>
    [HttpPost("records/{id:guid}/acknowledge")]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Acknowledge(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var record = await LoadAsync(id, branchId, r => r.SubjectUserId == me && r.Status != StaffRecordStatus.Draft && r.Visibility != WelfareVisibility.Restricted);
        if (record == null) return RecordNotFound();

        if (record.AcknowledgedAt == null)
        {
            record.AcknowledgedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            await Activity.RecordAsync(ActivityActions.RecordAcknowledged, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
                RecordSummary("acknowledged by the subject", record, record.Parameter?.Name, SubjectName(record)), null, branchId, record.OrganizationId, visibility: record.Visibility);
        }

        return Ok(await ToDtoAsync(record));
    }

    /// <summary>The void that keeps the row: Status → Annulled, a note saying why, the row drops out of scoring and the subject's score is pushed again.</summary>
    [HttpPost("records/{id:guid}/annul")]
    [RequirePermission(Permissions.StaffRecordsEdit)]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Annul(Guid branchId, Guid id, [FromBody] AnnulStaffRecordRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await LoadAsync(id, branchId);
        if (record == null || !await CanActOnRecordAsync(record, branchId)) return RecordNotFound();
        if (record.Status == StaffRecordStatus.Annulled) return BadRequestProblem("This record is already annulled");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5) return BadRequestProblem("Say why the record is being annulled");
        if (record.Status == StaffRecordStatus.Final)
        {
            var closed = ClosedPeriodProblem(_policy, await _policy.GetAsync(record.OrganizationId), record.OccurredAt, "Annulling a record");
            if (closed != null) return closed;
        }

        var me = CurrentUserId();
        var wasFinal = record.Status == StaffRecordStatus.Final;
        record.Status = StaffRecordStatus.Annulled;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = me;
        Db.StaffPerformanceNotes.Add(new StaffPerformanceNote { RecordId = record.Id, Body = request.Reason.Trim(), AuthorUserId = me, Kind = StaffNoteKind.Annulment });
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.RecordAnnulled, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            RecordSummary("annulled", record, record.Parameter?.Name, SubjectName(record)), new { WasFinal = wasFinal }, branchId, record.OrganizationId, visibility: record.Visibility);

        if (wasFinal) await _alerts.NotifyScoreUpdatedAsync(record.OrganizationId, record.BranchId, record.SubjectUserId);

        return Ok(await LoadDtoAsync(record.Id, branchId));
    }

    /// <summary>
    /// The one mutable field. Raising needs the target rung to be one the caller can read (or they
    /// could hide a record from everyone including themselves); lowering demands a reason, since it
    /// widens who can read it. Wellbeing never drops below Confidential. The change is a note, so the
    /// record's own chronology carries who, from what, to what, and why.
    /// </summary>
    [HttpPatch("records/{id:guid}/visibility")]
    [RequirePermission(Permissions.StaffRecordsEdit)]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateVisibility(Guid branchId, Guid id, [FromBody] UpdateStaffRecordVisibilityRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await LoadAsync(id, branchId);
        if (record == null || !await CanActOnRecordAsync(record, branchId)) return RecordNotFound();

        if (!Enum.IsDefined(request.Visibility)) return BadRequestProblem("Unrecognised visibility");
        var from = record.Visibility;
        var to = request.Visibility;
        if (from == to) return BadRequestProblem($"The record is already {to}");

        if (to > from && !await CanSeeAsync(to))
            return BadRequestProblem($"You cannot mark a record {to.ToString().ToLowerInvariant()}",
                to == WelfareVisibility.Restricted
                    ? "Restricted records are administrator-only. Ask an administrator to restrict it."
                    : "Your role cannot read confidential staff records, so it cannot file into that rung.");
        if (to < from && string.IsNullOrWhiteSpace(request.Reason))
            return BadRequestProblem("Lowering visibility needs a reason", "More people will be able to read this record; say why that is right.");
        if (record.Parameter?.Kind == ParameterKind.Wellbeing && to < WelfareVisibility.Confidential)
            return BadRequestProblem("A wellbeing record cannot be made Standard", "Welfare-of-staff records are confidential by nature.");

        var me = CurrentUserId();
        record.Visibility = to;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = me;
        var body = $"Visibility changed from {from} to {to}" + (string.IsNullOrWhiteSpace(request.Reason) ? "" : $": {request.Reason.Trim()}");
        Db.StaffPerformanceNotes.Add(new StaffPerformanceNote { RecordId = record.Id, Body = body, AuthorUserId = me, Kind = StaffNoteKind.VisibilityChange });
        await Db.SaveChangesAsync();

        // Written at the HIGHER of the two rungs, so a log reader without it learns only that a
        // restricted record's visibility moved, not what the record is.
        var forSummary = to > from ? record : new StaffPerformanceRecord { Visibility = from, Outcome = record.Outcome, Points = record.Points, Rating = record.Rating };
        await Activity.RecordAsync(ActivityActions.RecordVisibilityChanged, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            RecordSummary($"visibility changed from {from} to {to}", forSummary, record.Parameter?.Name, SubjectName(record)),
            new { From = from, To = to }, branchId, record.OrganizationId, visibility: to > from ? to : from);

        return Ok(await LoadDtoAsync(record.Id, branchId));
    }

    /// <summary>Corrects points and/or rating under the same sign and magnitude rules, with a Moderation note. The row is otherwise untouched.</summary>
    [HttpPatch("records/{id:guid}/points")]
    [RequirePermission(Permissions.StaffRecordsEdit)]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CorrectPoints(Guid branchId, Guid id, [FromBody] CorrectStaffRecordPointsRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await LoadAsync(id, branchId);
        if (record == null || !await CanActOnRecordAsync(record, branchId)) return RecordNotFound();
        if (record.Status == StaffRecordStatus.Annulled) return BadRequestProblem("An annulled record cannot be corrected");
        if (record.Parameter == null) return BadRequestProblem("The record's parameter no longer exists");
        if (request.Points == null && request.Rating == null) return BadRequestProblem("Nothing to correct", "Give new points, a new rating, or both.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5) return BadRequestProblem("Say why the points are being corrected");
        if (record.Status == StaffRecordStatus.Final)
        {
            var closed = ClosedPeriodProblem(_policy, await _policy.GetAsync(record.OrganizationId), record.OccurredAt, "Correcting a record");
            if (closed != null) return closed;
        }

        var oldPoints = record.Points;
        var oldRating = record.Rating;
        var newPoints = oldPoints;
        var newRating = oldRating;

        if (request.Points != null)
        {
            var error = ResolvePoints(record.Parameter, record.Outcome, request.Points, out newPoints);
            if (error != null) return BadRequestProblem(error);
        }
        if (request.Rating != null)
        {
            var error = ResolveRating(record.Parameter, request.Rating, out newRating);
            if (error != null) return BadRequestProblem(error);
        }
        if (newPoints == oldPoints && newRating == oldRating) return BadRequestProblem("Nothing changed");

        var me = CurrentUserId();
        record.Points = newPoints;
        record.Rating = newRating;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = me;

        var changes = new List<string>();
        if (newPoints != oldPoints) changes.Add($"points {Fmt(oldPoints)} → {Fmt(newPoints)}");
        if (newRating != oldRating) changes.Add($"rating {Fmt(oldRating)} → {Fmt(newRating)}");
        Db.StaffPerformanceNotes.Add(new StaffPerformanceNote
        {
            RecordId = record.Id,
            Body = $"Corrected {string.Join(", ", changes)}: {request.Reason.Trim()}",
            AuthorUserId = me,
            Kind = StaffNoteKind.Moderation
        });
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.RecordPointsCorrected, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            RecordSummary($"corrected ({string.Join(", ", changes)})", record, record.Parameter.Name, SubjectName(record)),
            new { OldPoints = oldPoints, NewPoints = newPoints, OldRating = oldRating, NewRating = newRating }, branchId, record.OrganizationId, visibility: record.Visibility);

        if (record.Status == StaffRecordStatus.Final)
            await _alerts.NotifyScoreUpdatedAsync(record.OrganizationId, record.BranchId, record.SubjectUserId);

        return Ok(await LoadDtoAsync(record.Id, branchId));
    }

    // ---------------------------------------------------------------------
    // Evidence
    // ---------------------------------------------------------------------

    /// <summary>
    /// Evidence: a signed register page, an observation sheet, a certificate. By anyone holding
    /// staff.records.create who can read the record, or by the SUBJECT on their own record (a
    /// teacher attaching the certificate that proves the training happened). Stored through the
    /// same IMediaStorageService as every other upload, classified StaffEvidence by
    /// UploadAuthorizer, served only on a signed link.
    /// </summary>
    [HttpPost("records/{id:guid}/attachments")]
    [RequestSizeLimit(MaxAttachmentSizeBytes)]
    [ProducesResponseType(typeof(StaffPerformanceAttachmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadAttachment(Guid branchId, Guid id, IFormFile file)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var record = await LoadAsync(id, branchId);
        if (record == null || !await CanReadRecordAsync(record, branchId)) return RecordNotFound();
        if (record.SubjectUserId != me && !await HasPermissionAsync(Permissions.StaffRecordsCreate)) return RecordNotFound();

        if (file == null || file.Length == 0) return BadRequestProblem("No file was provided");
        if (file.Length > MaxAttachmentSizeBytes) return BadRequestProblem($"File exceeds the {MaxAttachmentSizeBytes / 1024 / 1024}MB size limit");

        var mimeType = file.ContentType ?? "";
        if (!AllowedAttachmentMimePrefixes.Any(p => mimeType.StartsWith(p)))
            return BadRequestProblem("Only images, PDF documents, video, or audio are accepted as evidence");

        await using var uploadStream = file.OpenReadStream();
        var uploadResult = await _mediaStorage.UploadAsync(uploadStream, file.FileName, mimeType);
        if (!uploadResult.Success)
        {
            _logger.LogError("Staff evidence upload failed for record {RecordId}: {Error}", id, uploadResult.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "Failed to store the file" });
        }

        var attachment = new StaffPerformanceAttachment
        {
            RecordId = record.Id,
            FileUrl = uploadResult.FileUrl!,
            FileName = file.FileName,
            ContentType = mimeType,
            FileSizeBytes = file.Length,
            UploadedByUserId = me
        };
        Db.StaffPerformanceAttachments.Add(attachment);
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.RecordEvidenceAdded, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            RecordSummary("received evidence", record, record.Parameter?.Name, SubjectName(record)),
            new { attachment.FileName, attachment.ContentType, attachment.FileSizeBytes }, branchId, record.OrganizationId, visibility: record.Visibility);

        return CreatedAtAction(nameof(GetRecord), new { branchId, id = record.Id }, StaffPerformanceMapping.ToDto(attachment));
    }

    // ---------------------------------------------------------------------
    // A person: timeline and score
    // ---------------------------------------------------------------------

    /// <summary>
    /// One person's file: who they are, their records at the rungs the caller may read (newest
    /// first, others' drafts excluded), their score this period (with a private rank only for
    /// themselves), and the last fifty actions on the file — the subject-access trail. Self needs
    /// no permission; anyone else needs staff.records.view and the person in scope.
    /// </summary>
    [HttpGet("members/{userId:guid}/timeline")]
    [ProducesResponseType(typeof(StaffTimelineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTimeline(Guid branchId, Guid userId, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var access = await VerifyPersonAccessAsync(branchId, userId);
        if (access.Error != null) return access.Error;
        var (organizationId, subject, isSelf) = (access.OrganizationId, access.Subject!, access.IsSelf);

        var me = CurrentUserId();
        var policy = await _policy.GetAsync(organizationId);
        var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));

        var query = RecordsWithIncludes().Where(r => r.BranchId == branchId && r.SubjectUserId == userId);
        query = await ApplyRungAsync(query, me);
        if (from.HasValue) query = query.Where(r => r.OccurredAt >= DateTime.SpecifyKind(from.Value, DateTimeKind.Utc));
        if (to.HasValue) query = query.Where(r => r.OccurredAt <= DateTime.SpecifyKind(to.Value, DateTimeKind.Utc));
        var records = await query.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.CreatedAt).ToListAsync();

        var score = await _scoring.ComputeAsync(organizationId, branchId, userId, period, includeRank: isSelf);

        // The file's second layer: what was done between the records. Same period as the records,
        // this branch plus organization-level rows, and — for the subject reading their own file —
        // never a Restricted event, or "Restricted record viewed" would tell them one exists.
        var eventQuery = Db.ActivityEvents.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.SubjectUserId == userId && (e.BranchId == branchId || e.BranchId == null));
        if (isSelf) eventQuery = eventQuery.Where(e => e.Visibility != WelfareVisibility.Restricted);
        if (from.HasValue) eventQuery = eventQuery.Where(e => e.OccurredAt >= DateTime.SpecifyKind(from.Value, DateTimeKind.Utc));
        if (to.HasValue) eventQuery = eventQuery.Where(e => e.OccurredAt <= DateTime.SpecifyKind(to.Value, DateTimeKind.Utc));
        var events = await eventQuery
            .OrderByDescending(e => e.OccurredAt)
            .Take(TimelineActivityRows)
            .ToListAsync();

        var names = await BuildNamesAsync(records.Select(r => (Guid?)r.LoggedByUserId)
            .Concat(records.SelectMany(r => r.Notes).Select(n => (Guid?)n.AuthorUserId))
            .Concat(events.Select(e => e.ActorUserId))
            .Append(userId).Append(subject.LineManagerUserId));
        var departmentNames = await DepartmentNamesAsync(organizationId);

        if (!isSelf)
        {
            await Activity.RecordAsync(ActivityActions.TimelineViewed, nameof(User), userId, userId,
                $"Timeline of {StaffPerformanceMapping.FullName(subject)} viewed", null, branchId, organizationId);
        }

        return Ok(new StaffTimelineDto
        {
            Subject = StaffPerformanceMapping.ToDto(subject, names, departmentNames, score),
            Records = records.Select(r => StaffPerformanceMapping.ToDto(r, names, policy.LateEntryThresholdDays)).ToList(),
            Score = score,
            Activity = events.Select(e => StaffPerformanceMapping.ToDto(e, names)).ToList(),
            IsSelf = isSelf
        });
    }

    [HttpGet("members/{userId:guid}/score")]
    [ProducesResponseType(typeof(StaffScoreDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScore(Guid branchId, Guid userId, [FromQuery] string? period = null)
    {
        var access = await VerifyPersonAccessAsync(branchId, userId);
        if (access.Error != null) return access.Error;

        var policy = await _policy.GetAsync(access.OrganizationId);
        var periodDto = _policy.FindPeriod(policy, period) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));
        return Ok(await _scoring.ComputeAsync(access.OrganizationId, branchId, userId, periodDto, includeRank: access.IsSelf));
    }

    // ---------------------------------------------------------------------
    // Recognition
    // ---------------------------------------------------------------------

    /// <summary>
    /// Kudos from a colleague, within the tenant's monthly budget. NOT narrowed by staff scope on
    /// purpose: recognising somebody in another department is the point. Self-recognition is
    /// refused; the receiver is notified at once; the row is an ordinary Final record with
    /// Source = Recognition, so it counts and shows like any other.
    /// </summary>
    [HttpPost("recognition")]
    [RequirePermission(Permissions.StaffRecognitionGive)]
    [ProducesResponseType(typeof(StaffPerformanceRecordDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GiveRecognition(Guid branchId, [FromBody] GiveRecognitionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        if (request.SubjectUserId == me) return BadRequestProblem("You cannot recognise yourself");

        var subject = await FindBranchStaffAsync(organizationId, branchId, request.SubjectUserId);
        if (subject == null) return StaffMemberNotFound();

        var parameter = await Db.PerformanceParameters.FirstOrDefaultAsync(p => p.Id == request.ParameterId && p.OrganizationId == organizationId && p.IsActive);
        if (parameter == null) return BadRequestProblem("The recognition parameter was not found");
        if (parameter.Kind != ParameterKind.Recognition) return BadRequestProblem($"'{parameter.Name}' is not a recognition parameter");
        var group = subject.Role?.StaffGroup;
        if (!StaffGroups.Applies(parameter.AppliesToGroup, group))
            return BadRequestProblem($"'{parameter.Name}' applies to {parameter.AppliesToGroup} only");
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Trim().Length < 10)
            return BadRequestProblem("Say what you are recognising", "At least 10 characters — the point of recognition is the why.");

        var policy = await _policy.GetAsync(organizationId);
        if (policy.RecognitionMonthlyBudget <= 0) return BadRequestProblem("Peer recognition is switched off for this organization");
        var closedNow = ClosedPeriodProblem(_policy, policy, DateTime.UtcNow, "Recognition");
        if (closedNow != null) return closedNow;

        var points = Math.Max(1, Math.Abs(parameter.DefaultPoints ?? 1));
        if (parameter.MaxPointsPerEntry > 0) points = Math.Min(points, parameter.MaxPointsPerEntry);

        var record = new StaffPerformanceRecord
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            SubjectUserId = subject.Id,
            ParameterId = parameter.Id,
            Outcome = DutyOutcome.NotApplicable,
            Points = points,
            Description = request.Message.Trim(),
            OccurredAt = DateTime.UtcNow,
            Source = RecordSource.Recognition,
            Status = StaffRecordStatus.Final,
            Visibility = WelfareVisibility.Standard,
            LoggedByUserId = me,
            CreatedBy = me
        };

        // CONCURRENCY (found by the e2e, 2026-09-16): the budget was count-then-insert with nothing
        // between the two, so eight simultaneous recognitions against three remaining all read "3
        // left" and all inserted (measured: 8 of 8 succeeded). The count and the insert now happen
        // inside one transaction holding pg_advisory_xact_lock keyed on the GIVER — the pattern
        // AppointmentsController, VisitorsController and TokenRepository already use. Per giver,
        // so two different people recognising at once never wait on each other.
        var used = 0;
        var overBudget = false;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            overBudget = false;
            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"staff-recognition:{organizationId}:{me}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");

            used = await RecognitionsUsedThisMonthAsync(organizationId, me);
            if (used >= policy.RecognitionMonthlyBudget)
            {
                overBudget = true;
                await tx.RollbackAsync();
                return;
            }

            Db.StaffPerformanceRecords.Add(record);
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        if (overBudget)
            return BadRequestProblem($"You have used {used} of {policy.RecognitionMonthlyBudget} recognitions this month", "The budget resets on the first of the month.");

        await Activity.RecordAsync(ActivityActions.RecognitionGiven, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
            $"Recognition given to {StaffPerformanceMapping.FullName(subject)} ({parameter.Name}, +{points} pts)",
            new { record.ParameterId, record.Points }, branchId, organizationId);
        await _alerts.NotifyRecordLoggedAsync(record.Id);

        return CreatedAtAction(nameof(GetRecord), new { branchId, id = record.Id }, await LoadDtoAsync(record.Id, branchId));
    }

    [HttpGet("recognition/budget")]
    [RequirePermission(Permissions.StaffRecognitionGive)]
    [ProducesResponseType(typeof(RecognitionBudgetDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecognitionBudget(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        return Ok(await BuildRecognitionBudgetAsync(_policy, organizationId, CurrentUserId()));
    }

    // ---------------------------------------------------------------------
    // Rules, stated once
    // ---------------------------------------------------------------------

    /// <summary>
    /// The sign rule by kind, and the per-entry cap. Returns a plain-English message or null.
    /// Attendance and Duty derive their points from the outcome and let the client override the
    /// magnitude but never the sign; Late is half; Excused is zero; Recovered is a full credit.
    /// </summary>
    internal static string? ResolvePoints(PerformanceParameter p, DutyOutcome outcome, int? requested, out int? points)
    {
        points = null;
        var dp = Math.Abs(p.DefaultPoints ?? 0);

        switch (p.Kind)
        {
            case ParameterKind.Wellbeing:
                if (requested is { } w && w != 0) return "Wellbeing records are not scored — leave points blank";
                return null;

            case ParameterKind.Attendance:
            case ParameterKind.Duty:
            {
                int derived;
                switch (outcome)
                {
                    case DutyOutcome.Present or DutyOutcome.Completed or DutyOutcome.Recovered: derived = dp; break;
                    case DutyOutcome.Absent or DutyOutcome.NotCompleted: derived = -dp; break;
                    case DutyOutcome.Late: derived = (int)Math.Round(dp / 2.0, MidpointRounding.AwayFromZero); break;
                    case DutyOutcome.Excused: derived = 0; break;
                    default: return $"Choose an outcome for {(p.Kind == ParameterKind.Attendance ? "an attendance" : "a duty")} record";
                }
                if (requested is { } r && r != 0)
                {
                    if (derived == 0) return $"An {outcome.ToString().ToLowerInvariant()} outcome carries no points";
                    if (Math.Sign(r) != Math.Sign(derived)) return $"Points for a {outcome.ToString().ToLowerInvariant()} outcome must be {(derived > 0 ? "positive" : "negative")}";
                }
                points = requested ?? derived;
                break;
            }

            case ParameterKind.Contribution:
            case ParameterKind.Recognition:
                points = requested ?? dp;
                if (points < 0) return $"{p.Kind} points must be zero or positive";
                break;

            case ParameterKind.Conduct:
                points = requested ?? -dp;
                if (points > 0) return "Conduct points must be zero or negative";
                break;

            case ParameterKind.Observation:
                points = requested ?? p.DefaultPoints;
                break;
        }

        if (points is { } v && p.MaxPointsPerEntry > 0 && Math.Abs(v) > p.MaxPointsPerEntry)
            return $"Points for '{p.Name}' must be between -{p.MaxPointsPerEntry} and {p.MaxPointsPerEntry}";
        if (points is { } v2 && p.MaxPointsPerEntry == 0 && v2 != 0)
            return $"'{p.Name}' carries no points";

        return null;
    }

    internal static string? ResolveRating(PerformanceParameter p, int? requested, out int? rating)
    {
        rating = null;
        if (p.Kind != ParameterKind.Observation)
            return requested.HasValue ? "Only observations carry a rating" : null;

        var scale = p.RatingScale is > 1 ? p.RatingScale.Value : 4;
        if (requested == null) return $"An observation needs a rating from 1 to {scale}";
        if (requested < 1 || requested > scale) return $"The rating must be between 1 and {scale}";
        rating = requested;
        return null;
    }

    /// <summary>
    /// Server decides. A caller may ASK for a rung they can read (a request for one they cannot is
    /// refused: they would be hiding a record from everyone including themselves). The parameter's
    /// default and the Wellbeing floor are applied on top, and those are allowed even above the
    /// caller's rung — the welfare precedent, where a class teacher files a safeguarding case that
    /// is forced Confidential — because the author keeps read access to what they wrote.
    /// </summary>
    private async Task<(IActionResult? Error, WelfareVisibility Visibility)> ResolveVisibilityAsync(PerformanceParameter parameter, WelfareVisibility requested)
    {
        if (!Enum.IsDefined(requested)) return (BadRequestProblem("Unrecognised visibility"), default);

        if (requested == WelfareVisibility.Restricted && !await CanViewRestrictedAsync())
            return (BadRequestProblem("You cannot mark a record restricted", "Restricted records are administrator-only. Log it normally and ask an administrator to restrict it."), default);
        if (requested == WelfareVisibility.Confidential && !await CanViewConfidentialAsync() && parameter.DefaultVisibility < WelfareVisibility.Confidential && parameter.Kind != ParameterKind.Wellbeing)
            return (BadRequestProblem("You cannot mark a record confidential", "Your role cannot read confidential staff records. Log it at its normal visibility, or ask someone who can."), default);

        var visibility = requested;
        if (parameter.DefaultVisibility > visibility) visibility = parameter.DefaultVisibility;
        if (parameter.Kind == ParameterKind.Wellbeing && visibility < WelfareVisibility.Confidential) visibility = WelfareVisibility.Confidential;
        return (null, visibility);
    }

    /// <summary>
    /// May this caller WRITE to (annotate, annul, re-rung, correct) this record? The rung and the
    /// scope, or authorship below Restricted. Drafts are the author's alone. The permission itself
    /// is the action's [RequirePermission].
    /// </summary>
    private async Task<bool> CanActOnRecordAsync(StaffPerformanceRecord record, Guid branchId)
    {
        var me = CurrentUserId();
        var isAuthor = me != Guid.Empty && record.LoggedByUserId == me;
        if (record.Status == StaffRecordStatus.Draft) return isAuthor;
        if (isAuthor && record.Visibility != WelfareVisibility.Restricted) return true;
        return await CanSeeAsync(record.Visibility) && await StaffScope.CanSeeStaffAsync(branchId, record.SubjectUserId);
    }

    /// <summary>The query-side twin of CanReadRecordAsync: rung set for others, self and authorship below Restricted, drafts to their author.</summary>
    private async Task<IQueryable<StaffPerformanceRecord>> ApplyRungAsync(IQueryable<StaffPerformanceRecord> query, Guid me)
    {
        var levels = await VisibleLevelsAsync();
        return query.Where(r =>
            (r.Status != StaffRecordStatus.Draft || r.LoggedByUserId == me)
            && (levels.Contains(r.Visibility)
                || ((r.SubjectUserId == me || r.LoggedByUserId == me) && r.Visibility != WelfareVisibility.Restricted)));
    }

    /// <summary>Branch + person: self needs nothing more; anyone else needs staff.records.view (403 — the permission is not a secret) and the person in scope (404).</summary>
    private async Task<(IActionResult? Error, Guid OrganizationId, User? Subject, bool IsSelf)> VerifyPersonAccessAsync(Guid branchId, Guid userId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return (branchError, default, null, false);

        var me = CurrentUserId();
        var isSelf = me != Guid.Empty && me == userId;
        if (!isSelf)
        {
            if (!await HasPermissionAsync(Permissions.StaffRecordsView)) return (Forbid(), default, null, false);
            var scopeError = await StaffScope.VerifyStaffAccessAsync(branchId, userId);
            if (scopeError != null) return (scopeError, default, null, false);
        }

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var subject = await Db.Users.AsNoTracking().Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == organizationId && u.Role.Code != RoleCodes.SuperAdmin);
        if (subject == null) return (StaffMemberNotFound(), default, null, false);

        return (null, organizationId, subject, isSelf);
    }

    private Task<User?> FindBranchStaffAsync(Guid organizationId, Guid branchId, Guid userId)
        => Db.Users.AsNoTracking().Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == organizationId && u.IsActive
                                      && (u.AssignedBranchId == branchId || u.AssignedBranchId == null)
                                      && u.Role.Code != RoleCodes.SuperAdmin);

    // ---- Loading and mapping -----------------------------------------------------------------------

    private IQueryable<StaffPerformanceRecord> RecordsWithIncludes()
        => Db.StaffPerformanceRecords
            .Include(r => r.Parameter)
            .Include(r => r.Subject)
            .Include(r => r.Duty)
            .Include(r => r.Notes)
            .Include(r => r.Attachments);

    private Task<StaffPerformanceRecord?> LoadAsync(Guid id, Guid branchId, System.Linq.Expressions.Expression<Func<StaffPerformanceRecord, bool>>? also = null)
    {
        var query = RecordsWithIncludes().Where(r => r.Id == id && r.BranchId == branchId);
        if (also != null) query = query.Where(also);
        return query.FirstOrDefaultAsync();
    }

    private async Task<StaffPerformanceRecordDto> LoadDtoAsync(Guid id, Guid branchId)
    {
        var record = await RecordsWithIncludes().AsNoTracking().FirstAsync(r => r.Id == id && r.BranchId == branchId);
        return await ToDtoAsync(record);
    }

    private async Task<StaffPerformanceRecordDto> ToDtoAsync(StaffPerformanceRecord record)
    {
        var policy = await _policy.GetAsync(record.OrganizationId);
        var names = await NamesForAsync(new[] { record });
        return StaffPerformanceMapping.ToDto(record, names, policy.LateEntryThresholdDays);
    }

    private Task<StaffPerformanceMapping.NameLookup> NamesForAsync(IEnumerable<StaffPerformanceRecord> records)
    {
        var list = records.ToList();
        return BuildNamesAsync(list.Select(r => (Guid?)r.LoggedByUserId)
            .Concat(list.Select(r => (Guid?)r.SubjectUserId))
            .Concat(list.SelectMany(r => r.Notes).Select(n => (Guid?)n.AuthorUserId)));
    }

    /// <summary>The welfare late-entry shape: 409, the threshold named, resubmit with acknowledgeLateEntry=true.</summary>
    private IActionResult LateEntryProblem(int thresholdDays)
        => Conflict(new ProblemDetails
        {
            Title = "Late entry",
            Detail = $"The date you entered is more than {thresholdDays} days ago. If that's correct, resubmit with acknowledgeLateEntry=true; the record will say it was logged late.",
            Status = StatusCodes.Status409Conflict,
            Extensions = { ["code"] = "LATE_ENTRY", ["thresholdDays"] = thresholdDays }
        });

    private static string SubjectName(StaffPerformanceRecord r) => r.Subject != null ? StaffPerformanceMapping.FullName(r.Subject) : "a member of staff";
    private static string Fmt(int? v) => v?.ToString() ?? "none";

    private async Task SendAsync(CreateNotificationRequest request)
    {
        try { await _notifications.CreateInAppNotificationAsync(request); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to notify {UserId}: {Title}", request.UserId, request.Title); }
    }
}
