using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// WHAT A MEMBER OF STAFF MAY CONFIGURE FOR THEMSELVES, in My Workspace.
///
/// NO PERMISSION CODES ON THE SELF ROUTES, the ProfileController rule: self is always visible and is
/// not scope. What a caller may do is decided by WHAT THEY ALREADY HOLD — an assignment, a lesson of
/// their own — and by the school's dials, never by a permission they might be granted by accident.
///
/// THE THREE TIERS, and the test that sorts them is mechanical rather than a matter of taste:
///
///   Tier 1  DECLARE   Reaches nobody else. Unavailability, teaching preferences. Instant.
///   Tier 2  CLAIM     Can collide with a colleague; CANNOT widen anybody's access. Instant, but
///                     only into a slot that is provably free, inside an assignment already held.
///   Tier 3  REQUEST   Widens access, or collides. Decided by somebody else. Never by the asker.
///
/// A class assignment is ALWAYS Tier 3, whoever asks. StudentScopeService grants
/// StudentAccessTier.Teaching from a SubjectTeacher row, so "I teach S4 Maths" is a request for
/// access to a named group of children. No amount of validation substitutes for a second person.
///
/// ONE QUEUE and one decider: a holder of <c>timetable.manage</c> (user decision, 2026-09-21). The
/// DIRECT write paths do not move — the class-teachers endpoint keeps classes.teachers.manage and the
/// lessons endpoint keeps timetable.manage. What changes is who may approve somebody ELSE's request.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff/self-service")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffSelfServiceController : StaffPerformanceControllerBase
{
    private readonly ITimetableSettingsService _settings;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly INotificationService _notifications;
    private readonly ILogger<StaffSelfServiceController> _logger;

    public StaffSelfServiceController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        ITimetableSettingsService settings,
        IStaffPerformancePolicyService policy,
        INotificationService notifications,
        ILogger<StaffSelfServiceController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _settings = settings;
        _policy = policy;
        _notifications = notifications;
        _logger = logger;
    }

    // =================================================================================================
    // What am I allowed to do?
    // =================================================================================================

    /// <summary>
    /// The school's dials as they apply to this caller, plus the classes and subjects they already
    /// hold. One call, because every screen in My Workspace needs all of it to decide what to render
    /// — and a page that renders a control the server will refuse is worse than one that does not.
    /// </summary>
    [HttpGet("context")]
    [ProducesResponseType(typeof(SelfServiceContextDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetContext(Guid branchId)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var policy = (await _policy.GetAsync(organizationId)).SelfService;
        var settings = await _settings.ReadAsync(branchId);

        var assignments = await Db.ClassTeacherAssignments.AsNoTracking()
            .Where(a => a.BranchId == branchId && a.UserId == me && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher)
            .ToListAsync();

        var subjectIds = assignments.Where(a => a.SubjectId != null).Select(a => a.SubjectId!.Value).Distinct().ToList();
        var subjects = await Db.Subjects.AsNoTracking()
            .Where(s => subjectIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        var placed = await TimetableChecker.PlacedPerWeekAsync(Db, branchId);

        return Ok(new SelfServiceContextDto
        {
            BranchId = branchId,
            Policy = policy,
            CanDecideRequests = await HasPermissionAsync(Permissions.TimetableManage),
            MyClasses = assignments.Select(a => new MyTeachingAssignmentDto
            {
                AssignmentId = a.Id,
                ClassName = a.ClassName,
                SubjectId = a.SubjectId ?? Guid.Empty,
                SubjectName = a.SubjectId != null ? subjects.GetValueOrDefault(a.SubjectId.Value) ?? "(retired subject)" : "(no subject)",
                PeriodsPerWeek = a.PeriodsPerWeek,
                PlacedPerWeek = a.SubjectId == null ? 0
                    : (int)placed.GetValueOrDefault((me, TimetableCycle.Normalize(a.ClassName), a.SubjectId.Value)),
            }).OrderBy(x => x.ClassName).ThenBy(x => x.SubjectName).ToList(),
            MyUnavailability = settings.Unavailability
                .Where(u => u.UserId == me)
                .Select(u => new MyUnavailabilityLine
                {
                    CycleDay = u.CycleDay,
                    PeriodKey = u.PeriodKey,
                    Reason = u.Reason,
                    Note = u.Note,
                })
                .ToList(),
            // A line the MASTER entered is shown and is not editable here: a teacher clearing their
            // own declarations must not quietly undo a constraint the school put on them.
            MyUnavailabilityFromSchool = settings.Unavailability.Count(u => u.UserId == me && !u.DeclaredBySelf),
            MyPreferences = settings.Preferences.FirstOrDefault(p => p.UserId == me) ?? new StaffTeachingPreferenceDto { UserId = me },
            CycleDays = TimetableCycle.CycleDayCount(settings),
            DayLabels = Enumerable.Range(1, TimetableCycle.CycleDayCount(settings))
                .Select(d => TimetableCycle.CycleDayLabel(settings, TimetableCycle.CycleDayCount(settings), d)).ToList(),
            LessonPeriods = TimetableCycle.Rows(settings)
                .Where(p => p.Kind == BellPeriodKind.Lesson)
                .Select(p => new SelfServicePeriodDto { Key = p.Key, Label = p.Label, Start = p.Start, End = p.End })
                .ToList(),
        });
    }

    // =================================================================================================
    // Tier 1 — declarations
    // =================================================================================================

    /// <summary>
    /// Replace the caller's OWN unavailability and preferences.
    ///
    /// The request carries no user id, which is what makes it impossible to point at somebody else.
    /// Taken under BranchSettingsLock and re-read inside it, because Branch.Settings is one JSON column
    /// holding several independent keys and two concurrent saves otherwise lose one of them.
    /// </summary>
    [HttpPut("declarations")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateDeclarations(Guid branchId, [FromBody] UpdateMyTeachingDeclarationsRequest request)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = (await _policy.GetAsync(organizationId)).SelfService;
        if (!policy.Enabled || !policy.AllowDeclarations)
            return BadRequestProblem("Not available", "This school has not switched on staff self-service.");

        var me = CurrentUserId();
        var max = policy.MaxUnavailabilityLinesPerTeacher > 0
            ? policy.MaxUnavailabilityLinesPerTeacher
            : StaffSelfService.DefaultMaxUnavailabilityLines;

        IActionResult? result = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await Db.Database.BeginTransactionAsync();
            await BranchSettingsLock.AcquireAsync(Db, branchId);

            var settings = await _settings.ReadAsync(branchId);

            if (StaffSelfService.ApplyDeclarations(settings, me, request, max) is { } refusal)
            {
                result = BadRequestProblem("Cannot save", refusal);
                return;
            }

            // The same validator the timetable master's own save runs. A teacher's declaration cannot
            // be allowed to make the settings blob something the master's page would refuse to load.
            if (TimetableCycle.Validate(settings) is { } invalid)
            {
                result = BadRequestProblem("Cannot save", invalid);
                return;
            }

            await _settings.WriteAsync(branchId, settings);
            await tx.CommitAsync();
            result = NoContent();
        });

        if (result is NoContentResult)
        {
            // Named at the FIELD that moved, never its value — the staff-record rule. "Unavailability
            // updated" is the useful half; which periods and why are in the settings, gated as they are.
            await Activity.RecordAsync(ActivityActions.SelfServiceDeclared, "StaffDeclaration", null, me,
                "Teaching declarations updated", null, branchId, organizationId);
        }

        return result!;
    }

    // =================================================================================================
    // Tier 2 — the grid, and claiming from it
    // =================================================================================================

    /// <summary>
    /// The cycle for a class and subject the caller already holds.
    ///
    /// A teacher sees Free / Mine / Taken. A holder of <c>timetable.manage</c> sees the names too —
    /// one gate, the same code that gates building the timetable at all, so there is no second rule
    /// to drift from the first.
    /// </summary>
    [HttpGet("openings")]
    [ProducesResponseType(typeof(TimetableOpeningsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOpenings(Guid branchId, [FromQuery] string className, [FromQuery] Guid subjectId)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var policy = (await _policy.GetAsync(organizationId)).SelfService;

        var assignment = await FindMyAssignmentAsync(branchId, me, className, subjectId);
        if (assignment == null)
            // The same wording an unknown class gets. A distinct "not yours" would confirm that
            // somebody else teaches it, which is exactly what this caller may not learn.
            return NotFoundProblem("That class and subject are not one of yours.");

        var timetable = await CurrentDraftAsync(branchId);
        if (timetable == null)
            return NotFoundProblem("There is no draft timetable to place lessons in yet.");

        var lessons = await Db.TimetableLessons.AsNoTracking().Where(l => l.TimetableId == timetable.Id).ToListAsync();
        var dto = await BuildOpeningsDtoAsync(branchId, organizationId, timetable, lessons, me, assignment, policy);
        return Ok(dto);
    }

    /// <summary>
    /// Place, or move, one of the caller's own lessons.
    ///
    /// UNDER THE SAME ADVISORY LOCK THE TIMETABLE MASTER TAKES. That lock is what makes the class and
    /// room checks safe at all — they are code checks, not unique indexes — so a self-service write
    /// that skipped it would be the one unprotected writer and would remove the protection from the
    /// other four paths as well.
    /// </summary>
    [HttpPost("claims")]
    [ProducesResponseType(typeof(ClaimResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Claim(Guid branchId, [FromBody] ClaimSlotRequest request)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var policy = (await _policy.GetAsync(organizationId)).SelfService;

        if (!policy.Enabled || !policy.AllowDirectClaims)
            return Ok(new ClaimResultDto
            {
                Ok = false,
                Refusal = "This school does not let staff place their own lessons. Ask for the period instead.",
                OfferRequest = ConfigRequestKind.TimetableSlot,
            });

        var assignment = await FindMyAssignmentAsync(branchId, me, request.ClassName, request.SubjectId);
        if (assignment == null) return NotFoundProblem("That class and subject are not one of yours.");

        ClaimResultDto? outcome = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await Db.Database.BeginTransactionAsync();

            var timetable = await CurrentDraftAsync(branchId, tracking: true);
            if (timetable == null) { outcome = new ClaimResultDto { Ok = false, Refusal = "There is no draft timetable to place lessons in." }; return; }

            await LockTimetableAsync(timetable.Id);

            // RE-READ INSIDE THE LOCK. Checking the set the client rendered from would be checking
            // the past; this is the only read whose answer can be acted on.
            var lessons = await Db.TimetableLessons.Where(l => l.TimetableId == timetable.Id).ToListAsync();
            var settings = await _settings.ReadAsync(branchId);
            var mineUnavailable = settings.Unavailability.Where(u => u.UserId == me).ToList();

            var fresh = StaffSelfService.Fingerprint(timetable.Id, lessons, mineUnavailable);
            if (!string.Equals(fresh, request.Fingerprint, StringComparison.Ordinal))
            {
                outcome = new ClaimResultDto
                {
                    Ok = false,
                    Stale = true,
                    Refusal = "The timetable changed while you were looking at it. Here it is as it stands now.",
                    Openings = await BuildOpeningsDtoAsync(branchId, organizationId, timetable, lessons, me, assignment, policy),
                };
                return;
            }

            var classNorm = TimetableCycle.Normalize(request.ClassName);
            if (StaffSelfService.RefuseClaim(timetable, settings, lessons, me, request, classNorm,
                    assignment.PeriodsPerWeek, policy.ClaimsCappedByPlannedLoad) is { } refused)
            {
                outcome = new ClaimResultDto { Ok = false, Refusal = refused.Refusal, OfferRequest = refused.Offer };
                return;
            }

            if (request.MoveFromLessonId is { } moveId)
            {
                var existing = lessons.FirstOrDefault(l => l.Id == moveId && l.TeacherUserId == me);
                if (existing == null) { outcome = new ClaimResultDto { Ok = false, Refusal = "That lesson is not one of yours." }; return; }
                if (existing.GroupId != null)
                {
                    outcome = new ClaimResultDto
                    {
                        Ok = false,
                        Refusal = "That is a joint lesson shared with another class or teacher. Ask for it to be moved.",
                        OfferRequest = ConfigRequestKind.TimetableSlot,
                    };
                    return;
                }
                Db.TimetableLessons.Remove(existing);
            }

            var lesson = new TimetableLesson
            {
                TimetableId = timetable.Id,
                CycleDay = request.CycleDay,
                PeriodKey = request.PeriodKey,
                ClassName = assignment.ClassName,
                ClassNameNormalized = classNorm,
                SubjectId = request.SubjectId,
                TeacherUserId = me,
                Room = string.IsNullOrWhiteSpace(request.Room) ? null : request.Room.Trim(),
                RoomNormalized = string.IsNullOrWhiteSpace(request.Room) ? null : TimetableCycle.Normalize(request.Room),
                CreatedBy = me,
            };
            Db.TimetableLessons.Add(lesson);

            try
            {
                await Db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // The database's own last word. The teacher unique index exists precisely so two
                // writers who both passed the check cannot both win.
                outcome = new ClaimResultDto { Ok = false, Stale = true, Refusal = "Somebody placed a lesson there a moment ago." };
                return;
            }

            await tx.CommitAsync();
            outcome = new ClaimResultDto { Ok = true, LessonId = lesson.Id };
        });

        if (outcome is { Ok: true })
        {
            await Activity.RecordAsync(ActivityActions.SelfServiceClaimed, nameof(TimetableLesson), outcome.LessonId, me,
                $"Lesson placed: {request.ClassName} at day {request.CycleDay} {request.PeriodKey}",
                null, branchId, organizationId);
        }

        return Ok(outcome!);
    }

    /// <summary>Give up one of the caller's own lessons. The same lock; a joint lesson is never touched here.</summary>
    [HttpDelete("claims/{lessonId:guid}")]
    [ProducesResponseType(typeof(ClaimResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Release(Guid branchId, Guid lessonId)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var policy = (await _policy.GetAsync(organizationId)).SelfService;
        if (!policy.Enabled || !policy.AllowDirectClaims)
            return Ok(new ClaimResultDto { Ok = false, Refusal = "This school does not let staff change their own lessons.", OfferRequest = ConfigRequestKind.TimetableSlot });

        ClaimResultDto? outcome = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await Db.Database.BeginTransactionAsync();

            var lesson = await Db.TimetableLessons.Include(l => l.Timetable).FirstOrDefaultAsync(l => l.Id == lessonId);
            if (lesson == null || lesson.Timetable.BranchId != branchId) { outcome = new ClaimResultDto { Ok = false, Refusal = "That lesson could not be found." }; return; }

            await LockTimetableAsync(lesson.TimetableId);

            if (lesson.TeacherUserId != me) { outcome = new ClaimResultDto { Ok = false, Refusal = "That lesson is not one of yours." }; return; }
            if (lesson.Timetable.Status != TimetableStatus.Draft)
            {
                outcome = new ClaimResultDto { Ok = false, Refusal = "That timetable has been published. Ask for the change instead.", OfferRequest = ConfigRequestKind.TimetableSlot };
                return;
            }
            if (lesson.GroupId != null)
            {
                outcome = new ClaimResultDto { Ok = false, Refusal = "That is a joint lesson. Ask for it to be changed.", OfferRequest = ConfigRequestKind.TimetableSlot };
                return;
            }

            Db.TimetableLessons.Remove(lesson);
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
            outcome = new ClaimResultDto { Ok = true };
        });

        if (outcome is { Ok: true })
            await Activity.RecordAsync(ActivityActions.SelfServiceReleased, nameof(TimetableLesson), lessonId, me,
                "Lesson released", null, branchId, organizationId);

        return Ok(outcome!);
    }

    // =================================================================================================
    // Tier 3 — ONE queue
    // =================================================================================================

    /// <summary>
    /// The requests this caller may see: their own, always; the ones waiting on them as a colleague in
    /// a swap; and — for a holder of <c>timetable.manage</c> — everything open in the branch.
    ///
    /// A teacher reading the queue sees their OWN requests and nothing about anybody else's, which is
    /// the same disclosure rule as the grid: they learn what concerns them and nothing further.
    /// </summary>
    [HttpGet("requests")]
    [ProducesResponseType(typeof(List<StaffConfigRequestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRequests(Guid branchId, [FromQuery] bool openOnly = true)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var me = CurrentUserId();
        var canDecide = await HasPermissionAsync(Permissions.TimetableManage);

        var q = Db.StaffConfigRequests.AsNoTracking().Where(r => r.BranchId == branchId);
        if (!canDecide) q = q.Where(r => r.RequestedByUserId == me || r.CounterpartUserId == me);
        if (openOnly) q = q.Where(r => r.State == ConfigRequestState.Pending);

        var rows = await q.OrderByDescending(r => r.RequestedAt).Take(200).ToListAsync();
        return Ok(await MapRequestsAsync(rows, me, canDecide));
    }

    /// <summary>
    /// Ask for something. The request carries no requester id and no state: the caller is the asker and
    /// the state is always Pending, which is what stops it being filed in somebody else's name.
    ///
    /// DUPLICATE DETECTION IS THE DATABASE'S, not this method's. A partial-unique index allows one open
    /// request per person per thing, so a second tab or a double press produces one row and one clear
    /// refusal rather than two rows a decider has to reconcile.
    /// </summary>
    [HttpPost("requests")]
    [ProducesResponseType(typeof(StaffConfigRequestDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateRequest(Guid branchId, [FromBody] CreateConfigRequestRequest request)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var policy = (await _policy.GetAsync(organizationId)).SelfService;
        if (!policy.Enabled || !policy.AllowRequests)
            return BadRequestProblem("Not available", "This school has not switched on staff self-service.");

        var row = new StaffConfigRequest
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            Kind = request.Kind,
            State = ConfigRequestState.Pending,
            RequestedByUserId = me,
            RequestedAt = DateTime.UtcNow,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CycleDay = request.CycleDay,
            PeriodKey = string.IsNullOrWhiteSpace(request.PeriodKey) ? null : request.PeriodKey.Trim(),
            Room = string.IsNullOrWhiteSpace(request.Room) ? null : request.Room.Trim(),
            SubjectId = request.SubjectId,
            PeriodsPerWeek = request.PeriodsPerWeek,
            MyLessonId = request.MyLessonId,
            TheirLessonId = request.TheirLessonId,
            CreatedBy = me,
        };

        if (!string.IsNullOrWhiteSpace(request.ClassName))
        {
            row.ClassName = request.ClassName.Trim();
            row.ClassNameNormalized = TimetableCycle.Normalize(request.ClassName);
        }

        if (await RefuseRequestAsync(branchId, me, row, request) is { } refusal)
            return BadRequestProblem("Cannot ask for that", refusal);

        Db.StaffConfigRequests.Add(row);
        try
        {
            await Db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return ConflictProblem("Already asked", "You already have an open request for that. It is with the timetable master.");
        }

        await Activity.RecordAsync(ActivityActions.SelfServiceRequested, nameof(StaffConfigRequest), row.Id, me,
            $"{row.Kind} request made", null, branchId, organizationId);

        await NotifyDecidersAsync(branchId, organizationId, row, me);

        var mapped = (await MapRequestsAsync(new[] { row }, me, await HasPermissionAsync(Permissions.TimetableManage))).First();
        return CreatedAtAction(nameof(GetRequests), new { branchId }, mapped);
    }

    /// <summary>
    /// Decide one. THE ASKER CAN NEVER BE THE DECIDER, and that is checked here rather than left to
    /// the permission layer: a Director of Studies holds timetable.manage and also teaches, so the
    /// permission says yes on their own request and this is what says no.
    ///
    /// Approving does the thing. A slot becomes a lesson under the timetable lock; a class assignment
    /// becomes a ClassTeacherAssignment — which is the act that grants access to children, so it is
    /// recorded at the Confidential rung and the subject is told.
    /// </summary>
    [HttpPost("requests/{id:guid}/decide")]
    [ProducesResponseType(typeof(StaffConfigRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Decide(Guid branchId, Guid id, [FromBody] DecideConfigRequestRequest decision)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var canDecide = await HasPermissionAsync(Permissions.TimetableManage);

        var row = await Db.StaffConfigRequests.FirstOrDefaultAsync(r => r.Id == id && r.BranchId == branchId);
        // 404 rather than 403 for a request this caller may not see at all — the rule every other
        // per-subject route in this module follows, because a 403 confirms the row exists.
        if (row == null || (!canDecide && row.RequestedByUserId != me)) return NotFoundProblem("Request not found");

        if (StaffSelfService.RefuseDecision(row, me, canDecide) is { } refusal)
            return BadRequestProblem("Cannot decide", refusal);

        if (!decision.Approve && string.IsNullOrWhiteSpace(decision.Reason))
            return BadRequestProblem("A reason is needed", "Say why, so the person asking knows what to do next. A refusal with no reason sends them back to the corridor.");

        string? applyFailure = null;
        if (decision.Approve)
            applyFailure = await ApplyApprovalAsync(branchId, organizationId, row, me);

        if (applyFailure != null)
        {
            // APPROVED, AND THE WORLD HAD MOVED. Kept as Superseded rather than silently left Pending
            // so "what happened to my request" always has an answer, and so the decider is not asked
            // to decide it a second time.
            row.State = ConfigRequestState.Superseded;
            row.DecidedByUserId = me;
            row.DecidedAt = DateTime.UtcNow;
            row.DecisionReason = applyFailure;
        }
        else
        {
            row.State = decision.Approve ? ConfigRequestState.Approved : ConfigRequestState.Refused;
            row.DecidedByUserId = me;
            row.DecidedAt = DateTime.UtcNow;
            row.DecisionReason = string.IsNullOrWhiteSpace(decision.Reason) ? null : decision.Reason.Trim();
        }

        await Db.SaveChangesAsync();

        // A class assignment grants Teaching-tier access to a class of children, so the event about it
        // sits at the rung of what it is about — the standing rule for anything that touches a record
        // a reader of the log should not learn the contents of.
        await Activity.RecordAsync(ActivityActions.SelfServiceDecided, nameof(StaffConfigRequest), row.Id, row.RequestedByUserId,
            $"{row.Kind} request {row.State}", null, branchId, organizationId,
            visibility: row.Kind == ConfigRequestKind.ClassAssignment ? WelfareVisibility.Confidential : WelfareVisibility.Standard);

        await NotifyRequesterAsync(branchId, organizationId, row);

        return Ok((await MapRequestsAsync(new[] { row }, me, canDecide)).First());
    }

    /// <summary>Take back a request. Never a decision, so it needs no decider and no reason.</summary>
    [HttpPost("requests/{id:guid}/withdraw")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Withdraw(Guid branchId, Guid id)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var me = CurrentUserId();
        var row = await Db.StaffConfigRequests.FirstOrDefaultAsync(r => r.Id == id && r.BranchId == branchId && r.RequestedByUserId == me);
        if (row == null) return NotFoundProblem("Request not found");
        if (row.State != ConfigRequestState.Pending) return BadRequestProblem("Already decided", "That request has already been decided.");

        row.State = ConfigRequestState.Withdrawn;
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.SelfServiceWithdrawn, nameof(StaffConfigRequest), row.Id, me,
            $"{row.Kind} request withdrawn", null, branchId, await ResolveOrganizationIdAsync(branchId));
        return NoContent();
    }

    /// <summary>
    /// The colleague's half of a swap.
    ///
    /// A DECIDER MAY NOT MOVE A TEACHER'S LESSON WITHOUT THAT TEACHER HAVING AGREED. Automating the
    /// corridor negotiation would not replace it, it would take it away from the people having it —
    /// so a swap stays un-decidable until the other teacher says yes here.
    /// </summary>
    [HttpPost("requests/{id:guid}/agree")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Agree(Guid branchId, Guid id)
    {
        if (await VerifyBranchOwnership(branchId) is { } branchError) return branchError;

        var me = CurrentUserId();
        var row = await Db.StaffConfigRequests.FirstOrDefaultAsync(r => r.Id == id && r.BranchId == branchId && r.CounterpartUserId == me);
        if (row == null) return NotFoundProblem("Request not found");
        if (row.State != ConfigRequestState.Pending) return BadRequestProblem("Already decided", "That request has already been decided.");

        row.CounterpartAgreedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        return NoContent();
    }

    // =================================================================================================
    // Tier 3 helpers
    // =================================================================================================

    /// <summary>Why this request cannot even be made. Null means it may.</summary>
    private async Task<string?> RefuseRequestAsync(Guid branchId, Guid me, StaffConfigRequest row, CreateConfigRequestRequest request)
    {
        switch (row.Kind)
        {
            case ConfigRequestKind.ClassAssignment:
                if (string.IsNullOrWhiteSpace(row.ClassName) || row.SubjectId == null)
                    return "Name the class and the subject.";
                if (await Db.ClassTeacherAssignments.AnyAsync(a => a.BranchId == branchId && a.UserId == me
                        && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher
                        && a.SubjectId == row.SubjectId && a.ClassName.Trim().ToLower() == row.ClassNameNormalized))
                    return "You already teach that class.";
                return null;

            case ConfigRequestKind.TimetableSlot:
                if (row.CycleDay == null || string.IsNullOrWhiteSpace(row.PeriodKey))
                    return "Name the day and the period.";
                if (string.IsNullOrWhiteSpace(row.ClassName) || row.SubjectId == null)
                    return "Name the class and the subject.";
                // You may only ask for a period in a class you already hold. Asking for one in a class
                // you do not is a ClassAssignment request, which is a different and larger question.
                if (!await Db.ClassTeacherAssignments.AnyAsync(a => a.BranchId == branchId && a.UserId == me
                        && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher
                        && a.SubjectId == row.SubjectId && a.ClassName.Trim().ToLower() == row.ClassNameNormalized))
                    return "That class and subject are not one of yours. Ask to teach it first.";
                return null;

            case ConfigRequestKind.SlotSwap:
                if (request.MyLessonId == null || request.TheirLessonId == null)
                    return "A swap needs both lessons.";
                var mine = await Db.TimetableLessons.Include(l => l.Timetable)
                    .FirstOrDefaultAsync(l => l.Id == request.MyLessonId && l.TeacherUserId == me);
                if (mine == null || mine.Timetable.BranchId != branchId) return "That lesson is not one of yours.";
                var theirs = await Db.TimetableLessons.Include(l => l.Timetable)
                    .FirstOrDefaultAsync(l => l.Id == request.TheirLessonId);
                if (theirs == null || theirs.Timetable.BranchId != branchId) return "That lesson could not be found.";
                if (theirs.TeacherUserId == me) return "Both lessons are yours — move one instead of asking for a swap.";

                row.CounterpartUserId = theirs.TeacherUserId;
                row.TimetableId = mine.TimetableId;
                row.CycleDay = theirs.CycleDay;
                row.PeriodKey = theirs.PeriodKey;
                return null;

            default:
                return "That is not something that can be asked for.";
        }
    }

    /// <summary>
    /// Do what approving means. Returns null on success, or WHY it could not be applied — which is not
    /// a failure of the decision but of the world having moved since it was made.
    /// </summary>
    private async Task<string?> ApplyApprovalAsync(Guid branchId, Guid organizationId, StaffConfigRequest row, Guid deciderId)
    {
        if (row.Kind == ConfigRequestKind.ClassAssignment)
        {
            if (await Db.ClassTeacherAssignments.AnyAsync(a => a.BranchId == branchId && a.UserId == row.RequestedByUserId
                    && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher
                    && a.SubjectId == row.SubjectId && a.ClassName.Trim().ToLower() == row.ClassNameNormalized))
                return "They already teach that class.";

            var assignment = new ClassTeacherAssignment
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                ClassName = row.ClassName!,
                UserId = row.RequestedByUserId,
                Role = ClassTeacherRole.SubjectTeacher,
                SubjectId = row.SubjectId,
                PeriodsPerWeek = row.PeriodsPerWeek,
                AssignedAt = DateTime.UtcNow,
                AssignedByUserId = deciderId,
                CreatedBy = deciderId,
            };
            Db.ClassTeacherAssignments.Add(assignment);
            await Db.SaveChangesAsync();
            row.ResultAssignmentId = assignment.Id;
            return null;
        }

        if (row.Kind == ConfigRequestKind.TimetableSlot)
        {
            var timetable = await CurrentDraftAsync(branchId, tracking: true);
            if (timetable == null) return "There is no draft timetable to place it in any more.";

            await LockTimetableAsync(timetable.Id);
            var lessons = await Db.TimetableLessons.Where(l => l.TimetableId == timetable.Id).ToListAsync();

            var here = lessons.Where(l => l.CycleDay == row.CycleDay
                && string.Equals(l.PeriodKey, row.PeriodKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (here.Any(l => l.TeacherUserId == row.RequestedByUserId)) return "They already have a lesson in that period now.";
            if (here.Any(l => l.ClassNameNormalized == row.ClassNameNormalized)) return "That class is being taught then now.";

            var lesson = new TimetableLesson
            {
                TimetableId = timetable.Id,
                CycleDay = row.CycleDay!.Value,
                PeriodKey = row.PeriodKey!,
                ClassName = row.ClassName!,
                ClassNameNormalized = row.ClassNameNormalized!,
                SubjectId = row.SubjectId!.Value,
                TeacherUserId = row.RequestedByUserId,
                Room = row.Room,
                RoomNormalized = row.Room == null ? null : TimetableCycle.Normalize(row.Room),
                CreatedBy = deciderId,
            };
            Db.TimetableLessons.Add(lesson);
            try { await Db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return "Somebody placed a lesson there first."; }
            row.ResultLessonId = lesson.Id;
            return null;
        }

        if (row.Kind == ConfigRequestKind.SlotSwap)
        {
            var timetable = await CurrentDraftAsync(branchId, tracking: true);
            if (timetable == null) return "There is no draft timetable to change any more.";

            await LockTimetableAsync(timetable.Id);
            var mine = await Db.TimetableLessons.FirstOrDefaultAsync(l => l.Id == row.MyLessonId);
            var theirs = await Db.TimetableLessons.FirstOrDefaultAsync(l => l.Id == row.TheirLessonId);
            if (mine == null || theirs == null) return "One of those lessons has already been changed.";
            if (mine.GroupId != null || theirs.GroupId != null) return "One of those is a joint lesson and cannot be swapped here.";

            // Swap the SLOTS, not the teachers: each keeps their class and subject and changes when
            // they teach it. Swapping the teachers instead would move a class to a teacher who is not
            // assigned to it, which is a data-access change wearing a timetable change's clothes.
            (mine.CycleDay, theirs.CycleDay) = (theirs.CycleDay, mine.CycleDay);
            (mine.PeriodKey, theirs.PeriodKey) = (theirs.PeriodKey, mine.PeriodKey);

            try { await Db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return "That swap would double-book somebody now."; }
            row.ResultLessonId = mine.Id;
            return null;
        }

        return "That request cannot be applied.";
    }

    private async Task<List<StaffConfigRequestDto>> MapRequestsAsync(IReadOnlyCollection<StaffConfigRequest> rows, Guid me, bool canDecide)
    {
        if (rows.Count == 0) return new();

        var userIds = rows.SelectMany(r => new[] { (Guid?)r.RequestedByUserId, r.CounterpartUserId, r.DecidedByUserId })
            .Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var names = await Db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim());

        var subjectIds = rows.Where(r => r.SubjectId != null).Select(r => r.SubjectId!.Value).Distinct().ToList();
        var subjects = await Db.Subjects.AsNoTracking().Where(s => subjectIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        var settings = await _settings.ReadAsync(rows.First().BranchId);
        var cycleDays = TimetableCycle.CycleDayCount(settings);

        return rows.Select(r =>
        {
            var requester = names.GetValueOrDefault(r.RequestedByUserId) ?? "A member of staff";
            var subject = r.SubjectId != null ? subjects.GetValueOrDefault(r.SubjectId.Value) : null;
            var dayLabel = r.CycleDay is { } d ? TimetableCycle.CycleDayLabel(settings, cycleDays, d) : null;

            return new StaffConfigRequestDto
            {
                Id = r.Id,
                Kind = r.Kind,
                State = r.State,
                RequestedByUserId = r.RequestedByUserId,
                RequestedByName = requester,
                RequestedAt = r.RequestedAt,
                Reason = r.Reason,
                Summary = StaffSelfService.SummaryOf(r, requester, subject, dayLabel),
                CycleDay = r.CycleDay,
                PeriodKey = r.PeriodKey,
                ClassName = r.ClassName,
                SubjectId = r.SubjectId,
                SubjectName = subject,
                Room = r.Room,
                PeriodsPerWeek = r.PeriodsPerWeek,
                CounterpartUserId = r.CounterpartUserId,
                CounterpartName = r.CounterpartUserId is { } c ? names.GetValueOrDefault(c) : null,
                CounterpartAgreed = r.CounterpartUserId == null ? null : r.CounterpartAgreedAt != null,
                DecidedByUserId = r.DecidedByUserId,
                DecidedByName = r.DecidedByUserId is { } dd ? names.GetValueOrDefault(dd) : null,
                DecidedAt = r.DecidedAt,
                DecisionReason = r.DecisionReason,
                // The self-approval rule reaches the CLIENT too, so a decider never sees a Decide
                // button on their own request and then has it refused. The server still refuses it.
                CanIDecide = canDecide && r.State == ConfigRequestState.Pending && r.RequestedByUserId != me
                             && (r.Kind != ConfigRequestKind.SlotSwap || r.CounterpartAgreedAt != null),
                CanIWithdraw = r.State == ConfigRequestState.Pending && r.RequestedByUserId == me,
            };
        }).ToList();
    }

    /// <summary>
    /// One notification to the people who can clear it, and to the colleague when a swap needs them.
    /// A digest rather than one per decider per request is the plan's rule; this is the first half of
    /// it — the ladder that collapses repeats is the reminder engine's job, not this method's.
    /// </summary>
    private async Task NotifyDecidersAsync(Guid branchId, Guid organizationId, StaffConfigRequest row, Guid me)
    {
        try
        {
            if (row.CounterpartUserId is { } counterpart)
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = counterpart,
                    Title = "A colleague has asked to swap a lesson",
                    Message = "Open My Workspace to agree or decline.",
                    Type = NotificationType.StaffPerformance,
                    ActionUrl = "/portal?tab=teaching",
                    OrganizationId = organizationId,
                    BranchId = branchId,
                });
            }
        }
        catch (Exception ex)
        {
            // A request that was written and a notification that was not is a degraded success. The
            // same call as TryIssueVisitToken: failing the write because the bell did not ring would
            // be the worst available answer.
            _logger.LogWarning(ex, "Could not notify about self-service request {RequestId}", row.Id);
        }
    }

    private async Task NotifyRequesterAsync(Guid branchId, Guid organizationId, StaffConfigRequest row)
    {
        try
        {
            var verdict = row.State switch
            {
                ConfigRequestState.Approved => "Your request was approved",
                ConfigRequestState.Refused => "Your request was not approved",
                ConfigRequestState.Superseded => "Your request could not be applied",
                _ => "Your request was decided",
            };
            await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = row.RequestedByUserId,
                Title = verdict,
                // THE REASON IS THE POINT. A refusal with no reason sends somebody back to the
                // corridor, which is the workload this whole feature exists to remove.
                Message = row.DecisionReason ?? "Open My Workspace to see it.",
                Type = NotificationType.StaffPerformance,
                ActionUrl = "/portal?tab=teaching",
                OrganizationId = organizationId,
                BranchId = branchId,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not notify the requester of {RequestId}", row.Id);
        }
    }

    // =================================================================================================
    // Shared helpers
    // =================================================================================================

    private async Task<ClassTeacherAssignment?> FindMyAssignmentAsync(Guid branchId, Guid me, string className, Guid subjectId)
    {
        var norm = TimetableCycle.Normalize(className);
        return await Db.ClassTeacherAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.BranchId == branchId && a.UserId == me && a.EndedAt == null
                                      && a.Role == ClassTeacherRole.SubjectTeacher
                                      && a.SubjectId == subjectId
                                      && a.ClassName.Trim().ToLower() == norm);
    }

    /// <summary>
    /// The branch's Draft, which is the only version self-service ever writes. A published version is
    /// immutable by design: a change to it is a new draft published over it, and that is the master's
    /// act, not a teacher's.
    /// </summary>
    private async Task<Timetable?> CurrentDraftAsync(Guid branchId, bool tracking = false)
    {
        var q = Db.Timetables.Where(t => t.BranchId == branchId && t.Status == TimetableStatus.Draft);
        if (!tracking) q = q.AsNoTracking();
        return await q.OrderByDescending(t => t.EffectiveFrom).FirstOrDefaultAsync();
    }

    private async Task<TimetableOpeningsDto> BuildOpeningsDtoAsync(
        Guid branchId, Guid organizationId, Timetable timetable, IReadOnlyList<TimetableLesson> lessons,
        Guid me, ClassTeacherAssignment assignment, SelfServicePolicyDto policy)
    {
        var settings = await _settings.ReadAsync(branchId);
        var showsNames = await HasPermissionAsync(Permissions.TimetableManage);

        var teacherIds = lessons.Select(l => l.TeacherUserId).Distinct().ToList();
        var names = showsNames
            ? await Db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => teacherIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim())
            : new Dictionary<Guid, string>();

        var subjectIds = lessons.Select(l => l.SubjectId).Append(assignment.SubjectId ?? Guid.Empty).Distinct().ToList();
        var subjectNames = await Db.Subjects.AsNoTracking().Where(s => subjectIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        var branchSettings = await Db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var rooms = StudentsController.ReadVocabularies(branchSettings).Rooms
            .Where(r => r.IsActive).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dto = StaffSelfService.BuildOpenings(
            timetable, settings, lessons, me, assignment.ClassName, assignment.SubjectId ?? Guid.Empty,
            subjectNames.GetValueOrDefault(assignment.SubjectId ?? Guid.Empty) ?? "(no subject)",
            names, subjectNames, rooms, showsNames, assignment.PeriodsPerWeek);

        dto.CanClaim = policy.Enabled && policy.AllowDirectClaims && timetable.Status == TimetableStatus.Draft;
        dto.CannotClaimReason = dto.CanClaim ? null
            : !policy.Enabled ? "This school has not switched on staff self-service."
            : !policy.AllowDirectClaims ? "This school asks staff to request a period rather than take it."
            : "The timetable has been published, so it cannot be changed here.";
        return dto;
    }

    private Task LockTimetableAsync(Guid id)
    {
        var lockKey = $"timetable:{id}";
        return Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
