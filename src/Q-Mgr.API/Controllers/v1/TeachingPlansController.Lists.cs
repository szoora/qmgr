using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Domain.Identity;
using QMgr.Filters;

namespace QMgr.API.Controllers.v1;

public partial class TeachingPlansController
{
    // ---- The planbook: my week ------------------------------------------------------------------------------

    /// <summary>
    /// The teacher's own week (plan §5.3): each timetabled lesson with its plan's state and whether one is due, the term's
    /// schemes, and the classes they may plan for. Self is always visible: no permission code.
    /// </summary>
    [HttpGet("planbook")]
    [ProducesResponseType(typeof(PlanbookDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Planbook(Guid branchId, [FromQuery] DateOnly? weekStart)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var zone = await ZoneAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var settings = _policy.PlanSettings(policy);
        var nowUtc = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var start = weekStart ?? today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue), zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(start.AddDays(7).ToDateTime(TimeOnly.MinValue), zone);
        var period = _policy.PeriodFor(policy, start);

        var lessons = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.IsActive && d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me)
                        && d.StartsAt >= startUtc && d.StartsAt < endUtc)
            .OrderBy(d => d.StartsAt).ToListAsync();
        var items = await StaffLessons.ToItemsAsync(Db, lessons, me, _ => false, policy, zone, nowUtc);
        var dutyIds = lessons.Select(l => l.Id).ToList();
        var plans = await Db.TeachingPlans.AsNoTracking()
            .Where(p => p.AuthorUserId == me && p.DutyId != null && dutyIds.Contains(p.DutyId.Value) && p.Status != TeachingPlanStatus.Withdrawn)
            .ToListAsync();
        var subjectNames = await Db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == organizationId)
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        var result = new PlanbookDto
        {
            WeekStart = start,
            PeriodKey = period.Key,
            PeriodName = period.Name,
            Week = Math.Max(1, (start.DayNumber - period.Start.DayNumber) / 7 + 1),
            Requirement = settings.Requirement,
            LessonPlanStages = settings.LessonPlanStages,
            UploadsEnabled = settings.UploadsEnabled,
            TimeZone = await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync()
        };
        foreach (var l in lessons)
        {
            // The current plan for the lesson, or its open revision when there is one.
            var plan = plans.Where(p => p.DutyId == l.Id).OrderByDescending(p => p.IsCurrent).ThenByDescending(p => p.Version).FirstOrDefault();
            var item = items.FirstOrDefault(i => i.DutyId == l.Id);
            var lessonDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(l.StartsAt, zone));
            var dueAt = TimeZoneInfo.ConvertTimeToUtc(lessonDay.AddDays(-1).ToDateTime(new TimeOnly(settings.DeadlineHourDayBefore, 0)), zone);
            var submitted = plan != null && plan.Status is TeachingPlanStatus.Submitted or TeachingPlanStatus.Forwarded or TeachingPlanStatus.Approved;
            result.Lessons.Add(new PlanbookLessonDto
            {
                DutyId = l.Id,
                StartsAt = l.StartsAt,
                EndsAt = l.EndsAt,
                ClassName = l.ClassName,
                SubjectId = l.SubjectId,
                SubjectName = l.SubjectId is { } sid && subjectNames.TryGetValue(sid, out var sn) ? sn : null,
                Room = l.Room ?? l.Location,
                Outcome = item?.Status.ToString(),
                PlanId = plan?.Id,
                PlanStatus = plan?.Status,
                PlanTitle = plan?.Title,
                DueAt = dueAt,
                Due = settings.Requirement == PlanRequirement.EveryLesson && !submitted && l.StartsAt > nowUtc && nowUtc >= dueAt.AddHours(-24)
            });
        }

        var schemes = await Db.TeachingPlans.AsNoTracking()
            .Where(p => p.BranchId == branchId && p.AuthorUserId == me && p.Kind == TeachingPlanKind.SchemeOfWork && p.PeriodKey == period.Key && p.Status != TeachingPlanStatus.Withdrawn)
            .OrderBy(p => p.Title).ToListAsync();
        result.Schemes = schemes.Select(s => Summary(s, subjectNames, null, null, false)).ToList();

        result.Teaching = (await Db.ClassTeacherAssignments.AsNoTracking()
                .Where(a => a.BranchId == branchId && a.UserId == me && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher && a.SubjectId != null)
                .Select(a => new { a.SubjectId, a.ClassName }).ToListAsync())
            .Select(a => new PlanTeachingDto { SubjectId = a.SubjectId!.Value, SubjectName = subjectNames.GetValueOrDefault(a.SubjectId.Value, "Subject"), ClassName = a.ClassName })
            .OrderBy(t => t.SubjectName).ThenBy(t => t.ClassName, NaturalOrderComparer()).ToList();
        return Ok(result);
    }

    /// <summary>My own plans, newest first — a lesson's plans, schemes, drafts and returned plans.</summary>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(List<TeachingPlanSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(Guid branchId, [FromQuery] TeachingPlanKind? kind, [FromQuery] TeachingPlanStatus? status, [FromQuery] int limit = 100)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var query = Db.TeachingPlans.AsNoTracking().Where(p => p.BranchId == branchId && p.AuthorUserId == me && p.Status != TeachingPlanStatus.Withdrawn);
        if (kind is { } k) query = query.Where(p => p.Kind == k);
        if (status is { } s) query = query.Where(p => p.Status == s);
        var rows = await query.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync();
        var subjectNames = await Db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(x => x.OrganizationId == organizationId).ToDictionaryAsync(x => x.Id, x => x.Name);
        return Ok(rows.Select(p => Summary(p, subjectNames, null, null, false)).ToList());
    }

    // ---- The review queue ------------------------------------------------------------------------------------

    /// <summary>
    /// Plans the caller may read beyond their own: stage 1 for a head of department, stage 2 for an approver, approved
    /// plans for a viewer. Every row is decided by <see cref="TeachingPlans.AccessForAsync"/>, never by a second filter.
    /// </summary>
    [HttpGet("queue")]
    [ProducesResponseType(typeof(TeachingPlanQueueDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Queue(Guid branchId, [FromQuery] TeachingPlanStatus? status, [FromQuery] TeachingPlanKind? kind,
        [FromQuery] Guid? subjectId, [FromQuery] Guid? departmentId, [FromQuery] bool awaitingMe = false,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] string? search = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var policy = await _policy.GetAsync(organizationId);
        var settings = _policy.PlanSettings(policy);
        var canReview = await HasPermissionAsync(Permissions.TeachingPlansReview);
        var canApprove = await HasPermissionAsync(Permissions.TeachingPlansApprove);
        var canView = await HasPermissionAsync(Permissions.TeachingPlansView);
        if (!canReview && !canApprove && !canView && !settings.ShareApprovedWithDepartment)
            return Ok(new TeachingPlanQueueDto());

        var query = Db.TeachingPlans.AsNoTracking()
            .Where(p => p.BranchId == branchId && p.OrganizationId == organizationId && p.AuthorUserId != me
                        && p.Status != TeachingPlanStatus.Draft && p.Status != TeachingPlanStatus.Withdrawn);
        if (status is { } st) query = query.Where(p => p.Status == st);
        if (kind is { } k) query = query.Where(p => p.Kind == k);
        if (subjectId is { } sub) query = query.Where(p => p.SubjectId == sub);
        if (departmentId is { } dept) query = query.Where(p => Db.Subjects.Any(s => s.Id == p.SubjectId && s.DepartmentId == dept));
        if (from is { } f) query = query.Where(p => (p.LessonDate ?? DateOnly.MinValue) >= f || p.Kind == TeachingPlanKind.SchemeOfWork);
        if (to is { } t) query = query.Where(p => (p.LessonDate ?? DateOnly.MinValue) <= t || p.Kind == TeachingPlanKind.SchemeOfWork);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLower(); query = query.Where(p => p.Title.ToLower().Contains(term)); }

        // Awaiting first, then newest. Capped: a queue is for acting on, and the reports carry the totals.
        var candidates = await query.OrderBy(p => p.Status == TeachingPlanStatus.Approved).ThenByDescending(p => p.WaitingSince ?? p.SubmittedAt ?? p.CreatedAt)
            .Take(600).ToListAsync();

        var chains = new Dictionary<Guid, TeachingPlans.Chain>();
        var myDepartments = await Db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.DepartmentIds).FirstOrDefaultAsync() ?? Array.Empty<Guid>();
        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);
        var result = new TeachingPlanQueueDto { CanReview = canReview, CanApprove = canApprove };
        var subjectNames = await Db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(x => x.OrganizationId == organizationId).ToDictionaryAsync(x => x.Id, x => x.Name);
        var readable = new List<(TeachingPlan Plan, TeachingPlans.Chain Chain, bool AwaitsMe)>();
        foreach (var p in candidates)
        {
            if (!chains.TryGetValue(p.SubjectId, out var chain)) chains[p.SubjectId] = chain = await TeachingPlans.ChainAsync(Db, organizationId, p.SubjectId);
            var access = await TeachingPlans.AccessForAsync(me, p, chain, settings, HasPermissionAsync,
                () => Task.FromResult(visible == null || visible.Contains(p.AuthorUserId)),
                () => Task.FromResult(chain.DepartmentId is { } d && myDepartments.Contains(d)));
            if (!access.CanRead) continue;
            var awaits = access.CanForward || access.CanApprove;
            if (awaitingMe && !awaits) continue;
            readable.Add((p, chain, awaits));
        }

        var names = await StaffLookups.LoadNamesAsync(Db, readable.Select(r => (Guid?)r.Plan.AuthorUserId));
        result.Total = readable.Count;
        result.AwaitingMe = readable.Count(r => r.AwaitsMe);
        result.Items = readable.Take(300).Select(r => Summary(r.Plan, subjectNames, r.Chain, names, r.AwaitsMe)).ToList();
        return Ok(result);
    }

    private static TeachingPlanSummaryDto Summary(TeachingPlan p, IReadOnlyDictionary<Guid, string> subjects, TeachingPlans.Chain? chain,
        StaffPerformanceMapping.NameLookup? names, bool awaitsMe) => new()
    {
        Id = p.Id,
        Kind = p.Kind,
        Title = p.Title,
        AuthorUserId = p.AuthorUserId,
        AuthorName = names?[p.AuthorUserId] ?? string.Empty,

        SubjectId = p.SubjectId,
        SubjectName = subjects.GetValueOrDefault(p.SubjectId, "Subject"),
        DepartmentName = chain?.DepartmentName,
        ClassNames = p.ClassNames.ToList(),
        PeriodKey = p.PeriodKey,
        LessonDate = p.LessonDate,
        DutyId = p.DutyId,
        Status = p.Status,
        Stages = p.Stages,
        SubmittedAt = p.SubmittedAt,
        ApprovedAt = p.ApprovedAt,
        HasFile = p.FileUrl != null,
        Version = p.Version,
        WaitingOn = p.Status switch
        {
            TeachingPlanStatus.Submitted => chain?.DepartmentName is { } d ? $"Head of {d}" : "Head of department",
            TeachingPlanStatus.Forwarded => "Approver",
            _ => null
        },
        AwaitsMe = awaitsMe
    };

    private static IComparer<string> NaturalOrderComparer() => Comparer<string>.Create((a, b) => QMgr.Application.NaturalOrder.Compare(a, b));

    // ---- Records of work --------------------------------------------------------------------------------------

    /// <summary>
    /// Records of work, DERIVED, never typed (plan §5.3): the scheme's lines against the lessons the lesson flags say were
    /// taught. A lesson counts against the line its plan names, else the line of the week it fell in.
    /// </summary>
    [HttpGet("{id:guid}/record-of-work")]
    [ProducesResponseType(typeof(RecordOfWorkDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordOfWork(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: false);
        if (ctx == null || !ctx.Access.CanRead || ctx.Plan.Kind != TeachingPlanKind.SchemeOfWork) return PlanNotFound();
        var scheme = ctx.Plan;
        var policy = await _policy.GetAsync(scheme.OrganizationId);
        var period = _policy.FindPeriod(policy, scheme.PeriodKey);
        if (period == null) return PlanNotFound();
        var zone = await ZoneAsync(branchId);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(period.Start.ToDateTime(TimeOnly.MinValue), zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(period.End.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var keys = scheme.ClassNames.Select(ClassName.Key).ToHashSet();

        var lessons = (await Db.StaffDuties.AsNoTracking()
                .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.SubjectId == scheme.SubjectId && d.ExpectedUserIds != null
                            && d.ExpectedUserIds.Contains(scheme.AuthorUserId) && d.StartsAt >= startUtc && d.StartsAt < endUtc)
                .ToListAsync())
            .Where(d => d.ClassName != null && keys.Contains(ClassName.Key(d.ClassName))).ToList();
        var items = await StaffLessons.ToItemsAsync(Db, lessons, CurrentUserId(), _ => false, policy, zone, DateTime.UtcNow);
        var ids = lessons.Select(l => l.Id).ToList();
        var planRows = await Db.TeachingPlans.AsNoTracking()
            .Where(p => p.DutyId != null && ids.Contains(p.DutyId.Value) && p.SchemeId == scheme.Id && p.SchemeRowKey != null && p.Status != TeachingPlanStatus.Withdrawn)
            .Select(p => new { DutyId = p.DutyId!.Value, p.SchemeRowKey }).ToListAsync();

        var rows = TeachingPlans.RowsOf(scheme);
        var result = rows.Select(r => new RecordOfWorkRowDto
        {
            RowKey = r.Key, Week = r.Week,
            Topic = r.Cells.GetValueOrDefault(TeachingPlanDefaults.TopicKey), SubTopic = r.Cells.GetValueOrDefault(TeachingPlanDefaults.SubTopicKey),
            PeriodsPlanned = r.Periods, Remarks = r.Cells.GetValueOrDefault("remarks")
        }).ToList();

        foreach (var l in lessons)
        {
            var status = items.FirstOrDefault(i => i.DutyId == l.Id)?.Status;
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(l.StartsAt, zone));
            var week = Math.Max(1, (day.DayNumber - period.Start.DayNumber) / 7 + 1);
            var rowKey = planRows.FirstOrDefault(p => p.DutyId == l.Id)?.SchemeRowKey;
            var row = (rowKey != null ? result.FirstOrDefault(r => r.RowKey == rowKey) : null)
                      ?? result.Where(r => r.Week <= week).OrderByDescending(r => r.Week).FirstOrDefault();
            if (row == null) continue;
            if (status is LessonStatus.Taught or LessonStatus.TaughtSelfReported or LessonStatus.Recovered) { row.LessonsTaught++; row.TaughtOn.Add(day); }
            else if (status is LessonStatus.MissedWithPermission or LessonStatus.MissedWithoutPermission or LessonStatus.NotRecovered or LessonStatus.RecoveryScheduled or LessonStatus.NotTaughtSelfReported) row.LessonsMissed++;
        }

        var names = await StaffLookups.LoadNamesAsync(Db, new Guid?[] { scheme.AuthorUserId });
        return Ok(new RecordOfWorkDto
        {
            SchemeId = scheme.Id, Title = scheme.Title, SubjectName = ctx.Chain.SubjectName, ClassNames = scheme.ClassNames.ToList(),
            PeriodName = period.Name, AuthorName = names[scheme.AuthorUserId], Rows = result
        });
    }

    // ---- Reports ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Plan coverage per department and subject, schemes in and approved, and review quality — how long a decision took
    /// and how many carried a comment, which is the gap the Ugandan evidence found (plan §2). One builder: the page and
    /// the print both read this.
    /// </summary>
    [HttpGet("reports")]
    [ProducesResponseType(typeof(TeachingPlanReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reports(Guid branchId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await HasPermissionAsync(Permissions.TeachingPlansView) && !await HasPermissionAsync(Permissions.TeachingPlansApprove))
            return NotFoundProblem("Report not found");
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var zone = await ZoneAsync(branchId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var end = to ?? today;
        var start = from ?? end.AddDays(-27);
        if (end.DayNumber - start.DayNumber > 120) start = end.AddDays(-120);
        var policy = await _policy.GetAsync(organizationId);
        var report = await TeachingPlanReportBuilder.BuildAsync(Db, organizationId, branchId, start, end, zone, policy, _policy, await StaffScope.GetVisibleUserIdsAsync(branchId));
        return Ok(report);
    }
}
