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
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Lessons in the day (duty rota plan §7): My Day, the lesson list, Taught / Not taught, supervisors' flags and
/// confirmations, recovery lessons and cancellations.
///
/// Who sees a lesson (plan §13.5): its teacher, and holders of <c>timetable.lessons.flag</c> or
/// <c>staff.records.view</c> whose staff scope covers the teacher — never a peer. Out of reach is 404. Who flags: the
/// teacher (a self-report: Taught, or Not taught with a reason), or a lesson supervisor — a holder of
/// <c>timetable.lessons.flag</c> in scope, never on their own lesson — who confirms, overrides, or records a missed lesson
/// as with or without permission. A supervisor's mark is never overwritten by the teacher.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff/lessons")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffLessonsController : StaffPerformanceControllerBase
{
    private const int MaxRangeDays = 62;
    private const int MaxListed = 600;

    private readonly IStaffPerformancePolicyService _policy;
    private readonly ITimetableSettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IStaffAlertService _alerts;
    private readonly ILogger<StaffLessonsController> _logger;

    public StaffLessonsController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        ITimetableSettingsService settings,
        INotificationService notifications,
        IStaffAlertService alerts,
        ILogger<StaffLessonsController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _settings = settings;
        _notifications = notifications;
        _alerts = alerts;
        _logger = logger;
    }

    // =====================================================================================================
    // My Day
    // =====================================================================================================

    /// <summary>The caller's own day (plan §7.2). No permission code: it is the caller's own lessons and duties.</summary>
    [HttpGet("my-day")]
    [ProducesResponseType(typeof(MyDayDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyDay(Guid branchId, [FromQuery] DateOnly? date = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var now = DateTime.UtcNow;
        var zone = await ZoneAsync(branchId);
        var day = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        var dayStart = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);
        var dayEnd = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var policy = await _policy.GetAsync(organizationId);

        var lessons = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me)
                        && d.StartsAt >= dayStart && d.StartsAt < dayEnd)
            .OrderBy(d => d.StartsAt).ToListAsync();
        // A lesson cancelled because the timetable was replaced is noise on the day; one cancelled with a reason is news.
        lessons = lessons.Where(l => l.IsActive || l.Description != "No longer on the published timetable.").ToList();

        var sessions = await Db.StaffDuties.AsNoTracking().Include(d => d.Parameter)
            .Where(d => d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Session && d.StartsAt < dayEnd && d.EndsAt > dayStart
                        && ((d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me)) || d.RecorderUserIds.Contains(me)))
            .OrderBy(d => d.StartsAt).Take(20).ToListAsync();
        var rota = await Db.StaffDuties.AsNoTracking().Include(d => d.Parameter)
            .Where(d => d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Rota && d.StartsAt < dayEnd && d.EndsAt > dayStart
                        && ((d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me)) || d.SupervisorUserIds.Contains(me)))
            .OrderBy(d => d.StartsAt).Take(10).ToListAsync();

        var reports = await Db.StaffDutyReports.AsNoTracking().Include(r => r.Duty)
            .Where(r => r.BranchId == branchId && r.AuthorUserId == me && r.Duty!.IsActive
                        && (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned) && r.DueAt < dayEnd && r.DueAt >= now.AddDays(-31))
            .OrderBy(r => r.DueAt).Take(10).ToListAsync();

        // Missed lessons of mine still owed a recovery, and earlier lessons still unrecorded.
        var since = now.AddDays(-Math.Max(policy.LessonRecoveryDeadlineDays, 1) - 31);
        var recentMine = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.IsActive && d.RecoversDutyId == null
                        && d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me) && d.StartsAt >= since && d.StartsAt < dayStart)
            .OrderBy(d => d.StartsAt).Take(400).ToListAsync();

        var names = await BuildNamesAsync(sessions.Concat(rota).SelectMany(d => (d.ExpectedUserIds ?? Array.Empty<Guid>()).Concat(d.RecorderUserIds).Concat(d.SupervisorUserIds)).Select(id => (Guid?)id).Append(me));
        var allItems = await StaffLessons.ToItemsAsync(Db, lessons.Concat(recentMine).ToList(), me, _ => false, policy, zone, now);
        var todayItems = allItems.Where(i => i.StartsAt >= dayStart && i.StartsAt < dayEnd).ToList();
        var earlier = allItems.Where(i => i.StartsAt < dayStart).ToList();

        var hasTimetable = await Db.Timetables.AsNoTracking().AnyAsync(t => t.BranchId == branchId && t.Status == TimetableStatus.Published && t.EffectiveFrom <= day && t.EffectiveTo >= day);
        var welfareOwed = await Db.WelfareRecords.AsNoTracking().CountAsync(w => w.BranchId == branchId && w.AssignedToUserId == me && w.Status != WelfareStatus.Resolved && w.Status != WelfareStatus.Draft);

        return Ok(new MyDayDto
        {
            Date = day,
            Lessons = todayItems,
            Sessions = sessions.Select(d => StaffPerformanceMapping.ToDto(d, names, me, false, d.ExpectedUserIds?.Length ?? 0, 0, null, null)).ToList(),
            OnDuty = rota.Select(d => StaffPerformanceMapping.ToDto(d, names, me, false, d.ExpectedUserIds?.Length ?? 0, 0, null, null)).ToList(),
            ReportsDue = reports.Select(r => StaffDutyReports.ToSummary(r, r.Duty!, names, 0, now)).ToList(),
            RecoveryOwed = earlier.Where(i => i.Status is LessonStatus.MissedWithPermission or LessonStatus.MissedWithoutPermission or LessonStatus.NotTaughtSelfReported or LessonStatus.NotRecovered)
                .OrderBy(i => i.RecoverBy ?? DateTime.MaxValue).Take(20).ToList(),
            Unrecorded = earlier.Where(i => i.Status == LessonStatus.Unrecorded && i.StartsAt >= now.AddDays(-Math.Max(policy.UnrecordedLessonWindowDays, 1) - 7))
                .OrderByDescending(i => i.StartsAt).Take(20).ToList(),
            WelfareActionsOwed = welfareOwed,
            NextLessonStartsAt = todayItems.Where(i => i.Status == LessonStatus.Scheduled).Select(i => (DateTime?)i.StartsAt).FirstOrDefault(),
            HasTimetable = hasTimetable
        });
    }

    // =====================================================================================================
    // The lesson list
    // =====================================================================================================

    [HttpGet]
    [ProducesResponseType(typeof(LessonListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLessons(Guid branchId, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] Guid? teacherUserId = null, [FromQuery] LessonStatus? status = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var now = DateTime.UtcNow;
        var start = from ?? now.Date.AddDays(-7);
        var end = to ?? start.AddDays(8);
        if (end <= start) return BadRequestProblem("The range ends before it starts.");
        if ((end - start).TotalDays > MaxRangeDays) return BadRequestProblem($"Choose at most {MaxRangeDays} days.");

        var (visible, canFlagAny, ownOnly) = await ReachAsync(branchId, me);
        var query = Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.StartsAt >= start && d.StartsAt < end && d.ExpectedUserIds != null);
        if (ownOnly) query = query.Where(d => d.ExpectedUserIds!.Contains(me));
        else if (visible != null)
        {
            var ids = visible.ToList();
            query = query.Where(d => d.ExpectedUserIds!.Any(u => ids.Contains(u)));
        }
        if (teacherUserId is { } t)
        {
            // A peer asking for a colleague's lessons, or a scoped reader asking outside scope: the person is not there.
            if (t != me && (ownOnly || (visible != null && !visible.Contains(t)))) return NotFoundProblem("Staff member not found");
            query = query.Where(d => d.ExpectedUserIds!.Contains(t));
        }
        var rows = await query.OrderBy(d => d.StartsAt).Take(MaxListed + 1).ToListAsync();
        // Replaced-timetable cancellations are bookkeeping; a lesson cancelled with a reason stays visible.
        rows = rows.Where(d => d.IsActive || d.Description != "No longer on the published timetable.").ToList();

        var policy = await _policy.GetAsync(organizationId);
        var zone = await ZoneAsync(branchId);
        var items = await StaffLessons.ToItemsAsync(Db, rows.Take(MaxListed).ToList(), me, teacher => canFlagAny && (visible == null || visible.Contains(teacher)), policy, zone, now);
        var filtered = status == null ? items : items.Where(i => i.Status == status).ToList();

        return Ok(new LessonListDto
        {
            Items = filtered,
            Total = rows.Count > MaxListed ? MaxListed : rows.Count,
            Scheduled = items.Count(i => i.Status == LessonStatus.Scheduled),
            Taught = items.Count(i => i.Status is LessonStatus.Taught or LessonStatus.TaughtSelfReported),
            Unrecorded = items.Count(i => i.Status == LessonStatus.Unrecorded),
            Missed = items.Count(i => i.Status is LessonStatus.MissedWithPermission or LessonStatus.MissedWithoutPermission or LessonStatus.NotTaughtSelfReported or LessonStatus.RecoveryScheduled),
            Recovered = items.Count(i => i.Status == LessonStatus.Recovered),
            NotRecovered = items.Count(i => i.Status == LessonStatus.NotRecovered),
            SelfReportsToConfirm = items.Count(i => i.CanFlag && i.Status is LessonStatus.TaughtSelfReported or LessonStatus.NotTaughtSelfReported),
            ScopedToDepartments = visible == null || ownOnly ? new() : (await StaffScope.GetScopedDepartmentNamesAsync()).ToList(),
            OwnOnly = ownOnly
        });
    }

    // =====================================================================================================
    // Flags
    // =====================================================================================================

    [HttpPost("{dutyId:guid}/flag")]
    [ProducesResponseType(typeof(LessonItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Flag(Guid branchId, Guid dutyId, [FromBody] FlagLessonRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var now = DateTime.UtcNow;
        var lesson = await Db.StaffDuties.AsNoTracking().Include(d => d.Parameter)
            .FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.ExpectedUserIds != null);
        if (lesson == null || lesson.ExpectedUserIds!.Length == 0) return NotFoundProblem("Lesson not found");
        var teacher = lesson.ExpectedUserIds[0];
        var isTeacher = teacher == me;
        var supervisor = !isTeacher && await IsSupervisorForAsync(branchId, teacher);
        if (!isTeacher && !supervisor) return NotFoundProblem("Lesson not found");

        if (!lesson.IsActive) return ConflictProblem("This lesson was cancelled");
        if (lesson.StartsAt > now) return BadRequestProblem("A lesson is flagged once it has started.");
        if (lesson.Parameter == null || !lesson.Parameter.IsActive) return BadRequestProblem("This lesson's parameter has been retired.");

        var organizationId = lesson.OrganizationId;
        var policy = await _policy.GetAsync(organizationId);
        if (ClosedPeriodProblem(_policy, policy, lesson.StartsAt, "flag a lesson") is { } closed) return closed;

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (isTeacher)
        {
            if (request.Outcome is not (DutyOutcome.Present or DutyOutcome.Late or DutyOutcome.Absent))
                return BadRequestProblem("Mark the lesson taught, taught late, or not taught.", "Whether a missed lesson had permission is for a lesson supervisor to record.");
            if (request.Outcome == DutyOutcome.Absent && note == null)
                return BadRequestProblem("Say why the lesson was not taught.");
            var confirmed = await Db.StaffPerformanceRecords.AsNoTracking()
                .AnyAsync(r => r.DutyId == lesson.Id && r.SubjectUserId == teacher && r.Status == StaffRecordStatus.Final && r.Source != RecordSource.SelfReport);
            if (confirmed) return ConflictProblem("A lesson supervisor has already recorded this lesson", "Ask them to change it; your own mark cannot replace theirs.");
        }
        else
        {
            if (request.Outcome is not (DutyOutcome.Present or DutyOutcome.Late or DutyOutcome.Absent or DutyOutcome.Excused))
                return BadRequestProblem("Record the lesson taught, taught late, missed with permission, or missed without permission.");
            if (request.Outcome is DutyOutcome.Absent or DutyOutcome.Excused && note == null)
                return BadRequestProblem("Give the reason the lesson was missed.");
        }

        var previous = await Db.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.DutyId == lesson.Id && r.SubjectUserId == teacher && r.Status == StaffRecordStatus.Final)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();

        var record = await StaffLessons.WriteFlagAsync(Db, lesson, lesson.Parameter, request.Outcome, note, me,
            isTeacher ? RecordSource.SelfReport : RecordSource.Register, now);

        var names = await BuildNamesAsync(new Guid?[] { teacher, me });
        var zone = await ZoneAsync(branchId);
        var overriding = previous != null && (previous.Outcome != request.Outcome || previous.Source == RecordSource.SelfReport) && !isTeacher;
        await Activity.RecordAsync(overriding ? ActivityActions.LessonFlagOverridden : ActivityActions.LessonFlagged, nameof(StaffDuty), lesson.Id, teacher,
            string.Create(CultureInfo.InvariantCulture,
                $"{lesson.Title} ({TimeZoneInfo.ConvertTimeFromUtc(lesson.StartsAt, zone):ddd dd MMM HH:mm}) {(isTeacher ? "self-reported" : overriding ? $"recorded by {names[me]}, replacing {Describe(previous!.Outcome)}{(previous.Source == RecordSource.SelfReport ? " (self-report)" : "")}:" : $"recorded by {names[me]}:")} {Describe(request.Outcome)}"),
            new { request.Outcome, Previous = previous?.Outcome, PreviousSource = previous?.Source, SelfReport = isTeacher }, branchId, organizationId);

        if (!isTeacher) await _alerts.NotifyRecordLoggedAsync(record.Id);

        var fresh = await Db.StaffDuties.AsNoTracking().FirstAsync(d => d.Id == lesson.Id);
        return Ok((await StaffLessons.ToItemsAsync(Db, new[] { fresh }, me, _ => supervisor, policy, zone, DateTime.UtcNow)).Single());
    }

    /// <summary>Confirm teachers' self-reports as they stand (plan §10 "bulk confirm self-reports"). Scoped; bounded; synchronous.</summary>
    [HttpPost("confirm")]
    [RequirePermission(Permissions.TimetableLessonsFlag)]
    [ProducesResponseType(typeof(ConfirmLessonsResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Confirm(Guid branchId, [FromBody] ConfirmLessonsRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var ids = request.DutyIds.Distinct().Take(200).ToList();
        if (ids.Count == 0) return BadRequestProblem("Choose the lessons to confirm.");
        var me = CurrentUserId();
        var now = DateTime.UtcNow;
        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);

        var lessons = await Db.StaffDuties.AsNoTracking().Include(d => d.Parameter)
            .Where(d => ids.Contains(d.Id) && d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.IsActive && d.ExpectedUserIds != null).ToListAsync();
        var confirmed = 0;
        foreach (var lesson in lessons)
        {
            var teacher = lesson.ExpectedUserIds![0];
            if (teacher == me || (visible != null && !visible.Contains(teacher)) || lesson.Parameter == null || !lesson.Parameter.IsActive) continue;
            var self = await Db.StaffPerformanceRecords.AsNoTracking()
                .Where(r => r.DutyId == lesson.Id && r.SubjectUserId == teacher && r.Status == StaffRecordStatus.Final && r.Source == RecordSource.SelfReport)
                .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
            if (self == null) continue;
            // A "not taught" self-report cannot be confirmed as it stands: with or without permission is the supervisor's call.
            if (!StaffLessons.IsTaught(self.Outcome)) continue;
            var record = await StaffLessons.WriteFlagAsync(Db, lesson, lesson.Parameter, self.Outcome, NoteFrom(self.Description, lesson.Title), me, RecordSource.Register, now);
            await Activity.RecordAsync(ActivityActions.LessonFlagged, nameof(StaffDuty), lesson.Id, teacher,
                $"{lesson.Title}: self-report confirmed ({Describe(self.Outcome)})", new { self.Outcome, Confirmed = true }, branchId, lesson.OrganizationId);
            confirmed++;
        }
        return Ok(new ConfirmLessonsResultDto { Confirmed = confirmed, Skipped = ids.Count - confirmed });
    }

    // =====================================================================================================
    // Recovery and cancellation
    // =====================================================================================================

    /// <summary>
    /// Schedule a recovery lesson for a missed one (MoES's Lesson Recovery Schedule): a Lesson duty on the Lesson Recovery
    /// parameter, naming the missed lesson. When it is flagged taught, it offsets the missed lesson in the scores.
    /// </summary>
    [HttpPost("{dutyId:guid}/recovery")]
    [ProducesResponseType(typeof(LessonItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ScheduleRecovery(Guid branchId, Guid dutyId, [FromBody] ScheduleRecoveryRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var now = DateTime.UtcNow;
        var lesson = await Db.StaffDuties.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.ExpectedUserIds != null && d.RecoversDutyId == null);
        if (lesson == null || lesson.ExpectedUserIds!.Length == 0) return NotFoundProblem("Lesson not found");
        var teacher = lesson.ExpectedUserIds[0];
        var supervisor = teacher != me && await IsSupervisorForAsync(branchId, teacher);
        if (teacher != me && !supervisor) return NotFoundProblem("Lesson not found");

        var starts = DateTime.SpecifyKind(request.StartsAt, DateTimeKind.Utc);
        var ends = DateTime.SpecifyKind(request.EndsAt, DateTimeKind.Utc);
        if (ends <= starts) return BadRequestProblem("The recovery lesson ends before it starts.");
        if ((ends - starts).TotalHours > 4) return BadRequestProblem("A recovery lesson is at most four hours.");
        if (starts < now.AddMinutes(-5)) return BadRequestProblem("Schedule the recovery lesson for a time still to come.");

        var missed = await Db.StaffPerformanceRecords.AsNoTracking()
            .AnyAsync(r => r.DutyId == lesson.Id && r.SubjectUserId == teacher && r.Status == StaffRecordStatus.Final && (r.Outcome == DutyOutcome.Absent || r.Outcome == DutyOutcome.Excused));
        if (!missed) return ConflictProblem("Only a missed lesson is recovered", "Record the lesson as not taught first.");

        var organizationId = lesson.OrganizationId;
        var policy = await _policy.GetAsync(organizationId);
        var (_, recoveryParameter) = await StaffLessons.EnsureParametersAsync(Db, _policy, organizationId, _logger);
        var room = string.IsNullOrWhiteSpace(request.Room) ? lesson.Room : request.Room.Trim();

        StaffDuty? recovery = null;
        IActionResult? conflict = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            conflict = null;
            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"lesson-recovery:{lesson.Id}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");
            if (await Db.StaffDuties.AnyAsync(d => d.RecoversDutyId == lesson.Id && d.IsActive))
            {
                conflict = ConflictProblem("A recovery lesson is already scheduled for this lesson");
                return;
            }
            recovery = new StaffDuty
            {
                OrganizationId = organizationId, BranchId = branchId, ParameterId = recoveryParameter, Kind = DutyKind.Lesson,
                Title = Truncate($"Recovery: {lesson.Title}", 200), Location = room, StartsAt = starts, EndsAt = ends,
                ExpectedUserIds = new[] { teacher }, RecorderUserIds = new[] { teacher },
                ClassName = lesson.ClassName, SubjectId = lesson.SubjectId, Room = room, RecoversDutyId = lesson.Id,
                CreatedByUserId = me, CreatedBy = me
            };
            Db.StaffDuties.Add(recovery);
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        if (conflict != null) return conflict;

        var zone = await ZoneAsync(branchId);
        await Activity.RecordAsync(ActivityActions.RecoveryScheduled, nameof(StaffDuty), recovery!.Id, teacher,
            string.Create(CultureInfo.InvariantCulture, $"Recovery for {lesson.Title} ({TimeZoneInfo.ConvertTimeFromUtc(lesson.StartsAt, zone):ddd dd MMM}) scheduled for {TimeZoneInfo.ConvertTimeFromUtc(starts, zone):ddd dd MMM HH:mm}"),
            new { MissedDutyId = lesson.Id, starts, ends, room }, branchId, organizationId);
        if (supervisor)
            await SendAsync(new CreateNotificationRequest
            {
                UserId = teacher, OrganizationId = organizationId, BranchId = branchId,
                Title = "A recovery lesson was scheduled for you",
                Message = string.Create(CultureInfo.InvariantCulture, $"{lesson.Title}, {TimeZoneInfo.ConvertTimeFromUtc(starts, zone):ddd dd MMM HH:mm}."),
                Type = NotificationType.StaffPerformance, Priority = NotificationPriority.Normal, Channels = NotificationChannel.InApp | NotificationChannel.Email,
                EventKey = NotificationEventKeys.StaffLessonChanged, ActionUrl = "/my-day", IconClass = "arrow-repeat"
            });

        var item = (await StaffLessons.ToItemsAsync(Db, new[] { recovery }, me, _ => supervisor, policy, zone, DateTime.UtcNow)).Single();
        return StatusCode(StatusCodes.Status201Created, item);
    }

    /// <summary>Cancel a lesson for a school event, with a reason; its teacher is told (plan §7.1).</summary>
    [HttpPost("{dutyId:guid}/cancel")]
    [ProducesResponseType(typeof(LessonItemDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid branchId, Guid dutyId, [FromBody] CancelLessonRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await HasPermissionAsync(Permissions.TimetableManage) && !await HasPermissionAsync(Permissions.StaffDutiesManage)) return Forbid();

        var me = CurrentUserId();
        var lesson = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.ExpectedUserIds != null);
        if (lesson == null) return NotFoundProblem("Lesson not found");
        var teacher = lesson.ExpectedUserIds![0];
        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);
        if (visible != null && !visible.Contains(teacher)) return NotFoundProblem("Lesson not found");
        if (!lesson.IsActive) return ConflictProblem("This lesson is already cancelled");
        var reason = request.Reason.Trim();
        if (reason.Length < 3) return BadRequestProblem("Say why the lesson is cancelled.");
        if (await Db.StaffPerformanceRecords.AnyAsync(r => r.DutyId == lesson.Id && r.Status == StaffRecordStatus.Final))
            return ConflictProblem("This lesson is already recorded", "A flagged lesson is history; record it as missed with permission instead.");

        lesson.IsActive = false;
        lesson.Description = Truncate($"Cancelled: {reason}", 2000);
        lesson.UpdatedAt = DateTime.UtcNow;
        lesson.UpdatedBy = me;
        await Db.SaveChangesAsync();

        var zone = await ZoneAsync(branchId);
        var at = TimeZoneInfo.ConvertTimeFromUtc(lesson.StartsAt, zone);
        await Activity.RecordAsync(ActivityActions.LessonCancelled, nameof(StaffDuty), lesson.Id, teacher,
            string.Create(CultureInfo.InvariantCulture, $"{lesson.Title} ({at:ddd dd MMM HH:mm}) cancelled: {Truncate(reason, 200)}"), new { reason }, branchId, lesson.OrganizationId);
        await SendAsync(new CreateNotificationRequest
        {
            UserId = teacher, OrganizationId = lesson.OrganizationId, BranchId = branchId,
            Title = "A lesson of yours was cancelled",
            Message = string.Create(CultureInfo.InvariantCulture, $"{lesson.Title}, {at:ddd dd MMM HH:mm}."),
            Type = NotificationType.StaffPerformance, Priority = NotificationPriority.Normal, Channels = NotificationChannel.InApp,
            EventKey = NotificationEventKeys.StaffLessonChanged, ActionUrl = "/my-day", IconClass = "x-circle"
        });

        var policy = await _policy.GetAsync(lesson.OrganizationId);
        return Ok((await StaffLessons.ToItemsAsync(Db, new[] { lesson }, me, _ => false, policy, zone, DateTime.UtcNow)).Single());
    }

    /// <summary>Materialise the next fortnight of lessons for this branch now (the nightly job does it too). Unscoped timetable masters only.</summary>
    [HttpPost("generate")]
    [RequirePermission(Permissions.TimetableManage)]
    [ProducesResponseType(typeof(StaffLessons.MaterialiseResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Generate(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await StaffScope.IsUnscopedAsync()) return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Title = "Lessons are generated for the whole school by an unscoped timetable master", Status = 403 });
        return Ok(await StaffLessons.MaterialiseAsync(Db, _settings, _policy, _notifications, _logger, branchId, DateTime.UtcNow));
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    /// <summary>What the caller reaches: the visible set (null = everyone), whether they may flag, and whether they see only their own.</summary>
    private async Task<(HashSet<Guid>? Visible, bool CanFlag, bool OwnOnly)> ReachAsync(Guid branchId, Guid me)
    {
        var canFlag = await HasPermissionAsync(Permissions.TimetableLessonsFlag);
        var canView = canFlag || await HasPermissionAsync(Permissions.StaffRecordsView);
        if (!canView) return (null, false, true);
        return (await StaffScope.GetVisibleUserIdsAsync(branchId), canFlag, false);
    }

    private async Task<bool> IsSupervisorForAsync(Guid branchId, Guid teacher)
    {
        if (!await HasPermissionAsync(Permissions.TimetableLessonsFlag)) return false;
        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);
        return visible == null || visible.Contains(teacher);
    }

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => AppointmentScheduling.ResolveTimeZone(await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

    private async Task SendAsync(CreateNotificationRequest request)
    {
        try { await _notifications.CreateInAppNotificationAsync(request); }
        catch (Exception ex) { _logger.LogError(ex, "Lesson notice to {UserId} failed", request.UserId); }
    }

    private static string Describe(DutyOutcome o) => o switch
    {
        DutyOutcome.Present => "taught",
        DutyOutcome.Late => "taught late",
        DutyOutcome.Absent => "not taught",
        DutyOutcome.Excused => "missed with permission",
        _ => o.ToString()
    };

    private static string? NoteFrom(string description, string title)
        => description.StartsWith(title + " — ", StringComparison.Ordinal) ? description[(title.Length + 3)..] : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
