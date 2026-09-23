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
    private readonly ITimetableRepublishService _republish;
    private readonly ILogger<StaffSelfServiceController> _logger;

    public StaffSelfServiceController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        ITimetableSettingsService settings,
        IStaffPerformancePolicyService policy,
        INotificationService notifications,
        ITimetableRepublishService republish,
        ILogger<StaffSelfServiceController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _settings = settings;
        _policy = policy;
        _notifications = notifications;
        _republish = republish;
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
            // True when this caller can decide ANYTHING — the permission, or being the appointed master of some
            // version of this branch. Per-request it is narrower (StaffConfigRequestDto.CanIDecide); this one only
            // decides whether the queue is worth showing them at all.
            CanDecideRequests = await HasPermissionAsync(Permissions.TimetableManage)
                                || await Db.Timetables.AsNoTracking().AnyAsync(t => t.BranchId == branchId && t.ManagerUserIds.Contains(me)),
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
        var holdsManage = await HasPermissionAsync(Permissions.TimetableManage);

        // An appointed master reads the queue for the versions they manage, and their own requests besides. They
        // do NOT read the whole branch's queue, which is the difference between being appointed to one timetable
        // and holding the permission over all of them.
        var mineToManage = holdsManage
            ? new List<Guid>()
            : await Db.Timetables.AsNoTracking().Where(t => t.BranchId == branchId && t.ManagerUserIds.Contains(me))
                .Select(t => t.Id).ToListAsync();

        var q = Db.StaffConfigRequests.AsNoTracking().Where(r => r.BranchId == branchId);
        if (!holdsManage)
            q = q.Where(r => r.RequestedByUserId == me || r.CounterpartUserId == me
                             || (r.TimetableId != null && mineToManage.Contains(r.TimetableId.Value)));
        if (openOnly) q = q.Where(r => r.State == ConfigRequestState.Pending);

        var rows = await q.OrderByDescending(r => r.RequestedAt).Take(200).ToListAsync();
        return Ok(await MapRequestsAsync(rows, me, holdsManage));
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

        // Computed AFTER the refusals, because a swap fills in its own slot from the lesson it names.
        // Never null, so the unique index can actually see a duplicate — see DedupeKey's own note.
        row.DedupeKey = string.Join('|', new[]
        {
            row.Kind.ToString(),
            row.CycleDay?.ToString() ?? "-",
            (row.PeriodKey ?? "-").ToLowerInvariant(),
            row.ClassNameNormalized ?? "-",
            row.SubjectId?.ToString() ?? "-",
            row.TheirLessonId?.ToString() ?? "-",
            // THE DATE IS PART OF WHAT A REQUEST IS ABOUT. Without it, a teacher who arranged cover for this
            // Tuesday could not arrange it for the next one, and a one-off swap and a permanent one for the same
            // pair of lessons would collide as duplicates of each other.
            row.EffectiveOn?.ToString("yyyy-MM-dd") ?? "-",
            row.CoverUserId?.ToString() ?? "-",
        });

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

        var row = await Db.StaffConfigRequests.FirstOrDefaultAsync(r => r.Id == id && r.BranchId == branchId);
        // The permission is enough to SEE the queue; deciding a particular request is narrower — see below.
        var holdsManage = await HasPermissionAsync(Permissions.TimetableManage);
        // 404 rather than 403 for a request this caller may not see at all — the rule every other
        // per-subject route in this module follows, because a 403 confirms the row exists.
        if (row == null || (!holdsManage && row.RequestedByUserId != me && row.CounterpartUserId != me)) return NotFoundProblem("Request not found");

        var canDecide = await MayDecideAsync(row);
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
    /// The colleague's half of a swap, or of a cover request.
    ///
    /// A DECIDER MAY NOT MOVE A TEACHER'S LESSON WITHOUT THAT TEACHER HAVING AGREED. Automating the
    /// corridor negotiation would not replace it, it would take it away from the people having it —
    /// so a swap stays un-decidable until the other teacher says yes here, and cover is on exactly the
    /// same footing: nobody is volunteered to stand in front of a class by somebody else.
    ///
    /// The route takes the request id and nothing else. The colleague is <c>CounterpartUserId</c>, read from the
    /// row rather than the body, so this cannot be used to agree on somebody else's behalf.
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

        // The asker hears back, because until now the only signal that a colleague had agreed was the queue
        // screen changing shape, and the person waiting is not the person looking at it.
        try
        {
            var names = await Db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == me)
                .Select(u => PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName)).FirstOrDefaultAsync();
            await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = row.RequestedByUserId,
                OrganizationId = row.OrganizationId,
                BranchId = branchId,
                Title = row.Kind == ConfigRequestKind.LessonCover ? "A colleague agreed to cover your lesson" : "A colleague agreed to your swap",
                Message = $"{(string.IsNullOrWhiteSpace(names) ? "Your colleague" : names)} has agreed. It is with the timetable master now.",
                Type = NotificationType.StaffPerformance,
                Priority = NotificationPriority.Normal,
                Channels = NotificationChannel.InApp,
                EventKey = NotificationEventKeys.StaffLessonCover,
                ActionUrl = "/portal?tab=teaching",
                IconClass = "check2"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agreement notice for request {RequestId} could not be sent", row.Id);
        }

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
                if (mine.TimetableId != theirs.TimetableId) return "Those two lessons are on different timetables.";
                if (mine.GroupId != null || theirs.GroupId != null)
                    return "One of those is a joint lesson, taught with somebody else. Ask the timetable master to change it.";

                row.CounterpartUserId = theirs.TeacherUserId;
                row.TimetableId = mine.TimetableId;
                row.CycleDay = theirs.CycleDay;
                row.PeriodKey = theirs.PeriodKey;
                // A ONE-OFF swap names the date of the asker's own lesson; a permanent one names none. The date is
                // checked against the timetable and the cycle here, so the two teachers cannot agree to something
                // that was never going to be applicable.
                return await RefuseOneOffAsync(branchId, row, mine, request.EffectiveOn, oneOffRequired: false);

            case ConfigRequestKind.LessonCover:
                if (request.MyLessonId == null) return "Name the lesson you need covered.";
                if (request.CoverUserId == null || request.CoverUserId == Guid.Empty) return "Name the colleague you are asking.";
                if (request.CoverUserId == me) return "That is already your lesson.";
                var toCover = await Db.TimetableLessons.Include(l => l.Timetable)
                    .FirstOrDefaultAsync(l => l.Id == request.MyLessonId && l.TeacherUserId == me);
                if (toCover == null || toCover.Timetable.BranchId != branchId) return "That lesson is not one of yours.";
                if (toCover.GroupId != null)
                    return "That is a joint lesson, taught with somebody else. Ask the timetable master to arrange cover.";
                // BranchStaff, not a hand-written branch test: somebody with no assigned branch belongs to every
                // branch of the organization, and refusing them would refuse a real colleague.
                if (!await StaffLookups.BranchStaff(Db, row.OrganizationId, branchId).AnyAsync(u => u.Id == request.CoverUserId))
                    return "That person is not active staff of this branch.";

                row.CoverUserId = request.CoverUserId;
                // The colleague being asked IS the counterpart, so the agreement half needs no second rule: the
                // same endpoint, the same index, the same "waiting on me" list on their portal.
                row.CounterpartUserId = request.CoverUserId;
                row.TimetableId = toCover.TimetableId;
                row.CycleDay = toCover.CycleDay;
                row.PeriodKey = toCover.PeriodKey;
                row.ClassName = toCover.ClassName;
                row.ClassNameNormalized = toCover.ClassNameNormalized;
                row.SubjectId = toCover.SubjectId;
                return await RefuseOneOffAsync(branchId, row, toCover, request.EffectiveOn, oneOffRequired: true);

            default:
                return "That is not something that can be asked for.";
        }
    }

    /// <summary>
    /// WHO MAY DECIDE THIS REQUEST — the permission, OR being an appointed master of the timetable it is about
    /// (2026-09-22). Unified with <see cref="TimetableAccess.MayWrite"/> on purpose, because deciding a request
    /// IS a write to that timetable, and the two rules disagreeing produced a real hole in both directions:
    ///
    ///   * an appointed timetable master holding no permission could not decide a swap on their OWN timetable,
    ///     which is precisely the job they were appointed to do;
    ///   * a permission holder who owns a different version could decide swaps on one they have nothing to do with.
    ///
    /// A ClassAssignment has no timetable, so it stays on the permission alone. That is right rather than a gap: it
    /// grants Teaching-tier access to a class of children, which is not a timetable master's decision to make.
    /// </summary>
    private async Task<bool> MayDecideAsync(StaffConfigRequest row)
    {
        var holds = await HasPermissionAsync(Permissions.TimetableManage);
        if (holds) return true;
        if (row.TimetableId is not { } timetableId) return false;
        var timetable = await Db.Timetables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == timetableId);
        return timetable != null && TimetableAccess.IsManager(timetable, CurrentUserId());
    }

    /// <summary>
    /// The date half of a one-off request. Null return means it may be asked for.
    ///
    /// THE PREVIEW AND THE APPLY RUN THE SAME RULES. Everything here is <see cref="TimetableExceptions.Refuse"/>,
    /// which is also what the master's own cover endpoint calls and what the approval calls — a row the request
    /// step accepted and the approval then refuses in different words is the worst answer this can give.
    /// </summary>
    private async Task<string?> RefuseOneOffAsync(Guid branchId, StaffConfigRequest row, TimetableLesson lesson, DateOnly? effectiveOn, bool oneOffRequired)
    {
        if (effectiveOn == null)
        {
            if (oneOffRequired) return "Say which day you need it covered.";
            // A permanent swap. It rewrites the timetable for the rest of its life, so it only makes sense against
            // a version that is live or still being built.
            if (lesson.Timetable.Status == TimetableStatus.Archived)
                return "That timetable is history. A swap on it would change nothing.";
            return null;
        }

        row.EffectiveOn = effectiveOn;

        // A one-off is a dated exception, and an exception only exists against a PUBLISHED version — a draft is
        // changed by editing it, which is what a permanent swap does.
        if (lesson.Timetable.Status != TimetableStatus.Published)
            return "That timetable is not published yet, so there is no single day to change. Ask for the swap to be permanent instead.";

        var settings = await _settings.ReadAsync(branchId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, await ZoneAsync(branchId)));
        return TimetableExceptions.Refuse(lesson.Timetable, lesson, effectiveOn.Value, today,
            LessonExceptionKind.Cover, row.CoverUserId ?? row.CounterpartUserId, settings);
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

        // A ONE-OFF: two covers for a swap, one for a cover request. Nothing moves, so no class, room or cohort
        // can be disturbed and TimetableChecker has nothing to say about it.
        if (StaffSelfService.IsOneOff(row))
            return await ApplyOneOffAsync(branchId, organizationId, row, deciderId);

        if (row.Kind == ConfigRequestKind.SlotSwap)
            return await ApplyPermanentSwapAsync(branchId, organizationId, row, deciderId);

        return "That request cannot be applied.";
    }

    /// <summary>
    /// A ONE-OFF swap or a cover: dated <see cref="TimetableLessonException"/> rows against the PUBLISHED version.
    ///
    /// A one-off swap is TWO COVERS, each teacher taking the other's lesson at its own time, and the colleague's
    /// date is derived from their cycle day within the same cycle week — which is what lets the request carry one
    /// date rather than two that could disagree.
    /// </summary>
    private async Task<string?> ApplyOneOffAsync(Guid branchId, Guid organizationId, StaffConfigRequest row, Guid deciderId)
    {
        if (row.EffectiveOn is not { } myDate) return "That request has no date to apply.";

        var mine = await Db.TimetableLessons.Include(l => l.Timetable).AsNoTracking().FirstOrDefaultAsync(l => l.Id == row.MyLessonId);
        if (mine == null) return "That lesson has already been changed.";
        var timetable = mine.Timetable;
        if (timetable.Status != TimetableStatus.Published) return "That timetable is no longer the published one.";

        var settings = await _settings.ReadAsync(branchId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, await ZoneAsync(branchId)));

        // Who takes the asker's lesson: the colleague, either way round.
        var counterpart = row.CoverUserId ?? row.CounterpartUserId;
        if (counterpart == null) return "That request names nobody to cover it.";

        var writes = new List<(TimetableLesson Lesson, DateOnly Date, Guid Cover)> { (mine, myDate, counterpart.Value) };

        if (row.Kind == ConfigRequestKind.SlotSwap)
        {
            var theirs = await Db.TimetableLessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == row.TheirLessonId);
            if (theirs == null) return "One of those lessons has already been changed.";
            var theirDate = TimetableExceptions.SameCycleDateFor(settings, timetable.CycleDays, timetable.EffectiveFrom, myDate, theirs.CycleDay);
            if (theirDate == null) return "The other lesson's day does not fall in the same week.";
            // The other half. The asker takes the colleague's lesson on the colleague's own date.
            writes.Add((theirs, theirDate.Value, row.RequestedByUserId));
        }

        foreach (var (lesson, date, cover) in writes)
            if (TimetableExceptions.Refuse(timetable, lesson, date, today, LessonExceptionKind.Cover, cover, settings) is { } why)
                return why;

        await LockTimetableAsync(timetable.Id);
        foreach (var (lesson, date, cover) in writes)
        {
            Db.TimetableLessonExceptions.Add(new TimetableLessonException
            {
                OrganizationId = organizationId, BranchId = branchId, TimetableId = timetable.Id,
                TimetableLessonId = lesson.Id, Date = date, Kind = LessonExceptionKind.Cover, CoverUserId = cover,
                Reason = row.Reason, SourceRequestId = row.Id, CreatedByUserId = deciderId, CreatedBy = deciderId
            });
        }

        try { await Db.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return "One of those lessons already has cover on that date.";
        }

        row.ResultLessonId = mine.Id;
        // The duty may already exist inside the fortnight window, so it has to change hands now rather than at the
        // nightly run — otherwise cover approved today for tomorrow reaches nobody's My Day.
        try { Hangfire.BackgroundJob.Enqueue<QMgr.Infrastructure.Jobs.LessonGenerationJob>(job => job.RunForBranchAsync(branchId)); }
        catch (Exception ex) { _logger.LogError(ex, "Could not enqueue lesson generation for branch {BranchId} after cover", branchId); }
        return null;
    }

    /// <summary>
    /// A PERMANENT swap: the two teachers trade slots for the rest of the version's life.
    ///
    /// ON A DRAFT it is an edit, exactly as it always was. ON A PUBLISHED VERSION it is a RE-PUBLISH — a new draft
    /// copied from it with the two slots traded, then published over it — because a published version is immutable
    /// by design and "a change to it is a new draft published over it" is this module's own rule. That path is also
    /// the only one that runs <see cref="TimetableChecker"/>, which is where the CLASS-side effect neither teacher
    /// can see gets raised: two teachers can agree to trade Tuesday P3 for Thursday P5 and leave a cohort with
    /// double Maths and no Physics that week.
    ///
    /// THE DATE RANGE IS KEPT, NOT SPLIT AT THE SWAP DATE, and that is deliberate: cycle day 1 is anchored on
    /// <c>EffectiveFrom</c>, so moving it would shift every cycle day of an A/B timetable. The archived version
    /// stays as the record of what the timetable said before.
    /// </summary>
    private async Task<string?> ApplyPermanentSwapAsync(Guid branchId, Guid organizationId, StaffConfigRequest row, Guid deciderId)
    {
        var mine = await Db.TimetableLessons.Include(l => l.Timetable).AsNoTracking().FirstOrDefaultAsync(l => l.Id == row.MyLessonId);
        var theirs = await Db.TimetableLessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == row.TheirLessonId);
        if (mine == null || theirs == null) return "One of those lessons has already been changed.";
        if (mine.GroupId != null || theirs.GroupId != null) return "One of those is a joint lesson and cannot be swapped here.";
        var source = mine.Timetable;

        if (source.Status == TimetableStatus.Draft)
        {
            await LockTimetableAsync(source.Id);
            var a = await Db.TimetableLessons.FirstOrDefaultAsync(l => l.Id == mine.Id);
            var b = await Db.TimetableLessons.FirstOrDefaultAsync(l => l.Id == theirs.Id);
            if (a == null || b == null) return "One of those lessons has already been changed.";

            // Swap the SLOTS, not the teachers: each keeps their class and subject and changes when they teach it.
            // Swapping the teachers instead would move a class to a teacher who is not assigned to it, which is a
            // data-access change wearing a timetable change's clothes.
            (a.CycleDay, b.CycleDay) = (b.CycleDay, a.CycleDay);
            (a.PeriodKey, b.PeriodKey) = (b.PeriodKey, a.PeriodKey);

            try { await Db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return "That swap would double-book somebody now."; }
            row.ResultLessonId = a.Id;
            return null;
        }

        if (source.Status != TimetableStatus.Published)
            return "That timetable is history now, so a permanent swap on it would change nothing.";

        var result = await _republish.SwapAndRepublishAsync(source.Id, mine.Id, theirs.Id, deciderId,
            $"Swap approved from a staff request: {mine.ClassName} {mine.PeriodKey} and {theirs.ClassName} {theirs.PeriodKey}");
        if (result.Refusal != null) return result.Refusal;
        row.ResultLessonId = result.MyNewLessonId;
        return null;
    }

    private async Task<List<StaffConfigRequestDto>> MapRequestsAsync(IReadOnlyCollection<StaffConfigRequest> rows, Guid me, bool holdsManage)
    {
        if (rows.Count == 0) return new();

        var userIds = rows.SelectMany(r => new[] { (Guid?)r.RequestedByUserId, r.CounterpartUserId, r.DecidedByUserId })
            .Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var names = await Db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName));

        var subjectIds = rows.Where(r => r.SubjectId != null).Select(r => r.SubjectId!.Value).Distinct().ToList();
        var subjects = await Db.Subjects.AsNoTracking().Where(s => subjectIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        var settings = await _settings.ReadAsync(rows.First().BranchId);
        var cycleDays = TimetableCycle.CycleDayCount(settings);

        // WHICH OF THESE MAY I DECIDE — per row, not per caller, since 2026-09-22. An appointed master decides
        // requests about THEIR OWN version and nobody else's, so the answer differs row by row and the client's
        // button has to follow the server's rule exactly or a decider is offered one that then refuses.
        var timetableIds = rows.Where(r => r.TimetableId != null).Select(r => r.TimetableId!.Value).Distinct().ToList();
        var managedByMe = holdsManage || timetableIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Timetables.AsNoTracking()
                .Where(t => timetableIds.Contains(t.Id) && t.ManagerUserIds.Contains(me))
                .Select(t => t.Id).ToListAsync()).ToHashSet();

        // What a permanent swap would do to anybody other than the two teachers. Computed only for a pending one
        // a reader may actually decide: it runs the clash checker, so doing it for every historic row on the queue
        // screen would be a query storm for information nobody can act on.
        var previews = new Dictionary<Guid, SwapPreview>();
        foreach (var r in rows.Where(r => r.State == ConfigRequestState.Pending
                                          && r.Kind == ConfigRequestKind.SlotSwap && r.EffectiveOn == null
                                          && r.TimetableId != null && r.MyLessonId != null && r.TheirLessonId != null
                                          && (holdsManage || managedByMe.Contains(r.TimetableId!.Value))))
        {
            previews[r.Id] = await _republish.PreviewSwapAsync(r.TimetableId!.Value, r.MyLessonId!.Value, r.TheirLessonId!.Value);
        }

        return rows.Select(r =>
        {
            var requester = names.GetValueOrDefault(r.RequestedByUserId) ?? "A member of staff";
            var subject = r.SubjectId != null ? subjects.GetValueOrDefault(r.SubjectId.Value) : null;
            var dayLabel = r.CycleDay is { } d ? TimetableCycle.CycleDayLabel(settings, cycleDays, d) : null;
            var preview = previews.GetValueOrDefault(r.Id);
            // A ClassAssignment carries no timetable, so it stays on the permission alone: it grants Teaching-tier
            // access to a class of children, which is not a timetable master's decision to make.
            var canDecide = holdsManage || (r.TimetableId is { } tid && managedByMe.Contains(tid));

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
                EffectiveOn = r.EffectiveOn,
                IsOneOff = StaffSelfService.IsOneOff(r),
                DecisionWarnings = preview?.Warnings ?? new(),
                Blocked = preview?.Blocked,
                // The self-approval rule reaches the CLIENT too, so a decider never sees a Decide
                // button on their own request and then has it refused. The server still refuses it.
                CanIDecide = canDecide && r.State == ConfigRequestState.Pending && r.RequestedByUserId != me
                             && (!StaffSelfService.NeedsCounterpart(r.Kind) || r.CounterpartAgreedAt != null)
                             && preview?.Blocked == null,
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
                    Title = row.Kind == ConfigRequestKind.LessonCover
                        ? "A colleague has asked you to cover a lesson"
                        : "A colleague has asked to swap a lesson",
                    Message = "Open My Workspace to agree or decline.",
                    Type = NotificationType.StaffPerformance,
                    ActionUrl = "/portal?tab=teaching",
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    EventKey = NotificationEventKeys.StaffLessonCover,
                });
            }

            // THE APPOINTED MASTER OF THE VERSION HEARS ABOUT IT, because they are now a decider and a queue
            // whose decider is never told is a queue that sits. Permission holders are deliberately NOT mailed
            // one notification per request — that is the digest the reminder ladder owns, and the plan's rule.
            if (row.TimetableId is { } timetableId)
            {
                var managers = await Db.Timetables.AsNoTracking().Where(t => t.Id == timetableId)
                    .Select(t => t.ManagerUserIds).FirstOrDefaultAsync() ?? Array.Empty<Guid>();
                foreach (var manager in managers.Where(x => x != me && x != row.CounterpartUserId))
                {
                    await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                    {
                        UserId = manager,
                        Title = "A request is waiting on your timetable",
                        Message = "Open My Workspace to approve or refuse it.",
                        Type = NotificationType.StaffPerformance,
                        ActionUrl = "/portal?tab=teaching",
                        OrganizationId = organizationId,
                        BranchId = branchId,
                        EventKey = NotificationEventKeys.StaffLessonCover,
                    });
                }
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
                .ToDictionaryAsync(u => u.Id, u => PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName))
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

    /// <summary>
    /// The branch's own time zone. A one-off is dated in the SCHOOL's local day, not the server's — "cover my
    /// Thursday" is a school's Thursday, and on a UTC server in Kampala the two differ for three hours a day.
    /// </summary>
    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => AppointmentScheduling.ResolveTimeZone(await Db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
