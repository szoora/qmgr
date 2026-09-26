using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Domain.Identity;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Lesson plans and schemes of work (plan LESSON_PLANS_AND_SCHEMES_OF_WORK, 2026-09-26): write on the form, submit,
/// forward, approve, return, withdraw, comment, reflect, revise, copy to parallel streams, bump to another lesson; the
/// planbook, the review queue, records of work and the reports; the one uploaded PDF; Word templates; the curriculum.
///
/// <para>Every decision about who may read or act is <see cref="TeachingPlans.AccessForAsync"/>; out of reach is 404,
/// never 403. Nobody takes two stages of one plan and nobody decides on their own (<see cref="DutySeparation"/>). Every
/// transition saves through the row's xmin, so two reviewers pressing at once cannot both act: the second gets 409.</para>
///
/// <para>Notifications name the plan, never its content.</para>
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/teaching-plans")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public partial class TeachingPlansController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly INotificationService _notifications;
    private readonly IMediaStorageService _mediaStorage;
    private readonly IUsageTrackingService _usage;
    private readonly IStaffSystemAwards _awards;
    private readonly ILogger<TeachingPlansController> _logger;

    public TeachingPlansController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        INotificationService notifications,
        IMediaStorageService mediaStorage,
        IUsageTrackingService usage,
        IStaffSystemAwards awards,
        ILogger<TeachingPlansController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _awards = awards;
        _policy = policy;
        _notifications = notifications;
        _mediaStorage = mediaStorage;
        _usage = usage;
        _logger = logger;
    }

    // ---- One plan ----------------------------------------------------------------------------------------

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TeachingPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: false);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        return Ok(await MapAsync(ctx));
    }

    /// <summary>
    /// Starts a plan, filled in as far as the product can: the header from the timetable and the roll, the topic and
    /// outcomes from this week's line of the approved scheme (or the curriculum list), and — when asked — the content of
    /// the author's last plan for the same classes. A plan that already exists for the lesson is returned, not duplicated.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TeachingPlanDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(TeachingPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(Guid branchId, [FromBody] CreateTeachingPlanRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var policy = await _policy.GetAsync(organizationId);
        var settings = _policy.PlanSettings(policy);
        var zone = await ZoneAsync(branchId);

        if (request.ClientRequestId is { } clientId)
        {
            var again = await Db.TeachingPlans.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.ClientRequestId == clientId).Select(p => p.Id).FirstOrDefaultAsync();
            if (again != Guid.Empty) return await ReturnExistingAsync(branchId, again);
        }

        Guid subjectId;
        List<string> classes;
        DateOnly? lessonDate = null;
        StaffDuty? duty = null;
        if (request.Kind == TeachingPlanKind.LessonPlan && request.DutyId is { } dutyId)
        {
            duty = await Db.StaffDuties.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId
                                                                                && d.Kind == DutyKind.Lesson && d.IsActive);
            // Only the lesson's own teacher plans it (a cover included). Anything else reads as a lesson that does not exist.
            if (duty == null || duty.ExpectedUserIds == null || !duty.ExpectedUserIds.Contains(me) || duty.SubjectId == null || string.IsNullOrWhiteSpace(duty.ClassName))
                return NotFoundProblem("Lesson not found");
            var existing = await Db.TeachingPlans.AsNoTracking()
                .Where(p => p.AuthorUserId == me && p.DutyId == dutyId && p.IsCurrent && p.Status != TeachingPlanStatus.Withdrawn)
                .Select(p => p.Id).FirstOrDefaultAsync();
            if (existing != Guid.Empty) return await ReturnExistingAsync(branchId, existing);
            subjectId = duty.SubjectId.Value;
            classes = new() { duty.ClassName! };
            lessonDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(duty.StartsAt, zone));
        }
        else
        {
            if (request.SubjectId is not { } s) return BadRequestProblem("Choose the subject");
            subjectId = s;
            classes = (request.ClassNames ?? new()).Select(c => ClassName.Display(c)).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (classes.Count == 0) return BadRequestProblem("Choose at least one class");
            if (request.Kind == TeachingPlanKind.LessonPlan)
            {
                if (request.LessonDate is not { } d) return BadRequestProblem("Choose the date of the lesson");
                lessonDate = d;
            }
            // FAIL CLOSED: nobody plans for a class they do not teach.
            var taught = await TeachingPlans.TaughtClassesAsync(Db, organizationId, branchId, me, subjectId);
            if (!TeachingPlans.Covers(taught, classes))
                return BadRequestProblem("You do not teach that class", "Plans are written for the classes you are assigned to teach in this subject. Ask the Director of Studies to assign you, or choose one of your classes.");
        }

        var period = lessonDate is { } ld ? _policy.PeriodFor(policy, ld)
                     : _policy.FindPeriod(policy, request.PeriodKey) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone)));
        var classKey = TeachingPlanRules.ClassKey(classes);

        if (request.Kind == TeachingPlanKind.SchemeOfWork)
        {
            var existing = await Db.TeachingPlans.AsNoTracking()
                .Where(p => p.BranchId == branchId && p.AuthorUserId == me && p.Kind == TeachingPlanKind.SchemeOfWork && p.SubjectId == subjectId
                            && p.ClassKey == classKey && p.PeriodKey == period.Key && p.IsCurrent && p.Status != TeachingPlanStatus.Withdrawn)
                .Select(p => p.Id).FirstOrDefaultAsync();
            if (existing != Guid.Empty) return await ReturnExistingAsync(branchId, existing);
        }

        var chain = await TeachingPlans.ChainAsync(Db, organizationId, subjectId);
        var plan = new TeachingPlan
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            Kind = request.Kind,
            AuthorUserId = me,
            SubjectId = subjectId,
            ClassNames = classes.ToArray(),
            ClassKey = classKey,
            PeriodKey = period.Key,
            LessonDate = lessonDate,
            DutyId = duty?.Id,
            TimetableLessonId = duty?.TimetableLessonId,
            Stages = TeachingPlans.StagesFor(request.Kind, settings),
            ClientRequestId = request.ClientRequestId,
            CreatedBy = me
        };
        var classText = string.Join(", ", classes);
        plan.Title = request.Kind == TeachingPlanKind.SchemeOfWork
            ? Truncate($"{chain.SubjectName} · {classText} · {period.Name}", 200)
            : Truncate(string.Create(CultureInfo.InvariantCulture, $"{chain.SubjectName} · {classText} · {lessonDate:ddd dd MMM yyyy}"), 200);

        if (request.Kind == TeachingPlanKind.LessonPlan)
        {
            var content = await PrefillLessonAsync(plan, duty, period, settings, zone, request);
            plan.SectionsJson = TeachingPlans.Serialize(content);
        }
        else
        {
            plan.RowsJson = TeachingPlans.Serialize(await PrefillSchemeAsync(plan, period, settings));
        }

        Db.TeachingPlans.Add(plan);
        try { await Db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Two presses at once: the unique index let one through. Return that one.
            Db.ChangeTracker.Clear();
            var winner = await Db.TeachingPlans.AsNoTracking()
                .Where(p => p.OrganizationId == organizationId && ((request.ClientRequestId != null && p.ClientRequestId == request.ClientRequestId)
                            || (plan.DutyId != null && p.AuthorUserId == me && p.DutyId == plan.DutyId && p.IsCurrent && p.Status != TeachingPlanStatus.Withdrawn)
                            || (plan.Kind == TeachingPlanKind.SchemeOfWork && p.AuthorUserId == me && p.Kind == plan.Kind && p.SubjectId == subjectId && p.ClassKey == classKey && p.PeriodKey == period.Key && p.IsCurrent && p.Status != TeachingPlanStatus.Withdrawn)))
                .Select(p => p.Id).FirstOrDefaultAsync();
            if (winner == Guid.Empty) throw;
            return await ReturnExistingAsync(branchId, winner);
        }

        await Activity.RecordAsync(ActivityActions.TeachingPlanCreated, nameof(TeachingPlan), plan.Id, me,
            $"{KindText(plan.Kind)} started: {plan.Title}", null, branchId, organizationId);

        var ctx = await LoadAsync(branchId, plan.Id, track: false);
        return CreatedAtAction(nameof(Get), new { branchId, id = plan.Id }, await MapAsync(ctx!));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TeachingPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Save(Guid branchId, Guid id, [FromBody] SaveTeachingPlanRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanEdit) return ConflictProblem("This plan cannot be changed now", "A submitted plan is frozen. Withdraw it first, or wait for it to be returned.");
        var p = ctx.Plan;
        Db.Entry(p).Property(x => x.RowVersion).OriginalValue = request.RowVersion;

        if (!string.IsNullOrWhiteSpace(request.Title)) p.Title = Truncate(request.Title.Trim(), 200);
        if (request.ClassNames is { Count: > 0 } wanted)
        {
            var classes = wanted.Select(c => ClassName.Display(c)).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var problem = await ClassesProblemAsync(p, classes);
            if (problem != null) return problem;
            p.ClassNames = classes.ToArray();
            p.ClassKey = TeachingPlanRules.ClassKey(classes);
        }
        if (p.Kind == TeachingPlanKind.LessonPlan && request.Content != null)
        {
            var cleaned = TeachingPlans.Clean(request.Content, TeachingPlanDefaults.SectionsOf(ctx.Settings));
            // The header is the product's snapshot, not the author's to retype — except the learner count, which a
            // teacher corrects on the day.
            var stored = TeachingPlans.ContentOf(p).Header;
            cleaned.Header = stored with { Learners = request.Content.Header?.Learners is > 0 and < 1000 ? request.Content.Header.Learners : stored.Learners };
            p.SectionsJson = TeachingPlans.Serialize(cleaned);
        }
        if (p.Kind == TeachingPlanKind.SchemeOfWork && request.Rows != null)
            p.RowsJson = TeachingPlans.Serialize(TeachingPlans.Clean(request.Rows, TeachingPlanDefaults.ColumnsOf(ctx.Settings)));
        if (p.Kind == TeachingPlanKind.LessonPlan && request.SchemeId is { } schemeId)
        {
            var scheme = await Db.TeachingPlans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == schemeId && s.BranchId == branchId && s.Kind == TeachingPlanKind.SchemeOfWork && s.SubjectId == p.SubjectId);
            if (scheme == null) return BadRequestProblem("That scheme of work was not found");
            p.SchemeId = schemeId;
            p.SchemeRowKey = request.SchemeRowKey;
        }
        p.UpdatedAt = DateTime.UtcNow;
        p.UpdatedBy = CurrentUserId();

        if (await SaveOrConflictAsync() is { } conflict) return conflict;
        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    // ---- The chain ---------------------------------------------------------------------------------------

    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> Submit(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanSubmit) return ConflictProblem("This plan cannot be submitted now");
        var p = ctx.Plan;

        var problems = TeachingPlanRules.SubmitProblems(p.Kind, TeachingPlans.ContentOf(p), TeachingPlans.RowsOf(p),
            TeachingPlanDefaults.SectionsOf(ctx.Settings), TeachingPlanDefaults.ColumnsOf(ctx.Settings), p.FileUrl != null);
        if (problems.Count > 0)
            return BadRequest(new ProblemDetails { Title = "Finish the plan first", Detail = string.Join(" ", problems), Status = StatusCodes.Status400BadRequest });

        var me = CurrentUserId();
        var now = DateTime.UtcNow;
        p.Stages = TeachingPlans.StagesFor(p.Kind, ctx.Settings);
        p.SubmittedAt = now;
        p.WaitingSince = now;
        p.ReturnedAt = null; p.ReturnedByUserId = null; p.ReturnReason = null;
        p.ForwardedAt = null; p.ForwardedByUserId = null; p.StageSkippedReason = null;
        p.ReviewReminderStage = 0;
        TeachingPlans.AddTrail(p, PlanTrailKind.Submitted, me);

        var stage1 = ctx.Chain.Stage1For(p.AuthorUserId);
        if (stage1.Count == 0)
        {
            // Nobody else leads the department: the stage is skipped and SAID, never handed to the author.
            p.StageSkippedReason = ctx.Chain.DepartmentId == null
                ? $"{ctx.Chain.SubjectName} is in no department, so it went straight to the approver."
                : ctx.Chain.HeadUserId == p.AuthorUserId
                    ? $"The author heads {ctx.Chain.DepartmentName} and there is no deputy, so it went straight to the approver."
                    : $"{ctx.Chain.DepartmentName} has no head of department, so it went straight to the approver.";
            p.Status = TeachingPlanStatus.Forwarded;
            TeachingPlans.AddTrail(p, PlanTrailKind.StageSkipped, null, p.StageSkippedReason);
        }
        else p.Status = TeachingPlanStatus.Submitted;
        Touch(p, me);

        if (await SaveOrConflictAsync() is { } conflict) return conflict;
        await Activity.RecordAsync(ActivityActions.TeachingPlanSubmitted, nameof(TeachingPlan), p.Id, p.AuthorUserId,
            $"{KindText(p.Kind)} submitted: {p.Title}", null, branchId, p.OrganizationId);
        if (p.StageSkippedReason != null)
            await Activity.RecordAsync(ActivityActions.TeachingPlanStageSkipped, nameof(TeachingPlan), p.Id, p.AuthorUserId,
                $"Head-of-department stage skipped for {p.Title}: {p.StageSkippedReason}", null, branchId, p.OrganizationId);

        var author = await NameAsync(p.AuthorUserId);
        if (p.Status == TeachingPlanStatus.Submitted)
            await NotifyAsync(stage1, p, NotificationEventKeys.StaffPlanSubmitted, $"{author}'s {KindWord(p.Kind)} awaits your review", p.Title, "/admin/timetable?tab=plans");
        else
            await NotifyAsync(await TeachingPlans.ApproversAsync(Db, p, SeesStaffAsync), p, NotificationEventKeys.StaffPlanForwarded,
                $"{author}'s {KindWord(p.Kind)} awaits approval", p.Title, "/admin/timetable?tab=plans");

        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    /// <summary>The author takes a plan back before anyone has decided it. It returns to Draft.</summary>
    [HttpPost("{id:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanWithdraw) return ConflictProblem("This plan cannot be withdrawn now");
        var p = ctx.Plan;
        var me = CurrentUserId();
        p.Status = TeachingPlanStatus.Draft;
        p.ForwardedAt = null; p.ForwardedByUserId = null; p.StageSkippedReason = null; p.WaitingSince = null;
        TeachingPlans.AddTrail(p, PlanTrailKind.Withdrawn, me);
        Touch(p, me);
        if (await SaveOrConflictAsync() is { } conflict) return conflict;
        await Activity.RecordAsync(ActivityActions.TeachingPlanWithdrawn, nameof(TeachingPlan), p.Id, p.AuthorUserId,
            $"{KindText(p.Kind)} withdrawn to draft: {p.Title}", null, branchId, p.OrganizationId);
        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    /// <summary>The author discards a draft or a returned plan. The row stays (Withdrawn); nobody else ever saw a draft.</summary>
    [HttpPost("{id:guid}/discard")]
    public async Task<IActionResult> Discard(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanEdit) return ConflictProblem("Only a draft or a returned plan can be discarded");
        var p = ctx.Plan;
        p.Status = TeachingPlanStatus.Withdrawn;
        if (p.SupersedesId != null) p.IsCurrent = false;
        TeachingPlans.AddTrail(p, PlanTrailKind.Withdrawn, CurrentUserId(), "Discarded");
        Touch(p, CurrentUserId());
        if (await SaveOrConflictAsync() is { } conflict) return conflict;
        return NoContent();
    }

    /// <summary>Stage 1 of a two-stage plan: the head of department sends it on to the approver.</summary>
    [HttpPost("{id:guid}/forward")]
    public async Task<IActionResult> Forward(Guid branchId, Guid id, [FromBody] PlanDecisionRequest? request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        var me = CurrentUserId();
        if (DutySeparation.Refusal(me, ctx.Plan.AuthorUserId, "review your own plan") is { } own) return DutySeparation.Problem(own);
        if (!ctx.Access.CanForward) return ConflictProblem("This plan is not waiting on you to forward it");

        var p = ctx.Plan;
        var now = DateTime.UtcNow;
        p.Status = TeachingPlanStatus.Forwarded;
        p.ForwardedByUserId = me;
        p.ForwardedAt = now;
        p.WaitingSince = now;
        p.ReviewReminderStage = 0;
        TeachingPlans.AddTrail(p, PlanTrailKind.Forwarded, me, Clean(request?.Note));
        Touch(p, me);
        if (await SaveOrConflictAsync() is { } conflict) return conflict;

        await Activity.RecordAsync(ActivityActions.TeachingPlanForwarded, nameof(TeachingPlan), p.Id, p.AuthorUserId,
            $"{KindText(p.Kind)} forwarded for approval: {p.Title}", null, branchId, p.OrganizationId);
        var author = await NameAsync(p.AuthorUserId);
        await NotifyAsync(await TeachingPlans.ApproversAsync(Db, p, SeesStaffAsync), p, NotificationEventKeys.StaffPlanForwarded,
            $"{author}'s {KindWord(p.Kind)} awaits approval", p.Title, "/admin/timetable?tab=plans");
        await NotifyAsync(new[] { p.AuthorUserId }, p, NotificationEventKeys.StaffPlanForwarded,
            $"Your {KindWord(p.Kind)} was forwarded for approval", p.Title, $"/plans/{p.Id}");

        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    /// <summary>
    /// Approves: stage 2 of a two-stage plan, or stage 1 of a one-stage lesson plan. The teacher, the head of department who
    /// forwarded it and the approvers (the Director of Studies) are told — a lesson plan's approvers in the weekly digest.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid branchId, Guid id, [FromBody] PlanDecisionRequest? request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        var me = CurrentUserId();
        if (DutySeparation.Refusal(me, ctx.Plan.AuthorUserId, "approve your own plan", ctx.Plan.ForwardedByUserId) is { } refused)
            return DutySeparation.Problem(refused);
        if (!ctx.Access.CanApprove) return ConflictProblem("This plan is not waiting on you to approve it");

        var p = ctx.Plan;
        var now = DateTime.UtcNow;
        p.Status = TeachingPlanStatus.Approved;
        p.ApprovedByUserId = me;
        p.ApprovedAt = now;
        p.WaitingSince = null;
        TeachingPlans.AddTrail(p, PlanTrailKind.Approved, me, Clean(request?.Note));
        Touch(p, me);

        // A revision replaces the version it supersedes only now. The old one stops being current FIRST, as its own
        // statement in the same transaction: in a single save the database may apply the two updates in either order,
        // and the one-current-plan index then refuses the pair — which is how every approval of a revision 409ed
        // until section 45 ran twice (2026-09-26).
        if (p.SupersedesId != null) p.IsCurrent = true;
        IActionResult? conflict = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            conflict = null;
            await using var tx = await Db.Database.BeginTransactionAsync();
            if (p.SupersedesId is { } previousId)
                await Db.TeachingPlans.Where(x => x.Id == previousId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsCurrent, false).SetProperty(x => x.UpdatedAt, now));
            conflict = await SaveOrConflictAsync();
            if (conflict == null) await tx.CommitAsync();
        });
        if (conflict != null) return conflict;

        await Activity.RecordAsync(ActivityActions.TeachingPlanApproved, nameof(TeachingPlan), p.Id, p.AuthorUserId,
            $"{KindText(p.Kind)} approved: {p.Title}", null, branchId, p.OrganizationId);
        await CreditIfOnTimeAsync(p, ctx.Settings);

        var title = $"{Capital(KindWord(p.Kind))} approved";
        await NotifyAsync(new[] { p.AuthorUserId }, p, NotificationEventKeys.StaffPlanApproved, $"Your {KindWord(p.Kind)} was approved", p.Title, $"/plans/{p.Id}");
        var others = new List<Guid>();
        if (p.ForwardedByUserId is { } hod && hod != me) others.Add(hod);
        // The Director of Studies hears of every SCHEME at once; of lesson plans in the Monday digest (decision L4).
        if (p.Kind == TeachingPlanKind.SchemeOfWork || p.Stages == 2)
            others.AddRange((await StaffLookups.UsersWithPermissionAsync(Db, p.OrganizationId, Permissions.TeachingPlansApprove, default, branchId))
                .Where(u => u != me && u != p.AuthorUserId));
        var author = await NameAsync(p.AuthorUserId);
        await NotifyAsync(others.Distinct(), p, NotificationEventKeys.StaffPlanApproved, $"{title}: {author}", p.Title, $"/plans/{p.Id}");

        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    /// <summary>Returns a plan to its author with a reason, from either stage. What was returned is kept in the trail.</summary>
    [HttpPost("{id:guid}/return")]
    public async Task<IActionResult> Return(Guid branchId, Guid id, [FromBody] PlanDecisionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        var me = CurrentUserId();
        if (DutySeparation.Refusal(me, ctx.Plan.AuthorUserId, "return your own plan") is { } own) return DutySeparation.Problem(own);
        if (!ctx.Access.CanReturn) return ConflictProblem("This plan is not waiting on you");
        var reason = Clean(request?.Note);
        if (reason == null || reason.Length < 10)
            return BadRequestProblem("Say what to change", "At least ten characters: a plan returned without a reason cannot be fixed.");

        var p = ctx.Plan;
        var returnedByApprover = p.Status == TeachingPlanStatus.Forwarded;
        var forwarder = p.ForwardedByUserId;
        var snapshot = p.Kind == TeachingPlanKind.LessonPlan ? p.SectionsJson : p.RowsJson;
        p.Status = TeachingPlanStatus.Returned;
        p.ReturnedByUserId = me;
        p.ReturnedAt = DateTime.UtcNow;
        p.ReturnReason = reason;
        p.WaitingSince = null;
        // A resubmitted plan starts the chain again: a change after forwarding is something the head has not seen.
        p.ForwardedByUserId = null; p.ForwardedAt = null; p.StageSkippedReason = null;
        TeachingPlans.AddTrail(p, PlanTrailKind.Returned, me, reason, request?.SectionKey, snapshot);
        Touch(p, me);
        if (await SaveOrConflictAsync() is { } conflict) return conflict;

        await Activity.RecordAsync(ActivityActions.TeachingPlanReturned, nameof(TeachingPlan), p.Id, p.AuthorUserId,
            $"{KindText(p.Kind)} returned for changes: {p.Title}", null, branchId, p.OrganizationId);
        await NotifyAsync(new[] { p.AuthorUserId }, p, NotificationEventKeys.StaffPlanReturned, $"Your {KindWord(p.Kind)} was returned for changes", p.Title, $"/plans/{p.Id}");
        if (returnedByApprover && forwarder is { } hod && hod != me)
            await NotifyAsync(new[] { hod }, p, NotificationEventKeys.StaffPlanReturned, $"A {KindWord(p.Kind)} you forwarded was returned", p.Title, $"/plans/{p.Id}");

        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    [HttpPost("{id:guid}/comment")]
    public async Task<IActionResult> Comment(Guid branchId, Guid id, [FromBody] PlanDecisionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanComment) return ConflictProblem("You cannot comment on this plan");
        var body = Clean(request?.Note);
        if (body == null) return BadRequestProblem("Write the comment");
        var p = ctx.Plan;
        var me = CurrentUserId();
        TeachingPlans.AddTrail(p, PlanTrailKind.Comment, me, body, request?.SectionKey);
        p.UpdatedAt = DateTime.UtcNow;
        if (await SaveOrConflictAsync() is { } conflict) return conflict;

        // The author hears a reviewer's comment; the people who have the plan hear the author's.
        var recipients = me == p.AuthorUserId
            ? p.Status == TeachingPlanStatus.Forwarded ? await TeachingPlans.ApproversAsync(Db, p, SeesStaffAsync) : ctx.Chain.Stage1For(p.AuthorUserId).ToList()
            : new List<Guid> { p.AuthorUserId };
        var who = await NameAsync(me);
        await NotifyAsync(recipients.Where(r => r != me), p, NotificationEventKeys.StaffPlanComment, $"{who} commented on a {KindWord(p.Kind)}", p.Title, $"/plans/{p.Id}");
        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    /// <summary>The self-evaluation, written after the lesson — the one field open once a plan is approved.</summary>
    [HttpPost("{id:guid}/reflection")]
    public async Task<IActionResult> Reflect(Guid branchId, Guid id, [FromBody] PlanDecisionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanReflect) return ConflictProblem("Only the teacher writes the self-evaluation");
        var p = ctx.Plan;
        p.Reflection = request?.Note is { } n && !string.IsNullOrWhiteSpace(n) ? Truncate(n.Trim(), 4000) : null;
        p.ReflectionAt = DateTime.UtcNow;
        TeachingPlans.AddTrail(p, PlanTrailKind.Reflection, CurrentUserId());
        p.UpdatedAt = DateTime.UtcNow;
        if (await SaveOrConflictAsync() is { } conflict) return conflict;
        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    /// <summary>A change to an approved plan is a new version; the approved one stays current until the new one is approved.</summary>
    [HttpPost("{id:guid}/revise")]
    public async Task<IActionResult> Revise(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: false);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanRevise) return ConflictProblem("Only an approved plan is revised", "A draft or a returned plan is simply edited.");
        var p = ctx.Plan;
        var open = await Db.TeachingPlans.AsNoTracking().Where(x => x.SupersedesId == p.Id && !x.IsCurrent && x.Status != TeachingPlanStatus.Withdrawn).Select(x => x.Id).FirstOrDefaultAsync();
        if (open != Guid.Empty) return await ReturnExistingAsync(branchId, open);

        var me = CurrentUserId();
        var revision = new TeachingPlan
        {
            OrganizationId = p.OrganizationId, BranchId = p.BranchId, Kind = p.Kind, AuthorUserId = p.AuthorUserId, SubjectId = p.SubjectId,
            ClassNames = p.ClassNames, ClassKey = p.ClassKey, PeriodKey = p.PeriodKey, LessonDate = p.LessonDate, DutyId = p.DutyId,
            TimetableLessonId = p.TimetableLessonId, SchemeId = p.SchemeId, SchemeRowKey = p.SchemeRowKey, Title = p.Title,
            SectionsJson = p.SectionsJson, RowsJson = p.RowsJson, Stages = TeachingPlans.StagesFor(p.Kind, ctx.Settings),
            Version = p.Version + 1, SupersedesId = p.Id, IsCurrent = false, CreatedBy = me
        };
        TeachingPlans.AddTrail(revision, PlanTrailKind.Revised, me, $"Revision of version {p.Version}");
        Db.TeachingPlans.Add(revision);
        try { await Db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            Db.ChangeTracker.Clear();
            var winner = await Db.TeachingPlans.AsNoTracking().Where(x => x.SupersedesId == p.Id && !x.IsCurrent && x.Status != TeachingPlanStatus.Withdrawn).Select(x => x.Id).FirstOrDefaultAsync();
            if (winner == Guid.Empty) throw;
            return await ReturnExistingAsync(branchId, winner);
        }
        await Activity.RecordAsync(ActivityActions.TeachingPlanRevised, nameof(TeachingPlan), revision.Id, p.AuthorUserId,
            $"{KindText(p.Kind)} revision started: {p.Title}", null, branchId, p.OrganizationId);
        var created = await LoadAsync(branchId, revision.Id, track: false);
        return CreatedAtAction(nameof(Get), new { branchId, id = revision.Id }, await MapAsync(created!));
    }

    /// <summary>Parallel streams: the same plan also covers these classes. One plan, several class names.</summary>
    [HttpPost("{id:guid}/copy")]
    public async Task<IActionResult> AlsoFor(Guid branchId, Guid id, [FromBody] CopyTeachingPlanRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanEdit) return ConflictProblem("Add classes while the plan is a draft or returned");
        var p = ctx.Plan;
        var classes = p.ClassNames.Concat(request.AlsoClassNames ?? new()).Select(c => ClassName.Display(c)).Where(c => c.Length > 0)
            .GroupBy(ClassName.Key).Select(g => g.First()).ToList();
        var problem = await ClassesProblemAsync(p, classes);
        if (problem != null) return problem;
        p.ClassNames = classes.ToArray();
        p.ClassKey = TeachingPlanRules.ClassKey(classes);
        var content = TeachingPlans.ContentOf(p);
        if (p.Kind == TeachingPlanKind.LessonPlan)
        {
            content.Header = content.Header with { ClassText = string.Join(", ", classes), Learners = await LearnersAsync(p.BranchId, classes) };
            p.SectionsJson = TeachingPlans.Serialize(content);
        }
        Touch(p, CurrentUserId());
        if (await SaveOrConflictAsync() is { } conflict) return conflict;
        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    /// <summary>Moves a lesson plan to another of the teacher's lessons — a missed lesson's plan to its recovery, or the next lesson.</summary>
    [HttpPost("{id:guid}/bump")]
    public async Task<IActionResult> Bump(Guid branchId, Guid id, [FromBody] BumpTeachingPlanRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead || !ctx.Access.IsAuthor) return PlanNotFound();
        var p = ctx.Plan;
        if (p.Kind != TeachingPlanKind.LessonPlan || p.Status == TeachingPlanStatus.Withdrawn) return ConflictProblem("Only a lesson plan moves to another lesson");
        var me = CurrentUserId();
        var target = await Db.StaffDuties.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.ToDutyId && d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.IsActive
                                                                                 && d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me));
        if (target == null) return NotFoundProblem("Lesson not found");
        if (target.SubjectId != p.SubjectId) return BadRequestProblem("That lesson is in another subject");
        var taken = await Db.TeachingPlans.AnyAsync(x => x.Id != p.Id && x.AuthorUserId == me && x.DutyId == target.Id && x.IsCurrent && x.Status != TeachingPlanStatus.Withdrawn);
        if (taken) return ConflictProblem("That lesson already has a plan", "Open it instead, or discard it first.");
        var zone = await ZoneAsync(branchId);
        var from = p.LessonDate;
        p.DutyId = target.Id;
        p.TimetableLessonId = target.TimetableLessonId;
        p.LessonDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(target.StartsAt, zone));
        var content = TeachingPlans.ContentOf(p);
        var local = TimeZoneInfo.ConvertTimeFromUtc(target.StartsAt, zone);
        content.Header = content.Header with { Time = string.Create(CultureInfo.InvariantCulture, $"{local:HH:mm}–{TimeZoneInfo.ConvertTimeFromUtc(target.EndsAt, zone):HH:mm}"), Room = target.Room ?? content.Header.Room };
        p.SectionsJson = TeachingPlans.Serialize(content);
        var subjectName = (await TeachingPlans.ChainAsync(Db, p.OrganizationId, p.SubjectId)).SubjectName;
        p.Title = Truncate(string.Create(CultureInfo.InvariantCulture, $"{subjectName} · {string.Join(", ", p.ClassNames)} · {p.LessonDate:ddd dd MMM yyyy}"), 200);
        TeachingPlans.AddTrail(p, PlanTrailKind.Comment, me, string.Create(CultureInfo.InvariantCulture, $"Moved from {from:dd MMM yyyy} to {p.LessonDate:dd MMM yyyy}."));
        Touch(p, me);
        if (await SaveOrConflictAsync() is { } conflict) return conflict;
        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// Automatic credit (plan §5.8, decision L10): behind the school's existing automatic-credit switch, off by default.
    /// On time means FIRST submitted by the deadline — a lesson plan the day before its lesson at the school's hour, a
    /// scheme by the end of the school's due week of the term. A resubmission after a return keeps the first date, so a
    /// return does not cost the credit; a plan that was never on time earns nothing however it ends.
    /// </summary>
    private async Task CreditIfOnTimeAsync(TeachingPlan p, TeachingPlanSettingsDto settings)
    {
        if (p.Version > 1) return; // a revision is not a new plan
        var firstSubmitted = TeachingPlans.TrailOf(p).Where(t => t.Kind == PlanTrailKind.Submitted).Select(t => (DateTime?)t.At).Min() ?? p.SubmittedAt;
        if (firstSubmitted is not { } submitted) return;
        var zone = await ZoneAsync(p.BranchId);
        DateTime deadlineUtc;
        if (p.Kind == TeachingPlanKind.LessonPlan)
        {
            if (p.LessonDate is not { } d) return;
            deadlineUtc = TimeZoneInfo.ConvertTimeToUtc(d.AddDays(-1).ToDateTime(new TimeOnly(settings.DeadlineHourDayBefore, 0)), zone);
        }
        else
        {
            var policy = await _policy.GetAsync(p.OrganizationId);
            if (_policy.FindPeriod(policy, p.PeriodKey) is not { } period) return;
            deadlineUtc = TimeZoneInfo.ConvertTimeToUtc(period.Start.AddDays(7 * settings.SchemeDueWeek).ToDateTime(TimeOnly.MinValue), zone);
        }
        if (submitted > deadlineUtc) return;
        await _awards.CreditAsync(p.OrganizationId, p.BranchId, p.AuthorUserId, StaffSystemAwards.PlanApprovedOnTime,
            $"{KindText(p.Kind)} approved, submitted on time: {p.Title}");
    }

    internal sealed record PlanContext(TeachingPlan Plan, TeachingPlans.Chain Chain, TeachingPlanSettingsDto Settings, TeachingPlans.Access Access);

    private async Task<PlanContext?> LoadAsync(Guid branchId, Guid id, bool track)
    {
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var query = Db.TeachingPlans.Where(p => p.Id == id && p.BranchId == branchId && p.OrganizationId == organizationId);
        var plan = track ? await query.FirstOrDefaultAsync() : await query.AsNoTracking().FirstOrDefaultAsync();
        if (plan == null) return null;
        var policy = await _policy.GetAsync(organizationId);
        var settings = _policy.PlanSettings(policy);
        var chain = await TeachingPlans.ChainAsync(Db, organizationId, plan.SubjectId);
        var access = await AccessAsync(plan, chain, settings);
        return new PlanContext(plan, chain, settings, access);
    }

    private Task<TeachingPlans.Access> AccessAsync(TeachingPlan plan, TeachingPlans.Chain chain, TeachingPlanSettingsDto settings)
        => TeachingPlans.AccessForAsync(CurrentUserId(), plan, chain, settings, HasPermissionAsync,
            () => StaffScope.CanSeeStaffAsync(plan.BranchId, plan.AuthorUserId),
            async () => chain.DepartmentId is { } dept && await Db.Users.AsNoTracking().AnyAsync(u => u.Id == CurrentUserId() && u.DepartmentIds.Contains(dept)));

    /// <summary>Whether <paramref name="viewer"/> can see <paramref name="subject"/> in their staff scope — for the approver list.</summary>
    private async Task<bool> SeesStaffAsync(Guid viewer, Guid subject)
    {
        // Null means an unscoped viewer: everybody.
        var visible = await StaffScopeService.VisibleUserIdsForAsync(Db, viewer);
        return visible == null || visible.Contains(subject);
    }

    private async Task<IActionResult> ReturnExistingAsync(Guid branchId, Guid id)
    {
        var ctx = await LoadAsync(branchId, id, track: false);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        return Ok(await MapAsync(ctx));
    }

    private async Task<IActionResult?> ClassesProblemAsync(TeachingPlan p, List<string> classes)
    {
        if (classes.Count == 0) return BadRequestProblem("Choose at least one class");
        if (classes.Count > 8) return BadRequestProblem("A plan covers at most eight classes");
        var taught = await TeachingPlans.TaughtClassesAsync(Db, p.OrganizationId, p.BranchId, p.AuthorUserId, p.SubjectId);
        // The lesson's own class stays allowed even without an assignment (a cover lesson).
        if (p.DutyId != null) taught.AddRange(p.ClassNames);
        return TeachingPlans.Covers(taught, classes) ? null
            : BadRequestProblem("You do not teach one of those classes", "A plan covers only classes you are assigned to teach in this subject.");
    }

    private async Task<IActionResult?> SaveOrConflictAsync()
    {
        try { await Db.SaveChangesAsync(); return null; }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictProblem("Somebody else changed this plan a moment ago", "Reload it and try again.");
        }
        catch (DbUpdateException)
        {
            return ConflictProblem("That would make a second plan for the same lesson or scheme", "Open the existing one instead.");
        }
    }

    private static void Touch(TeachingPlan p, Guid me) { p.UpdatedAt = DateTime.UtcNow; p.UpdatedBy = me == Guid.Empty ? null : me; }

    private IActionResult PlanNotFound() => NotFoundProblem("Plan not found");

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : Truncate(s.Trim(), 2000);

    internal static string KindText(TeachingPlanKind k) => k == TeachingPlanKind.SchemeOfWork ? "Scheme of work" : "Lesson plan";
    internal static string KindWord(TeachingPlanKind k) => k == TeachingPlanKind.SchemeOfWork ? "scheme of work" : "lesson plan";
    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => AppointmentScheduling.ResolveTimeZone(await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

    private async Task<string> NameAsync(Guid userId)
    {
        var names = await StaffLookups.LoadNamesAsync(Db, new Guid?[] { userId });
        return names[userId] is { Length: > 0 } n ? n : "A colleague";
    }

    private async Task<int?> LearnersAsync(Guid branchId, IEnumerable<string> classes)
    {
        var keys = classes.Select(ClassName.Key).ToHashSet();
        var names = await Db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.BranchId == branchId && s.IsActive && s.ClassName != null)
            .Select(s => s.ClassName!).ToListAsync();
        var count = names.Count(n => keys.Contains(ClassName.Key(n)));
        return count == 0 ? null : count;
    }

    /// <summary>One notification per recipient, naming the plan and never its content. Never fails the request.</summary>
    private async Task NotifyAsync(IEnumerable<Guid> recipients, TeachingPlan p, string eventKey, string title, string message, string url)
    {
        var list = recipients.Where(r => r != Guid.Empty).Distinct().ToList();
        if (list.Count == 0) return;
        try
        {
            await _notifications.NotifyManyAsync(list, new CreateNotificationRequest
            {
                OrganizationId = p.OrganizationId,
                BranchId = p.BranchId,
                Title = title,
                Message = message,
                Type = NotificationType.StaffPerformance,
                Priority = NotificationPriority.Normal,
                Channels = NotificationChannel.InApp | NotificationChannel.Email,
                EventKey = eventKey,
                ActionUrl = url,
                IconClass = p.Kind == TeachingPlanKind.SchemeOfWork ? "journal-bookmark" : "journal-text"
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Plan notice {Key} for {PlanId} failed", eventKey, p.Id); }
    }
}
