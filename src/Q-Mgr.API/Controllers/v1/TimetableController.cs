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
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The timetable builder (duty rota plan §6): the bell schedule and declared unavailability, versions per term,
/// placing lessons with a live diagnosis, and publishing.
///
/// Who reads what: a PUBLISHED or ARCHIVED timetable is operational and readable by every member of the branch
/// (plan §13.5 — who teaches what, where, when). A DRAFT is work in progress and exists only for somebody who may
/// write one (404 to anyone else).
///
/// WHO WRITES IT CHANGED ON 2026-09-22, and the change is why most of the endpoints below carry no
/// <c>[RequirePermission]</c> attribute. The rule is <c>TimetableAccess.MayWrite</c> — a named manager of THAT
/// VERSION, or a holder of <c>timetable.manage</c> — because an attribute refuses before the handler runs and so
/// cannot let an appointed timetable master who holds no permission through. <b>An endpoint added here that keeps
/// the attribute silently excludes the very people the feature exists for.</b> A write by a permission holder who
/// is not named is allowed, recorded as an override, and told to the named managers; see
/// <see cref="Timetable.ManagerUserIds"/> for why the override stays rather than being refused.
///
/// A write still needs an UNSCOPED caller, unchanged: publishing materialises lessons for the whole school in a
/// background job, which cannot be row-scoped downstream (the standing rule).
///
/// Every edit of a timetable runs under <c>pg_advisory_xact_lock</c> on it, and a teacher double-booking is also a
/// unique index — two masters placing at once cannot both win. Publishing locks the branch's timetables.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/timetable")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class TimetableController : StaffPerformanceControllerBase
{
    private readonly ITimetableSettingsService _settings;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly INotificationService _notifications;
    private readonly ILogger<TimetableController> _logger;

    public TimetableController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        ITimetableSettingsService settings,
        IStaffPerformancePolicyService policy,
        INotificationService notifications,
        ILogger<TimetableController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _settings = settings;
        _policy = policy;
        _notifications = notifications;
        _logger = logger;
    }

    // =====================================================================================================
    // Settings
    // =====================================================================================================

    /// <summary>The bell schedule, cycle and unavailability. No permission code: every timetable view needs the periods.</summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(TimetableSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var settings = await _settings.ReadAsync(branchId);
        if (!await HasPermissionAsync(Permissions.TimetableManage))
        {
            // Who declared themselves unavailable, and why, is for the timetable master.
            settings.Unavailability = new();
        }
        else if (settings.Unavailability.Count > 0)
        {
            var names = await BuildNamesAsync(settings.Unavailability.Select(u => (Guid?)u.UserId));
            foreach (var u in settings.Unavailability) u.UserName = names.Optional(u.UserId);
        }
        return Ok(settings);
    }

    [HttpPut("settings")]
    [RequirePermission(Permissions.TimetableManage)]
    [ProducesResponseType(typeof(TimetableSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettings(Guid branchId, [FromBody] TimetableSettingsDto request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (await RefuseScopedAsync() is { } refused) return refused;

        var error = TimetableCycle.Validate(request);
        if (error != null) return BadRequestProblem(error);

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var userIds = request.Unavailability.Select(u => u.UserId).Distinct().ToList();
        if (userIds.Count > 0)
        {
            var known = await StaffLookups.BranchStaff(Db, organizationId, branchId).Where(u => userIds.Contains(u.Id)).Select(u => u.Id).ToListAsync();
            if (known.Count != userIds.Count) return BadRequestProblem("An unavailability line names somebody who is not on this branch's staff.");
        }
        foreach (var u in request.Unavailability) u.UserName = null;

        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await Db.Database.BeginTransactionAsync();
            await _settings.WriteAsync(branchId, request);
            await tx.CommitAsync();
        });

        await Activity.RecordAsync(ActivityActions.TimetableSettingsSaved, "Branch", branchId, null,
            $"Bell schedule saved: {request.DayTypes.Count} day type(s), {TimetableCycle.CycleDayCount(request)}-day cycle, {request.Unavailability.Count} unavailability line(s)",
            new { request.CycleWeeks, DayTypes = request.DayTypes.Select(d => new { d.Name, Periods = d.Periods.Count }) }, branchId, organizationId);

        return await GetSettings(branchId);
    }

    // =====================================================================================================
    // Rooms (plan §6.1). Stored with the branch lists, written only here.
    // =====================================================================================================

    [HttpGet("rooms")]
    [ProducesResponseType(typeof(List<TimetableRoomDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRooms(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        return Ok(await RoomsAsync(branchId));
    }

    [HttpPut("rooms")]
    [RequirePermission(Permissions.TimetableManage)]
    [ProducesResponseType(typeof(List<TimetableRoomDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateRooms(Guid branchId, [FromBody] UpdateTimetableRoomsRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (await RefuseScopedAsync() is { } refused) return refused;

        var rooms = request.Rooms ?? new();
        foreach (var r in rooms)
        {
            r.Name = (r.Name ?? string.Empty).Trim();
            r.RoomType = string.IsNullOrWhiteSpace(r.RoomType) ? null : r.RoomType.Trim();
        }
        if (rooms.Count > 500) return BadRequestProblem("A branch can list at most 500 rooms.");
        if (rooms.Any(r => r.Name.Length == 0)) return BadRequestProblem("Every room needs a name.");
        if (rooms.FirstOrDefault(r => r.Name.Length > 100) is { } longName) return BadRequestProblem($"'{longName.Name[..40]}…' is longer than 100 characters.");
        if (rooms.FirstOrDefault(r => r.RoomType?.Length > 40) is { } longType) return BadRequestProblem($"The type of '{longType.Name}' is longer than 40 characters.");
        if (rooms.FirstOrDefault(r => r.Capacity is < 0 or > 5000) is { } badSeats) return BadRequestProblem($"The seats of '{badSeats.Name}' must be between 0 and 5,000.");
        if (rooms.GroupBy(r => TimetableCycle.Normalize(r.Name)).FirstOrDefault(g => g.Count() > 1) is { } dup)
            return BadRequestProblem($"'{dup.First().Name}' is listed twice.");

        var renames = (request.Renames ?? new())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value) && TimetableCycle.Normalize(kv.Key) != TimetableCycle.Normalize(kv.Value))
            .ToDictionary(kv => TimetableCycle.Normalize(kv.Key), kv => kv.Value.Trim());
        var newNames = rooms.ToDictionary(r => TimetableCycle.Normalize(r.Name), r => r.Name);
        if (renames.Values.FirstOrDefault(v => !newNames.ContainsKey(TimetableCycle.Normalize(v))) is { } lostTarget)
            return BadRequestProblem($"'{lostTarget}' is renamed to but not in the list.");

        IActionResult? result = null;
        int lessonsMoved = 0, dutiesMoved = 0;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            lessonsMoved = dutiesMoved = 0;
            await using var tx = await Db.Database.BeginTransactionAsync();
            await BranchSettingsLock.AcquireAsync(Db, branchId);
            var branch = await Db.Branches.FirstAsync(b => b.Id == branchId);

            // A rename moves what names the room: draft and published lessons (an archived version keeps the name it was
            // taught under), and lessons on the roster that have not started yet.
            var now = DateTime.UtcNow;
            foreach (var (oldNorm, newName) in renames)
            {
                var lessons = await Db.TimetableLessons
                    .Where(l => l.RoomNormalized == oldNorm && l.Timetable.BranchId == branchId && l.Timetable.Status != TimetableStatus.Archived)
                    .ToListAsync();
                foreach (var l in lessons) { l.Room = newName; l.RoomNormalized = TimetableCycle.Normalize(newName); l.UpdatedAt = now; }
                lessonsMoved += lessons.Count;

                var duties = await Db.StaffDuties
                    .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.StartsAt > now && d.Room != null && d.Room.ToLower() == oldNorm)
                    .ToListAsync();
                foreach (var d in duties) { d.Room = newName; d.UpdatedAt = now; }
                dutiesMoved += duties.Count;
            }
            if (lessonsMoved + dutiesMoved > 0) await Db.SaveChangesAsync();

            // After renames, a room still named by a live lesson cannot leave the list: retiring it keeps the name readable.
            var stranded = await Db.TimetableLessons
                .Where(l => l.RoomNormalized != null && l.Timetable.BranchId == branchId && l.Timetable.Status != TimetableStatus.Archived)
                .GroupBy(l => new { l.RoomNormalized, l.Timetable.Name })
                .Select(g => new { g.Key.RoomNormalized, g.Key.Name, Count = g.Count(), Room = g.Max(l => l.Room) })
                .ToListAsync();
            var missing = stranded.FirstOrDefault(x => !newNames.ContainsKey(x.RoomNormalized!));
            if (missing != null)
            {
                result = BadRequestProblem($"'{missing.Room}' has {missing.Count} lesson{(missing.Count == 1 ? "" : "s")} in '{missing.Name}'. Retire the room instead of removing it, or move those lessons first.");
                return;
            }

            var vocab = StudentsController.ReadVocabularies(branch.Settings);
            vocab.Rooms = rooms.Select((r, i) => new VocabularyItemDto { Name = r.Name, Capacity = r.Capacity, RoomType = r.RoomType, IsActive = r.IsActive, SortOrder = i }).ToList();
            branch.Settings = StudentsController.WriteVocabularies(branch.Settings, vocab);
            branch.UpdatedAt = now;
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        if (result != null) return result;

        await Activity.RecordAsync(ActivityActions.TimetableRoomsSaved, "Branch", branchId, null,
            $"Rooms saved: {rooms.Count(r => r.IsActive)} in use, {rooms.Count(r => !r.IsActive)} retired{(renames.Count > 0 ? $", {renames.Count} renamed ({lessonsMoved} timetable lesson(s) and {dutiesMoved} upcoming lesson(s) moved)" : "")}",
            new { Rooms = rooms.Count, Renames = renames.Count, lessonsMoved, dutiesMoved }, branchId, organizationId);
        return Ok(await RoomsAsync(branchId));
    }

    private async Task<List<TimetableRoomDto>> RoomsAsync(Guid branchId)
    {
        var json = await Db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var counts = await Db.TimetableLessons.AsNoTracking()
            .Where(l => l.RoomNormalized != null && l.Timetable.BranchId == branchId && l.Timetable.Status != TimetableStatus.Archived)
            .GroupBy(l => l.RoomNormalized!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        return StudentsController.ReadVocabularies(json).Rooms
            .OrderBy(r => r.SortOrder)
            .Select(r => new TimetableRoomDto
            {
                Name = r.Name, Capacity = r.Capacity, RoomType = r.RoomType, IsActive = r.IsActive, SortOrder = r.SortOrder,
                LessonCount = counts.GetValueOrDefault(TimetableCycle.Normalize(r.Name))
            })
            .ToList();
    }

    // =====================================================================================================
    // Versions
    // =====================================================================================================

    [HttpGet("timetables")]
    [ProducesResponseType(typeof(List<TimetableDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTimetables(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.TimetableManage);
        var query = Db.Timetables.AsNoTracking().Where(t => t.BranchId == branchId);
        // A DRAFT belongs to whoever may write it, which since 2026-09-22 includes an appointed manager holding
        // no permission at all. Filtering on the permission alone would hide a master's own draft from them.
        if (!holds) query = query.Where(t => t.Status != TimetableStatus.Draft || t.ManagerUserIds.Contains(me));
        var rows = await query
            .OrderByDescending(t => t.Status == TimetableStatus.Published).ThenByDescending(t => t.EffectiveFrom).ThenByDescending(t => t.CreatedAt)
            .Select(t => new { Timetable = t, Count = t.Lessons.Count })
            .ToListAsync();

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var names = await BuildNamesAsync(rows.SelectMany(r => r.Timetable.ManagerUserIds.Select(x => (Guid?)x).Append(r.Timetable.PublishedByUserId)));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, await ZoneAsync(branchId)));
        return Ok(rows.Select(r => ToDto(r.Timetable, r.Count, policy, names, me, holds, today)).ToList());
    }

    /// <summary>The published version in force on a branch-local date (default today), or 204.</summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(TimetableDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> GetCurrent(Guid branchId, [FromQuery] DateOnly? date = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var day = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, await ZoneAsync(branchId)));
        var id = await Db.Timetables.AsNoTracking()
            .Where(t => t.BranchId == branchId && t.Status == TimetableStatus.Published && t.EffectiveFrom <= day && t.EffectiveTo >= day)
            .OrderByDescending(t => t.PublishedAt).Select(t => (Guid?)t.Id).FirstOrDefaultAsync();
        return id == null ? NoContent() : await GetTimetable(branchId, id.Value);
    }

    [HttpGet("timetables/{id:guid}")]
    [ProducesResponseType(typeof(TimetableDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTimetable(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.TimetableManage);
        var timetable = await Db.Timetables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
        // A draft is visible to whoever may write it — its appointed managers included, permission or not.
        if (timetable == null || (timetable.Status == TimetableStatus.Draft && !TimetableAccess.MayWrite(timetable, me, holds)))
            return NotFoundProblem("Timetable not found");

        return Ok(await BuildDetailAsync(timetable, TimetableAccess.MayWrite(timetable, me, holds)));
    }

    [HttpPost("timetables")]
    [RequirePermission(Permissions.TimetableManage)]
    [ProducesResponseType(typeof(TimetableDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateTimetable(Guid branchId, [FromBody] CreateTimetableRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (await RefuseScopedAsync() is { } refused) return refused;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var zone = await ZoneAsync(branchId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));

        var period = string.IsNullOrWhiteSpace(request.PeriodKey) ? _policy.PeriodFor(policy, today) : _policy.FindPeriod(policy, request.PeriodKey);
        if (period == null || period.Key.Length > 20 || !period.Key.Contains('-')) return BadRequestProblem("Choose a term the scoring policy defines.");

        var from = request.EffectiveFrom ?? period.Start;
        var to = request.EffectiveTo ?? period.End;
        if (to < from) return BadRequestProblem("The timetable ends before it starts.");
        if (to.DayNumber - from.DayNumber > 400) return BadRequestProblem("A timetable version covers at most 400 days.");

        var settings = await _settings.ReadAsync(branchId);
        var cycleDays = TimetableCycle.CycleDayCount(settings);
        if (cycleDays == 0) return BadRequestProblem("Set up the bell schedule first.");

        var name = request.Name.Trim();
        if (name.Length == 0) return BadRequestProblem("Give the timetable a name.");

        Timetable? source = null;
        if (request.CopyFromTimetableId is { } sourceId)
        {
            source = await Db.Timetables.AsNoTracking().Include(t => t.Lessons).FirstOrDefaultAsync(t => t.Id == sourceId && t.BranchId == branchId);
            if (source == null) return BadRequestProblem("The timetable to copy from was not found.");
        }

        var actor = CurrentUserId();

        // Appointing the master(s) is part of creating the version, and only a permission holder reaches this
        // endpoint at all. An explicit list wins; otherwise the version copied from passes its masters on.
        var managers = (request.ManagerUserIds ?? source?.ManagerUserIds.ToList() ?? new List<Guid>())
            .Where(x => x != Guid.Empty).Distinct().ToArray();
        if (managers.Length > 0 && await RefuseUnknownManagersAsync(branchId, managers) is { } unknown) return unknown;

        var timetable = new Timetable
        {
            OrganizationId = organizationId, BranchId = branchId, Name = name, PeriodKey = period.Key,
            CycleDays = cycleDays, EffectiveFrom = from, EffectiveTo = to, CreatedBy = actor,
            ManagerUserIds = managers
        };
        if (source != null)
        {
            // Rows that no longer fit the cycle (a two-week cycle copied into one week) are left behind, not remapped.
            var groups = new Dictionary<Guid, Guid>();
            foreach (var l in source.Lessons.Where(l => l.CycleDay <= cycleDays))
            {
                timetable.Lessons.Add(new TimetableLesson
                {
                    CycleDay = l.CycleDay, PeriodKey = l.PeriodKey, ClassName = l.ClassName, ClassNameNormalized = l.ClassNameNormalized,
                    SubjectId = l.SubjectId, TeacherUserId = l.TeacherUserId, Room = l.Room, RoomNormalized = l.RoomNormalized,
                    GroupId = l.GroupId is { } g ? (groups.TryGetValue(g, out var ng) ? ng : groups[g] = Guid.NewGuid()) : null,
                    CreatedBy = actor
                });
            }
        }
        Db.Timetables.Add(timetable);
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.TimetableCreated, nameof(Timetable), timetable.Id, null,
            $"Timetable draft '{timetable.Name}' created for {period.Name}{(source != null ? $", copied from '{source.Name}' ({timetable.Lessons.Count} lessons)" : "")}{(managers.Length > 0 ? $", {managers.Length} appointed master(s)" : "")}",
            new { timetable.PeriodKey, timetable.CycleDays, CopiedFrom = source?.Id, timetable.ManagerUserIds }, branchId, organizationId);

        if (managers.Length > 0) await TellAppointedAsync(timetable, managers, appointed: true);

        return CreatedAtAction(nameof(GetTimetable), new { branchId, id = timetable.Id }, await BuildDetailAsync(timetable, canManage: true));
    }

    /// <summary>
    /// Appoint the master(s) of one version, replacing whoever was named. Since 2026-09-22.
    ///
    /// <b>The permission alone opens this, and that is the asymmetry the whole feature rests on</b> — a named
    /// manager may not add or remove managers, including themselves, or the appointment is self-serve and the
    /// control is decoration. It carries no <c>[RequirePermission]</c> attribute for the opposite reason to the
    /// write endpoints: the attribute would be correct here, and is spelled out in code so the two rules sit
    /// side by side and neither can be mistaken for the other.
    /// </summary>
    [HttpPut("timetables/{id:guid}/managers")]
    [ProducesResponseType(typeof(TimetableDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SetManagers(Guid branchId, Guid id, [FromBody] SetTimetableManagersRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        // THE APPOINTMENT CHECK COMES FIRST, BEFORE THE SCOPE ONE, and the order is the whole message. An
        // appointed master is almost always a teacher, whose role is StaffScope.SelfOnly — so testing the scope
        // first told them "the timetable is built by an unscoped timetable master", which is true of somebody
        // else's situation and says nothing about theirs. Found by e2e 26.1h. Give the reader the refusal that
        // is about THEM.
        if (!TimetableAccess.MayAppoint(await HasPermissionAsync(Permissions.TimetableManage)))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Appointing a timetable master needs the timetable permission",
                Detail = "Being the appointed master of a timetable does not let you appoint another one. Ask an administrator.",
                Status = StatusCodes.Status403Forbidden,
                Extensions = { ["code"] = "CANNOT_APPOINT" }
            });

        if (await RefuseScopedAsync() is { } refused) return refused;

        var timetable = await Db.Timetables.FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
        if (timetable == null) return NotFoundProblem("Timetable not found");
        if (timetable.Status == TimetableStatus.Archived)
            return ConflictProblem("That timetable is history", "An archived version is never edited, so it needs no master.");

        var wanted = request.UserIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (wanted.Length > 10) return BadRequestProblem("A timetable has at most ten appointed masters.");
        if (wanted.Length > 0 && await RefuseUnknownManagersAsync(branchId, wanted) is { } unknown) return unknown;

        var before = timetable.ManagerUserIds;
        if (before.OrderBy(x => x).SequenceEqual(wanted.OrderBy(x => x)))
            return Ok(await BuildDetailAsync(timetable, canManage: true));

        timetable.ManagerUserIds = wanted;
        timetable.UpdatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        var names = await BuildNamesAsync(before.Concat(wanted).Distinct().Select(x => (Guid?)x));
        await Activity.RecordAsync(ActivityActions.TimetableManagersSet, nameof(Timetable), timetable.Id, null,
            wanted.Length == 0
                ? $"'{timetable.Name}' has no appointed master; the timetable permission now opens it"
                : $"'{timetable.Name}' is now managed by {string.Join(", ", wanted.Select(x => names[x]))}",
            new { Before = before, After = wanted }, branchId, timetable.OrganizationId);

        // Told in both directions: somebody appointed has work to do, and somebody removed needs to know their
        // access went. Neither is a silent change to what a person may do.
        await TellAppointedAsync(timetable, wanted.Except(before).ToArray(), appointed: true);
        await TellAppointedAsync(timetable, before.Except(wanted).ToArray(), appointed: false);

        return Ok(await BuildDetailAsync(timetable, canManage: true));
    }

    /// <summary>
    /// A manager has to be an active member of THIS BRANCH. Naming somebody from another branch — or another
    /// tenant — would hand them a write they could not otherwise reach, so it is refused by name rather than
    /// stored and left to fail at the point they try to use it.
    /// </summary>
    private async Task<IActionResult?> RefuseUnknownManagersAsync(Guid branchId, IReadOnlyCollection<Guid> userIds)
    {
        // BranchStaff is the one home for "who belongs to this branch", and it matters here: somebody with no
        // assigned branch belongs to every branch of the organization, so an id-and-branch test written by hand
        // would refuse a perfectly valid appointment.
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var known = await StaffLookups.BranchStaff(Db, organizationId, branchId)
            .Where(u => userIds.Contains(u.Id))
            .Select(u => u.Id).ToListAsync();
        var missing = userIds.Except(known).ToList();
        return missing.Count == 0
            ? null
            : BadRequestProblem($"{missing.Count} of those people are not active staff of this branch.");
    }

    /// <summary>Tell somebody they have been appointed the master of a version, or that they no longer are.</summary>
    private async Task TellAppointedAsync(Timetable timetable, IReadOnlyCollection<Guid> userIds, bool appointed)
    {
        foreach (var userId in userIds.Where(x => x != CurrentUserId()))
        {
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = timetable.OrganizationId,
                    BranchId = timetable.BranchId,
                    Title = appointed ? "You are the timetable master for a term" : "You no longer manage a timetable",
                    Message = appointed
                        ? $"You can now build and publish \"{timetable.Name}\"."
                        : $"\"{timetable.Name}\" is managed by somebody else now.",
                    Type = NotificationType.StaffPerformance,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.StaffTimetablePublished,
                    ActionUrl = $"/admin/timetable?t={timetable.Id}",
                    IconClass = "grid-3x3-gap"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Appointment notice for timetable {TimetableId} could not reach {UserId}", timetable.Id, userId);
            }
        }
    }

    /// <summary>Discard a draft. A published or archived version is history and is never deleted.</summary>
    [HttpDelete("timetables/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteDraft(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var timetable = await Db.Timetables.FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
        if (timetable == null) return NotFoundProblem("Timetable not found");
        var (problem, isOverride) = await GuardWriteAsync(timetable);
        if (problem != null) return problem;
        if (timetable.Status != TimetableStatus.Draft) return ConflictProblem("Only a draft can be discarded", "A published or archived timetable is kept as history.");

        var name = timetable.Name;
        var snapshot = new Timetable { Id = id, Name = name, BranchId = branchId, OrganizationId = timetable.OrganizationId, ManagerUserIds = timetable.ManagerUserIds };
        Db.Timetables.Remove(timetable);
        await Db.SaveChangesAsync();
        await Activity.RecordAsync(ActivityActions.TimetableArchived, nameof(Timetable), id, null, $"Timetable draft '{name}' discarded", null, branchId, snapshot.OrganizationId);
        // Reported from the snapshot, because the row it is about no longer exists to read the managers off.
        if (isOverride) await ReportOverrideAsync(snapshot, "A draft was discarded");
        return NoContent();
    }

    // =====================================================================================================
    // Lessons
    // =====================================================================================================

    [HttpPost("timetables/{id:guid}/lessons")]
    [ProducesResponseType(typeof(LessonChangeResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PlaceLesson(Guid branchId, Guid id, [FromBody] PlaceLessonRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var teacherIds = request.TeacherUserIds.Where(t => t != Guid.Empty).Distinct().ToList();
        if (teacherIds.Count is 0 or > 4) return BadRequestProblem("A lesson has between one and four teachers.");
        var classNames = new[] { request.ClassName }.Concat(request.AlsoClassNames ?? new()).Select(c => c.Trim()).Where(c => c.Length > 0)
            .DistinctBy(TimetableCycle.Normalize).ToList();
        if (classNames.Count is 0 or > 8) return BadRequestProblem("A lesson is for between one and eight classes.");

        IActionResult? result = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            await LockTimetableAsync(id);
            var timetable = await Db.Timetables.FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
            if (timetable == null) { result = NotFoundProblem("Timetable not found"); return; }
            // Ownership, inside the lock: an appointed master of THIS version, or the permission (2026-09-22).
            var (refusal, isOverride) = await GuardWriteAsync(timetable);
            if (refusal != null) { result = refusal; return; }
            if (timetable.Status != TimetableStatus.Draft) { result = NotDraftProblem(); return; }

            var settings = await _settings.ReadAsync(branchId);
            var slotError = await ValidateSlotAsync(timetable, settings, request.CycleDay, request.PeriodKey);
            if (slotError != null) { result = slotError; return; }

            var (classes, rooms) = await VocabularyAsync(branchId);
            var resolvedClasses = new List<string>();
            foreach (var c in classNames)
            {
                if (!classes.TryGetValue(TimetableCycle.Normalize(c), out var configured)) { result = BadRequestProblem($"'{c}' is not a configured, active class."); return; }
                resolvedClasses.Add(configured);
            }
            string? room = null;
            if (!string.IsNullOrWhiteSpace(request.Room))
            {
                if (!rooms.TryGetValue(TimetableCycle.Normalize(request.Room), out var configuredRoom))
                { result = BadRequestProblem($"'{request.Room.Trim()}' is not a configured room.", "Add it under Student Roster → Lists → Rooms."); return; }
                room = configuredRoom;
            }

            var subject = await Db.Subjects.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.SubjectId && s.OrganizationId == timetable.OrganizationId && s.IsActive);
            if (subject == null) { result = BadRequestProblem("Choose an active subject."); return; }

            var staff = await StaffLookups.BranchStaff(Db, timetable.OrganizationId, branchId).Where(u => teacherIds.Contains(u.Id)).Select(u => u.Id).ToListAsync();
            if (staff.Count != teacherIds.Count) { result = BadRequestProblem("A teacher named is not an active member of this branch's staff."); return; }

            var busy = await TeacherBusyAsync(timetable.Id, request.CycleDay, request.PeriodKey, teacherIds, except: Array.Empty<Guid>());
            if (busy != null) { result = busy; return; }

            var groupId = resolvedClasses.Count * teacherIds.Count > 1 ? Guid.NewGuid() : (Guid?)null;
            var actor = CurrentUserId();
            var created = new List<TimetableLesson>();
            foreach (var cls in resolvedClasses)
                foreach (var teacher in teacherIds)
                    created.Add(new TimetableLesson
                    {
                        TimetableId = timetable.Id, CycleDay = request.CycleDay, PeriodKey = CanonicalPeriodKey(settings, timetable.CycleDays, request.CycleDay, request.PeriodKey),
                        ClassName = cls, ClassNameNormalized = TimetableCycle.Normalize(cls), SubjectId = subject.Id, TeacherUserId = teacher,
                        Room = room, RoomNormalized = room == null ? null : TimetableCycle.Normalize(room), GroupId = groupId, CreatedBy = actor
                    });
            Db.TimetableLessons.AddRange(created);
            timetable.UpdatedAt = DateTime.UtcNow;
            try
            {
                await Db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                result = ConflictProblem("Teacher already booked", "One of the teachers already has a lesson in that period.");
                return;
            }

            await Activity.RecordAsync(ActivityActions.TimetableLessonPlaced, nameof(Timetable), timetable.Id, null,
                $"Lesson placed in '{timetable.Name}': {string.Join(", ", resolvedClasses)} {subject.Code}, {TimetableCycle.CycleDayLabel(settings, timetable.CycleDays, request.CycleDay)} {request.PeriodKey}",
                new { request.CycleDay, request.PeriodKey, Classes = resolvedClasses, SubjectId = subject.Id, Teachers = teacherIds, Room = room }, branchId, timetable.OrganizationId);

            if (isOverride) await ReportOverrideAsync(timetable, "A lesson was placed");

            result = StatusCode(StatusCodes.Status201Created, await ChangeResultAsync(timetable, created.Select(c => c.Id)));
        });
        return result!;
    }

    /// <summary>Move a lesson — all of its joint group — to another slot, and set its room.</summary>
    [HttpPut("timetables/{id:guid}/lessons/{lessonId:guid}")]
    [ProducesResponseType(typeof(LessonChangeResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MoveLesson(Guid branchId, Guid id, Guid lessonId, [FromBody] MoveLessonRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        IActionResult? result = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            await LockTimetableAsync(id);
            var timetable = await Db.Timetables.FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
            if (timetable == null) { result = NotFoundProblem("Timetable not found"); return; }
            // Ownership, inside the lock: an appointed master of THIS version, or the permission (2026-09-22).
            var (refusal, isOverride) = await GuardWriteAsync(timetable);
            if (refusal != null) { result = refusal; return; }
            if (timetable.Status != TimetableStatus.Draft) { result = NotDraftProblem(); return; }

            var lesson = await Db.TimetableLessons.FirstOrDefaultAsync(l => l.Id == lessonId && l.TimetableId == id);
            if (lesson == null) { result = NotFoundProblem("Lesson not found"); return; }
            var rows = lesson.GroupId is { } g ? await Db.TimetableLessons.Where(l => l.TimetableId == id && l.GroupId == g).ToListAsync() : new List<TimetableLesson> { lesson };

            var settings = await _settings.ReadAsync(branchId);
            var slotError = await ValidateSlotAsync(timetable, settings, request.CycleDay, request.PeriodKey);
            if (slotError != null) { result = slotError; return; }

            string? room = null;
            if (!string.IsNullOrWhiteSpace(request.Room))
            {
                var (_, rooms) = await VocabularyAsync(branchId);
                if (!rooms.TryGetValue(TimetableCycle.Normalize(request.Room), out var configuredRoom))
                { result = BadRequestProblem($"'{request.Room.Trim()}' is not a configured room.", "Add it under Student Roster → Lists → Rooms."); return; }
                room = configuredRoom;
            }

            var busy = await TeacherBusyAsync(id, request.CycleDay, request.PeriodKey, rows.Select(r => r.TeacherUserId).Distinct(), except: rows.Select(r => r.Id));
            if (busy != null) { result = busy; return; }

            var from = $"{TimetableCycle.CycleDayLabel(settings, timetable.CycleDays, lesson.CycleDay)} {lesson.PeriodKey}";
            var periodKey = CanonicalPeriodKey(settings, timetable.CycleDays, request.CycleDay, request.PeriodKey);
            foreach (var r in rows)
            {
                r.CycleDay = request.CycleDay;
                r.PeriodKey = periodKey;
                r.Room = room;
                r.RoomNormalized = room == null ? null : TimetableCycle.Normalize(room);
                r.UpdatedAt = DateTime.UtcNow;
                r.UpdatedBy = CurrentUserId();
            }
            try
            {
                await Db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                result = ConflictProblem("Teacher already booked", "A teacher of this lesson already has a lesson in that period.");
                return;
            }

            await Activity.RecordAsync(ActivityActions.TimetableLessonPlaced, nameof(Timetable), timetable.Id, null,
                $"Lesson moved in '{timetable.Name}': {string.Join(", ", rows.Select(r => r.ClassName).Distinct())} from {from} to {TimetableCycle.CycleDayLabel(settings, timetable.CycleDays, request.CycleDay)} {periodKey}",
                new { LessonIds = rows.Select(r => r.Id), request.CycleDay, request.PeriodKey, Room = room }, branchId, timetable.OrganizationId);

            if (isOverride) await ReportOverrideAsync(timetable, "A lesson was moved");

            result = Ok(await ChangeResultAsync(timetable, rows.Select(r => r.Id)));
        });
        return result!;
    }

    /// <summary>Remove a lesson and the rest of its joint group.</summary>
    [HttpDelete("timetables/{id:guid}/lessons/{lessonId:guid}")]
    [ProducesResponseType(typeof(LessonChangeResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveLesson(Guid branchId, Guid id, Guid lessonId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        IActionResult? result = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            await LockTimetableAsync(id);
            var timetable = await Db.Timetables.FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
            if (timetable == null) { result = NotFoundProblem("Timetable not found"); return; }
            // Ownership, inside the lock: an appointed master of THIS version, or the permission (2026-09-22).
            var (refusal, isOverride) = await GuardWriteAsync(timetable);
            if (refusal != null) { result = refusal; return; }
            if (timetable.Status != TimetableStatus.Draft) { result = NotDraftProblem(); return; }

            var lesson = await Db.TimetableLessons.FirstOrDefaultAsync(l => l.Id == lessonId && l.TimetableId == id);
            if (lesson == null) { result = NotFoundProblem("Lesson not found"); return; }
            var rows = lesson.GroupId is { } g ? await Db.TimetableLessons.Where(l => l.TimetableId == id && l.GroupId == g).ToListAsync() : new List<TimetableLesson> { lesson };
            Db.TimetableLessons.RemoveRange(rows);
            timetable.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync();
            await tx.CommitAsync();

            await Activity.RecordAsync(ActivityActions.TimetableLessonRemoved, nameof(Timetable), timetable.Id, null,
                $"Lesson removed from '{timetable.Name}': {string.Join(", ", rows.Select(r => r.ClassName).Distinct())} {lesson.PeriodKey}",
                new { LessonIds = rows.Select(r => r.Id), lesson.CycleDay, lesson.PeriodKey }, branchId, timetable.OrganizationId);

            if (isOverride) await ReportOverrideAsync(timetable, "A lesson was removed");

            result = Ok(await ChangeResultAsync(timetable, Array.Empty<Guid>()));
        });
        return result!;
    }

    // =====================================================================================================
    // Publish and archive
    // =====================================================================================================

    /// <summary>
    /// Publish a draft. Refused while any hard clash remains (no override); soft clashes need acknowledging with a
    /// note. Then tells each teacher with lessons — a link, never the timetable itself.
    ///
    /// <b>PUBLISHING OVER A LIVE VERSION REFUSES UNLESS <c>Replace</c> IS ASKED FOR (2026-09-22), and that is the
    /// most consequential change in this file.</b> It used to archive every overlapping published version
    /// SILENTLY — and because every lesson query filters on Published over today's date, archiving the live one
    /// stops every lesson materialising: no registers, no teaching figures, no portal card, school-wide. The
    /// archive was reported in the response, but nobody reading "published" expects the other one to have stopped.
    ///
    /// This also answers what the school means by "three timetables in a term": they cannot be three
    /// <see cref="Timetable"/> rows, because only one may cover a date. General teaching is the timetable; exam
    /// supervision is a series of Session duties, which is what <see cref="DutyKind.Session"/> has always been for.
    /// </summary>
    [HttpPost("timetables/{id:guid}/publish")]
    [ProducesResponseType(typeof(TimetableDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(Guid branchId, Guid id, [FromBody] PublishTimetableRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        IActionResult? problem = null;
        Timetable? published = null;
        List<string> archivedNames = new();
        TimetableDiagnosisDto? diagnosis = null;
        var publishWasOverride = false;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            archivedNames = new();
            publishWasOverride = false;
            await using var tx = await Db.Database.BeginTransactionAsync();
            var branchLock = $"timetable-publish:{branchId}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({branchLock})::bigint)");
            await LockTimetableAsync(id);

            var timetable = await Db.Timetables.Include(t => t.Lessons).FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
            if (timetable == null) { problem = NotFoundProblem("Timetable not found"); return; }
            var (refusal, isOverride) = await GuardWriteAsync(timetable);
            if (refusal != null) { problem = refusal; return; }
            publishWasOverride = isOverride;
            if (timetable.Status != TimetableStatus.Draft) { problem = NotDraftProblem(); return; }
            if (timetable.Lessons.Count == 0) { problem = BadRequestProblem("There are no lessons to publish."); return; }

            diagnosis = await DiagnoseAsync(timetable, timetable.Lessons.ToList());
            if (diagnosis.HardCount > 0)
            {
                problem = Conflict(new ProblemDetails
                {
                    Title = "Hard clashes remain",
                    Detail = $"{diagnosis.HardCount} hard clash(es) must be resolved before this timetable can be published.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "HARD_CLASHES", ["hardCount"] = diagnosis.HardCount }
                });
                return;
            }
            if (diagnosis.SoftCount > 0 && !request.AcknowledgeSoftClashes)
            {
                problem = Conflict(new ProblemDetails
                {
                    Title = "Soft clashes to acknowledge",
                    Detail = $"{diagnosis.SoftCount} soft clash(es) remain. Resubmit with acknowledgeSoftClashes=true and a note.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "SOFT_CLASHES", ["softCount"] = diagnosis.SoftCount }
                });
                return;
            }
            if (diagnosis.SoftCount > 0 && string.IsNullOrWhiteSpace(request.Note))
            {
                problem = BadRequestProblem("Say why the soft clashes are acceptable.");
                return;
            }

            var overlapping = await Db.Timetables
                .Where(t => t.BranchId == branchId && t.Id != id && t.Status == TimetableStatus.Published && t.EffectiveFrom <= timetable.EffectiveTo && t.EffectiveTo >= timetable.EffectiveFrom)
                .ToListAsync();

            // REFUSED UNLESS ASKED FOR. Archiving a live version stops every lesson in the school materialising,
            // so it is never the default path — it names what it would replace and what the dates are, because a
            // school publishing an exam timetable over its teaching one is usually asking for the wrong thing.
            if (overlapping.Count > 0 && !request.Replace)
            {
                problem = Conflict(new ProblemDetails
                {
                    Title = "Another timetable is already live for these dates",
                    Detail = string.Create(CultureInfo.InvariantCulture,
                        $"{string.Join(", ", overlapping.Select(o => $"'{o.Name}' ({o.EffectiveFrom:dd MMM yyyy} to {o.EffectiveTo:dd MMM yyyy})"))} " +
                        $"covers the same dates. Publishing over it stops its lessons, registers and teaching figures. " +
                        $"Change these dates, or resubmit with replace=true to take it out of service deliberately."),
                    Status = StatusCodes.Status409Conflict,
                    Extensions =
                    {
                        ["code"] = "WOULD_REPLACE_PUBLISHED",
                        ["replaces"] = overlapping.Select(o => new { o.Id, o.Name, o.EffectiveFrom, o.EffectiveTo }).ToList()
                    }
                });
                return;
            }

            foreach (var old in overlapping)
            {
                old.Status = TimetableStatus.Archived;
                old.UpdatedAt = DateTime.UtcNow;
                archivedNames.Add(old.Name);
            }
            // Archive first, in its own statement: the one-published-per-term index would refuse the pair in either order otherwise.
            await Db.SaveChangesAsync();

            timetable.Status = TimetableStatus.Published;
            timetable.PublishedAt = DateTime.UtcNow;
            timetable.PublishedByUserId = CurrentUserId();
            timetable.ReportedIssueKeys = Array.Empty<string>();
            timetable.UpdatedAt = DateTime.UtcNow;
            try
            {
                await Db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                problem = ConflictProblem("Already published", "Another version for this term was published at the same moment.");
                return;
            }
            published = timetable;
        });
        if (problem != null) return problem;

        var timetableDone = published!;
        await Activity.RecordAsync(ActivityActions.TimetablePublished, nameof(Timetable), timetableDone.Id, null,
            $"Timetable '{timetableDone.Name}' published ({timetableDone.Lessons.Count} lessons){(archivedNames.Count > 0 ? $", replacing {string.Join(", ", archivedNames.Select(n => $"'{n}'"))}" : "")}{(diagnosis!.SoftCount > 0 ? $", {diagnosis.SoftCount} soft clash(es) acknowledged" : "")}",
            new { diagnosis.SoftCount, request.Note, Archived = archivedNames }, branchId, timetableDone.OrganizationId);

        foreach (var teacher in timetableDone.Lessons.Select(l => l.TeacherUserId).Distinct())
        {
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = teacher,
                    OrganizationId = timetableDone.OrganizationId,
                    BranchId = branchId,
                    Title = $"Your {timetableDone.Name} timetable is published",
                    Message = "Open it to see your lessons.",
                    Type = NotificationType.StaffPerformance,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.StaffTimetablePublished,
                    ActionUrl = $"/admin/timetable?t={timetableDone.Id}&by=teacher&key={teacher}",
                    IconClass = "grid-3x3-gap"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timetable published notice for {TimetableId} could not reach {UserId}", timetableDone.Id, teacher);
            }
        }

        if (publishWasOverride) await ReportOverrideAsync(timetableDone, "The timetable was published");

        // Lessons are materialised from the published version (plan §7.1): now, in the background, and nightly after.
        try { Hangfire.BackgroundJob.Enqueue<QMgr.Infrastructure.Jobs.LessonGenerationJob>(job => job.RunForBranchAsync(branchId)); }
        catch (Exception ex) { _logger.LogError(ex, "Could not enqueue lesson generation for branch {BranchId}; the nightly run will do it", branchId); }
        return await GetTimetable(branchId, timetableDone.Id);
    }

    [HttpPost("timetables/{id:guid}/archive")]
    [ProducesResponseType(typeof(TimetableDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Archive(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var timetable = await Db.Timetables.FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
        if (timetable == null) return NotFoundProblem("Timetable not found");
        var (refusal, isOverride) = await GuardWriteAsync(timetable);
        if (refusal != null) return refusal;
        if (timetable.Status != TimetableStatus.Published) return ConflictProblem("Only a published timetable can be archived");

        timetable.Status = TimetableStatus.Archived;
        timetable.UpdatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        await Activity.RecordAsync(ActivityActions.TimetableArchived, nameof(Timetable), id, null, $"Timetable '{timetable.Name}' archived", null, branchId, timetable.OrganizationId);
        if (isOverride) await ReportOverrideAsync(timetable, "The timetable was archived");
        return await GetTimetable(branchId, id);
    }

    // =====================================================================================================
    // Cover and cancellation: one-day departures from a published timetable (2026-09-22)
    // =====================================================================================================

    /// <summary>
    /// Record cover, or cancel one lesson on one date. <b>The timetable master's own path</b>; the other way in
    /// is a colleague's self-service request, which lands here through the same
    /// <see cref="TimetableExceptions.Refuse"/> rules so the two cannot disagree.
    ///
    /// It is a one-day thing and it never edits the timetable: a published version stays immutable, and the
    /// materialiser reads the exception when it makes the day's lesson duties. A PERMANENT change is a new draft
    /// published over it, which is what <see cref="Publish"/> is for.
    /// </summary>
    [HttpPost("timetables/{id:guid}/exceptions")]
    [ProducesResponseType(typeof(TimetableExceptionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateException(Guid branchId, Guid id, [FromBody] CreateLessonExceptionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var timetable = await Db.Timetables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
        if (timetable == null) return NotFoundProblem("Timetable not found");
        var (refusal, isOverride) = await GuardWriteAsync(timetable);
        if (refusal != null) return refusal;

        var lesson = await Db.TimetableLessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.TimetableLessonId && l.TimetableId == id);
        if (lesson == null) return NotFoundProblem("Lesson not found");

        var settings = await _settings.ReadAsync(branchId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, await ZoneAsync(branchId)));
        if (TimetableExceptions.Refuse(timetable, lesson, request.Date, today, request.Kind, request.CoverUserId, settings) is { } why)
            return BadRequestProblem("Cannot record that", why);

        if (request.Kind == LessonExceptionKind.Cover
            && await RefuseUnknownManagersAsync(branchId, new[] { request.CoverUserId!.Value }) != null)
            return BadRequestProblem("That person is not active staff of this branch.");

        var row = new TimetableLessonException
        {
            OrganizationId = timetable.OrganizationId, BranchId = branchId, TimetableId = id,
            TimetableLessonId = lesson.Id, Date = request.Date, Kind = request.Kind,
            CoverUserId = request.Kind == LessonExceptionKind.Cover ? request.CoverUserId : null,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CreatedByUserId = CurrentUserId(), CreatedBy = CurrentUserId()
        };
        Db.TimetableLessonExceptions.Add(row);
        try
        {
            await Db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // ONE EXCEPTION PER LESSON PER DATE, and the index is the rule. Withdraw the first rather than
            // stacking a second on it: two rows for one lesson on one date is something the materialiser would
            // have to pick between, and picking silently is how a teacher turns up to a lesson somebody covered.
            return ConflictProblem("Already arranged", "That lesson already has cover or a cancellation on that date. Withdraw it first.");
        }

        await Activity.RecordAsync(ActivityActions.TimetableExceptionSet, nameof(Timetable), id, null,
            $"{(row.Kind == LessonExceptionKind.Cover ? "Cover" : "Cancellation")} recorded on '{timetable.Name}' for {lesson.ClassName} {lesson.PeriodKey}",
            new { row.Date, row.Kind, row.CoverUserId, lesson.TeacherUserId, LessonId = lesson.Id }, branchId, timetable.OrganizationId);

        await TellAboutCoverAsync(timetable, lesson, row, withdrawn: false);
        if (isOverride) await ReportOverrideAsync(timetable, row.Kind == LessonExceptionKind.Cover ? "Cover was arranged" : "A lesson was cancelled");

        // The duty may already exist inside the 14-day window, in which case it has to change hands now rather
        // than at the nightly run — otherwise cover arranged today for tomorrow reaches nobody's My Day.
        await RegenerateLessonsAsync(branchId);

        var mapped = await MapExceptionsAsync(timetable, new List<TimetableLesson> { lesson }, settings, today);
        return StatusCode(StatusCodes.Status201Created, mapped.FirstOrDefault(x => x.Id == row.Id));
    }

    /// <summary>
    /// Undo one. The lesson goes back to its usual teacher and its usual place, and both people are told —
    /// somebody who agreed to cover a lesson has to learn that they no longer are.
    ///
    /// Either teacher may withdraw it, as well as anybody who may write the version: the two of them arranged it
    /// between themselves, so requiring an administrator to unpick it would send them back to the corridor.
    /// </summary>
    [HttpDelete("timetables/{id:guid}/exceptions/{exceptionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> WithdrawException(Guid branchId, Guid id, Guid exceptionId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var timetable = await Db.Timetables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
        if (timetable == null) return NotFoundProblem("Timetable not found");

        var row = await Db.TimetableLessonExceptions.FirstOrDefaultAsync(e => e.Id == exceptionId && e.TimetableId == id);
        if (row == null) return NotFoundProblem("Not found");
        var lesson = await Db.TimetableLessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == row.TimetableLessonId);
        if (lesson == null) return NotFoundProblem("Not found");

        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.TimetableManage);
        var mayWithdraw = TimetableAccess.MayWrite(timetable, me, holds)
                          || row.CreatedByUserId == me || row.CoverUserId == me || lesson.TeacherUserId == me;
        if (!mayWithdraw) return NotFoundProblem("Not found");

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, await ZoneAsync(branchId)));
        if (row.Date < today)
            return ConflictProblem("That date has passed", "Cover that has already happened is history the register records. It is not withdrawn afterwards.");

        Db.TimetableLessonExceptions.Remove(row);
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.TimetableExceptionWithdrawn, nameof(Timetable), id, null,
            $"{(row.Kind == LessonExceptionKind.Cover ? "Cover" : "A cancellation")} withdrawn on '{timetable.Name}' for {lesson.ClassName} {lesson.PeriodKey}",
            new { row.Date, row.Kind, row.CoverUserId, lesson.TeacherUserId }, branchId, timetable.OrganizationId);

        await TellAboutCoverAsync(timetable, lesson, row, withdrawn: true);
        await RegenerateLessonsAsync(branchId);
        return NoContent();
    }

    /// <summary>
    /// Tell the teacher whose lesson it is, and the colleague taking it. Both directions, both ways round, and it
    /// NEVER throws: the arrangement is committed, and failing a saved change because a notice did not send is
    /// the worst available answer — the TryIssueVisitToken rule.
    /// </summary>
    private async Task TellAboutCoverAsync(Timetable timetable, TimetableLesson lesson, TimetableLessonException row, bool withdrawn)
    {
        var when = string.Create(CultureInfo.InvariantCulture, $"{row.Date:dd MMM yyyy}");
        var what = $"{lesson.ClassName}, {lesson.PeriodKey}";
        var recipients = new List<(Guid UserId, string Title, string Message)>();

        if (row.Kind == LessonExceptionKind.Cover)
        {
            var names = await BuildNamesAsync(new Guid?[] { lesson.TeacherUserId, row.CoverUserId });
            var coverName = row.CoverUserId is { } c && names[c] is { Length: > 0 } cn ? cn : "A colleague";
            var teacherName = names[lesson.TeacherUserId] is { Length: > 0 } tn ? tn : "a colleague";

            recipients.Add((lesson.TeacherUserId,
                withdrawn ? "Cover for your lesson was withdrawn" : "Your lesson is covered",
                withdrawn
                    ? $"You are teaching {what} on {when} after all."
                    : $"{coverName} is teaching {what} on {when}.{Because(row.Reason)}"));

            if (row.CoverUserId is { } cover)
                recipients.Add((cover,
                    withdrawn ? "You are no longer covering a lesson" : "You are covering a lesson",
                    withdrawn
                        ? $"{teacherName}'s {what} on {when} is back with them."
                        : $"You are teaching {teacherName}'s {what} on {when}. The register is yours for that lesson.{Because(row.Reason)}"));
        }
        else
        {
            recipients.Add((lesson.TeacherUserId,
                withdrawn ? "A cancelled lesson is back on" : "A lesson was cancelled",
                withdrawn
                    ? $"{what} on {when} is on the timetable again."
                    : $"{what} on {when} does not happen.{Because(row.Reason)}"));
        }

        foreach (var (userId, title, message) in recipients.Where(r => r.UserId != Guid.Empty))
        {
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = timetable.OrganizationId,
                    BranchId = timetable.BranchId,
                    Title = title,
                    Message = message,
                    Type = NotificationType.StaffPerformance,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.StaffLessonCover,
                    ActionUrl = "/my-day",
                    IconClass = "arrow-left-right"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cover notice for timetable {TimetableId} could not reach {UserId}", timetable.Id, userId);
            }
        }

        static string Because(string? reason) => string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason.Trim()})";
    }

    /// <summary>
    /// Re-materialise the branch's lessons now, so cover inside the 14-day window changes hands on this request
    /// rather than at the nightly run. Enqueued rather than awaited, and a failure to enqueue is logged and
    /// swallowed: the nightly run is the backstop, exactly as publishing treats it.
    /// </summary>
    private Task RegenerateLessonsAsync(Guid branchId)
    {
        try { Hangfire.BackgroundJob.Enqueue<QMgr.Infrastructure.Jobs.LessonGenerationJob>(job => job.RunForBranchAsync(branchId)); }
        catch (Exception ex) { _logger.LogError(ex, "Could not enqueue lesson generation for branch {BranchId}; the nightly run will do it", branchId); }
        return Task.CompletedTask;
    }

    // =====================================================================================================
    // Import (plan §6.2, Phase 6)
    // =====================================================================================================

    /// <summary>
    /// Import lessons from an aSc / FET / spreadsheet export into a DRAFT, through the import-job machinery. Refused to a
    /// scoped caller like every other timetable write (the job cannot be scoped downstream). The rows are processed in
    /// the background; the draft's diagnosis afterwards is the report of what is left to fix.
    /// </summary>
    [HttpPost("timetables/{id:guid}/import")]
    [ProducesResponseType(typeof(RosterImportJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartImport(Guid branchId, Guid id, [FromBody] StartTimetableImportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var timetable = await Db.Timetables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId);
        if (timetable == null) return NotFoundProblem("Timetable not found");
        var (importRefusal, importIsOverride) = await GuardWriteAsync(timetable);
        if (importRefusal != null) return importRefusal;
        if (timetable.Status != TimetableStatus.Draft) return NotDraftProblem();
        if (request.Rows.Count == 0) return BadRequestProblem("The file has no lesson rows.");
        if (request.Rows.Count > 5000) return BadRequestProblem("A timetable file is at most 5,000 lesson rows.");
        var job = new RosterImportJob
        {
            OrganizationId = timetable.OrganizationId,
            BranchId = branchId,
            CreatedByUserId = CurrentUserId(),
            SourceFileName = string.IsNullOrWhiteSpace(request.SourceFileName) ? null : request.SourceFileName.Trim(),
            Source = "admin_ui",
            Kind = RosterImportKind.Timetable,
            Status = RosterImportStatus.Pending,
            TotalRows = request.Rows.Count,
            RowsJson = System.Text.Json.JsonSerializer.Serialize(new TimetableImportPayload { TimetableId = id, ReplaceExisting = request.ReplaceExisting, Rows = request.Rows })
        };
        // One import per draft at a time: the check and the insert run under a lock on the draft, or two uploads at once
        // would both pass the check and interleave their rows.
        var running = false;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"timetable-import:{id}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");
            running = await Db.RosterImportJobs.AnyAsync(j => j.BranchId == branchId && j.Kind == RosterImportKind.Timetable
                && (j.Status == RosterImportStatus.Pending || j.Status == RosterImportStatus.Processing) && j.RowsJson.Contains(id.ToString()));
            if (running) return;
            Db.RosterImportJobs.Add(job);
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        if (running) return ConflictProblem("An import into this draft is already running");

        await Activity.RecordAsync(ActivityActions.TimetableImported, nameof(Timetable), id, null,
            $"Timetable import into '{timetable.Name}' started: {request.Rows.Count} row(s){(request.ReplaceExisting ? ", replacing the draft's lessons" : "")}",
            new { Rows = request.Rows.Count, request.ReplaceExisting, request.SourceFileName, JobId = job.Id }, branchId, timetable.OrganizationId);

        if (importIsOverride) await ReportOverrideAsync(timetable, "Lessons were imported");

        Hangfire.BackgroundJob.Enqueue<QMgr.Infrastructure.Jobs.RosterImportProcessorJob>(j => j.ProcessAsync(job.Id));
        return StatusCode(StatusCodes.Status202Accepted, StudentsController.MapToDto(job));
    }

    [HttpGet("import-jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(RosterImportJobDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImportJob(Guid branchId, Guid jobId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await StaffScope.IsUnscopedAsync()) return NotFoundProblem("Import not found");
        var job = await Db.RosterImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.BranchId == branchId && j.Kind == RosterImportKind.Timetable);
        if (job == null || await RefuseImportJobAsync(job)) return NotFoundProblem("Import not found");
        return Ok(StudentsController.MapToDto(job));
    }

    [HttpGet("import-jobs/{jobId:guid}/entries")]
    [ProducesResponseType(typeof(List<RosterImportJobEntryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImportJobEntries(Guid branchId, Guid jobId, [FromQuery] int limit = 500)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await StaffScope.IsUnscopedAsync()) return NotFoundProblem("Import not found");
        var job = await Db.RosterImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.BranchId == branchId && j.Kind == RosterImportKind.Timetable);
        if (job == null || await RefuseImportJobAsync(job)) return NotFoundProblem("Import not found");
        var entries = await Db.RosterImportJobEntries.AsNoTracking().Where(e => e.RosterImportJobId == jobId).OrderBy(e => e.RowNumber).Take(Math.Clamp(limit, 1, 5000)).ToListAsync();
        return Ok(entries.Select(StudentsController.MapToDto).ToList());
    }

    /// <summary>
    /// True when this caller may not read a timetable import's progress. Since the write endpoints stopped
    /// carrying <c>[RequirePermission]</c>, "unscoped" alone would have let any member of staff read another
    /// import's rows — so it is the permission, OR having started the job, which is the same ownership rule the
    /// welfare import log uses (<c>ApplyImportJobScopeAsync</c>). Ownership is what covers an appointed manager
    /// who holds no permission: they started it, so they may watch it.
    /// </summary>
    private async Task<bool> RefuseImportJobAsync(RosterImportJob job)
        => job.CreatedByUserId != CurrentUserId() && !await HasPermissionAsync(Permissions.TimetableManage);

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    // =====================================================================================================
    // Ownership (2026-09-22). TimetableAccess holds the rule; these two turn it into an HTTP answer.
    // =====================================================================================================

    /// <summary>
    /// Refuses when this caller may not write the version, and says whether the write is an ADMINISTRATOR
    /// OVERRIDE — a permission holder touching a version somebody else is named on. Every write endpoint calls
    /// this in place of the <c>[RequirePermission]</c> attribute it used to carry, because the attribute cannot
    /// let a named manager who holds no permission through.
    ///
    /// The refusal names who the version belongs to: "you cannot edit this" without saying whose it is sends
    /// somebody to ask the wrong person.
    /// </summary>
    private async Task<(IActionResult? Problem, bool IsOverride)> GuardWriteAsync(Timetable timetable)
    {
        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.TimetableManage);
        if (TimetableAccess.MayWrite(timetable, me, holds))
        {
            // THE SCOPE REFUSAL APPLIES TO A PERMISSION HOLDER AND NOT TO AN APPOINTED MASTER — see
            // TimetableAccess.NeedsUnscopedStaffView. The seeded teacher role is StaffScope.SelfOnly, and a
            // teacher is exactly who a school appoints, so testing the scope first would refuse the very person
            // the appointment exists for.
            if (TimetableAccess.NeedsUnscopedStaffView(timetable, me) && await RefuseScopedAsync() is { } scoped)
                return (scoped, false);
            return (null, TimetableAccess.IsOverride(timetable, me, holds));
        }

        var names = await BuildNamesAsync(timetable.ManagerUserIds.Select(x => (Guid?)x));
        var detail = TimetableAccess.Refuse(timetable, me, holds,
            timetable.ManagerUserIds.ToDictionary(x => x, x => names[x]));
        return (StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
        {
            Title = "This timetable is not yours to change",
            Detail = detail,
            Status = StatusCodes.Status403Forbidden,
            Extensions = { ["code"] = "NOT_TIMETABLE_MANAGER" }
        }), false);
    }

    /// <summary>
    /// Record the override and tell every named manager. Called AFTER the write has committed, and it never
    /// throws: a change that has been saved must not be failed because the notice about it did not send — the
    /// same call as VisitorsController.TryIssueVisitToken. The activity event is the durable half; the
    /// notification is the courtesy.
    /// </summary>
    private async Task ReportOverrideAsync(Timetable timetable, string what)
    {
        var actor = CurrentUserId();
        try
        {
            await Activity.RecordAsync(ActivityActions.TimetableOverridden, nameof(Timetable), timetable.Id, null,
                $"{what} on '{timetable.Name}' by somebody who is not its appointed master",
                new { timetable.ManagerUserIds, What = what }, timetable.BranchId, timetable.OrganizationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the timetable override on {TimetableId}", timetable.Id);
        }

        var tell = TimetableAccess.TellAbout(timetable, actor);
        if (tell.Count == 0) return;
        var names = await BuildNamesAsync(new Guid?[] { actor });
        var who = names[actor] is { Length: > 0 } n ? n : "An administrator";
        foreach (var manager in tell)
        {
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = manager,
                    OrganizationId = timetable.OrganizationId,
                    BranchId = timetable.BranchId,
                    Title = "Somebody else changed your timetable",
                    Message = $"{who}: {what} on \"{timetable.Name}\".",
                    Type = NotificationType.StaffPerformance,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.StaffTimetableOverride,
                    ActionUrl = $"/admin/timetable?t={timetable.Id}",
                    IconClass = "shield-exclamation"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Override notice for timetable {TimetableId} could not reach {UserId}", timetable.Id, manager);
            }
        }
    }

    /// <summary>A scoped caller cannot build the timetable: publishing is bulk work a job carries out for the whole school.</summary>
    private async Task<IActionResult?> RefuseScopedAsync()
    {
        if (await StaffScope.IsUnscopedAsync()) return null;
        return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
        {
            Title = "The timetable is built by an unscoped timetable master",
            Detail = "Your role sees part of the staff, and a timetable is published for the whole school.",
            Status = StatusCodes.Status403Forbidden
        });
    }

    private IActionResult NotDraftProblem()
        => ConflictProblem("Only a draft can be edited", "A published timetable is changed by creating a new draft from it and publishing that.");

    private Task LockTimetableAsync(Guid id)
    {
        var lockKey = $"timetable:{id}";
        return Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private async Task<IActionResult?> ValidateSlotAsync(Timetable timetable, TimetableSettingsDto settings, int cycleDay, string periodKey)
    {
        if (cycleDay < 1 || cycleDay > timetable.CycleDays) return BadRequestProblem($"The cycle has {timetable.CycleDays} days.");
        if (TimetableCycle.LessonPeriodOn(settings, timetable.CycleDays, cycleDay, periodKey) == null)
            return BadRequestProblem($"{TimetableCycle.CycleDayLabel(settings, timetable.CycleDays, cycleDay)} has no teaching period '{periodKey}' in the bell schedule.");
        await Task.CompletedTask;
        return null;
    }

    private static string CanonicalPeriodKey(TimetableSettingsDto settings, int cycleDays, int cycleDay, string periodKey)
        => TimetableCycle.LessonPeriodOn(settings, cycleDays, cycleDay, periodKey)?.Key ?? periodKey.Trim();

    /// <summary>409 when a teacher already has a lesson in the slot (other than the rows being moved), naming what.</summary>
    private async Task<IActionResult?> TeacherBusyAsync(Guid timetableId, int cycleDay, string periodKey, IEnumerable<Guid> teachers, IEnumerable<Guid> except)
    {
        var ids = teachers.ToList();
        var skip = except.ToList();
        var key = periodKey.Trim().ToLower();
        var clash = await Db.TimetableLessons.AsNoTracking()
            .Where(l => l.TimetableId == timetableId && l.CycleDay == cycleDay && l.PeriodKey.ToLower() == key && ids.Contains(l.TeacherUserId) && !skip.Contains(l.Id))
            .Select(l => new { l.TeacherUserId, l.ClassName })
            .FirstOrDefaultAsync();
        if (clash == null) return null;
        var names = await BuildNamesAsync(new Guid?[] { clash.TeacherUserId });
        return ConflictProblem("Teacher already booked", $"{names[clash.TeacherUserId]} already teaches {clash.ClassName} in that period.");
    }

    private async Task<(Dictionary<string, string> Classes, Dictionary<string, string> Rooms)> VocabularyAsync(Guid branchId)
    {
        var json = await Db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var vocab = StudentsController.ReadVocabularies(json);
        return (
            vocab.Classes.Where(c => c.IsActive).GroupBy(c => TimetableCycle.Normalize(c.Name)).ToDictionary(g => g.Key, g => g.First().Name),
            vocab.Rooms.Where(r => r.IsActive).GroupBy(r => TimetableCycle.Normalize(r.Name)).ToDictionary(g => g.Key, g => g.First().Name));
    }

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => AppointmentScheduling.ResolveTimeZone(await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

    private async Task<TimetableDiagnosisDto> DiagnoseAsync(Timetable timetable, IReadOnlyList<TimetableLesson> lessons)
    {
        var settings = await _settings.ReadAsync(timetable.BranchId);
        var policy = await _policy.GetAsync(timetable.OrganizationId);
        var ctx = await TimetableChecker.LoadContextAsync(Db, timetable, settings, policy, await ZoneAsync(timetable.BranchId), lessons);
        return TimetableChecker.Diagnose(timetable, lessons, ctx);
    }

    private async Task<LessonChangeResultDto> ChangeResultAsync(Timetable timetable, IEnumerable<Guid> changedIds)
    {
        var lessons = await Db.TimetableLessons.AsNoTracking().Where(l => l.TimetableId == timetable.Id).ToListAsync();
        var ids = changedIds.ToHashSet();
        var mapped = await MapLessonsAsync(timetable.OrganizationId, lessons.Where(l => ids.Contains(l.Id)).ToList());
        return new LessonChangeResultDto { Lessons = mapped, Diagnosis = await DiagnoseAsync(timetable, lessons) };
    }

    private async Task<TimetableDetailDto> BuildDetailAsync(Timetable timetable, bool canManage)
    {
        var lessons = await Db.TimetableLessons.AsNoTracking().Where(l => l.TimetableId == timetable.Id).ToListAsync();
        var settings = await _settings.ReadAsync(timetable.BranchId);
        if (!canManage) settings.Unavailability = new();
        var policy = await _policy.GetAsync(timetable.OrganizationId);
        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.TimetableManage);
        var names = await BuildNamesAsync(timetable.ManagerUserIds.Select(x => (Guid?)x).Append(timetable.PublishedByUserId));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, await ZoneAsync(timetable.BranchId)));
        return new TimetableDetailDto
        {
            Timetable = ToDto(timetable, lessons.Count, policy, names, me, holds, today),
            Settings = settings,
            Lessons = await MapLessonsAsync(timetable.OrganizationId, lessons),
            Diagnosis = canManage && timetable.Status != TimetableStatus.Archived ? await DiagnoseAsync(timetable, lessons) : null,
            CanManage = canManage && await StaffScope.IsUnscopedAsync(),
            // Cover and cancellations from today on. Only a published version can have any, and everyone who may
            // read the version may read them: who is teaching a class on Thursday is operational information, the
            // same argument that makes a published timetable readable by the whole branch.
            Exceptions = timetable.Status == TimetableStatus.Published
                ? await MapExceptionsAsync(timetable, lessons, settings, today)
                : new()
        };
    }

    /// <summary>
    /// The version's cover and cancellations from <paramref name="today"/> on. A past exception is history the
    /// register already records, so the page does not carry it.
    /// </summary>
    private async Task<List<TimetableExceptionDto>> MapExceptionsAsync(Timetable timetable, List<TimetableLesson> lessons, TimetableSettingsDto settings, DateOnly today)
    {
        var rows = await Db.TimetableLessonExceptions.AsNoTracking()
            .Where(e => e.TimetableId == timetable.Id && e.Date >= today)
            .OrderBy(e => e.Date)
            .ToListAsync();
        if (rows.Count == 0) return new();

        var byId = lessons.ToDictionary(l => l.Id);
        var subjectIds = lessons.Select(l => l.SubjectId).Distinct().ToList();
        var subjects = await Db.Subjects.AsNoTracking()
            .Where(s => s.OrganizationId == timetable.OrganizationId && subjectIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);
        var names = await BuildNamesAsync(rows.SelectMany(e => new[] { e.CoverUserId, (Guid?)e.CreatedByUserId })
            .Concat(lessons.Select(l => (Guid?)l.TeacherUserId)));

        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.TimetableManage);
        var mayWrite = TimetableAccess.MayWrite(timetable, me, holds);

        var result = new List<TimetableExceptionDto>(rows.Count);
        foreach (var e in rows)
        {
            if (!byId.TryGetValue(e.TimetableLessonId, out var lesson)) continue;
            var teacherName = names[lesson.TeacherUserId] is { Length: > 0 } tn ? tn : "A former member of staff";
            var coverName = e.CoverUserId is { } c ? (names[c] is { Length: > 0 } cn ? cn : "A former member of staff") : null;
            var subject = subjects.GetValueOrDefault(lesson.SubjectId);
            var period = TimetableCycle.LessonPeriodOn(settings, timetable.CycleDays, lesson.CycleDay, lesson.PeriodKey);
            var periodLabel = period == null ? lesson.PeriodKey : $"{period.Label} ({period.Start}–{period.End})";
            result.Add(new TimetableExceptionDto
            {
                Id = e.Id, TimetableId = e.TimetableId, TimetableLessonId = e.TimetableLessonId, Date = e.Date, Kind = e.Kind,
                CoverUserId = e.CoverUserId, CoverUserName = coverName,
                TeacherUserId = lesson.TeacherUserId, TeacherName = teacherName,
                CycleDay = lesson.CycleDay, PeriodKey = lesson.PeriodKey, ClassName = lesson.ClassName,
                SubjectName = subject, Room = lesson.Room, Reason = e.Reason, SourceRequestId = e.SourceRequestId,
                CreatedByName = names.Optional(e.CreatedByUserId), CreatedAt = e.CreatedAt,
                // The two teachers may undo what they arranged; so may anybody who may write the version. A
                // colleague who is neither reads it and nothing more.
                CanIWithdraw = mayWrite || e.CreatedByUserId == me || e.CoverUserId == me || lesson.TeacherUserId == me,
                Summary = TimetableExceptions.SummaryOf(e, teacherName, coverName, lesson.ClassName, subject, periodLabel)
            });
        }
        return result;
    }

    private async Task<List<TimetableLessonDto>> MapLessonsAsync(Guid organizationId, List<TimetableLesson> lessons)
    {
        var names = await BuildNamesAsync(lessons.Select(l => (Guid?)l.TeacherUserId));
        var subjectIds = lessons.Select(l => l.SubjectId).Distinct().ToList();
        var subjects = await Db.Subjects.AsNoTracking().Where(s => s.OrganizationId == organizationId && subjectIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.Name, s.Code, s.Color });
        return lessons
            .OrderBy(l => l.CycleDay).ThenBy(l => l.PeriodKey).ThenBy(l => l.ClassName)
            .Select(l => new TimetableLessonDto
            {
                Id = l.Id, CycleDay = l.CycleDay, PeriodKey = l.PeriodKey, ClassName = l.ClassName, SubjectId = l.SubjectId,
                SubjectName = subjects.TryGetValue(l.SubjectId, out var s) ? s.Name : "A removed subject",
                SubjectCode = s?.Code ?? "?", SubjectColor = s?.Color,
                TeacherUserId = l.TeacherUserId, TeacherName = names[l.TeacherUserId] is { Length: > 0 } n ? n : "A former member of staff",
                Room = l.Room, GroupId = l.GroupId
            })
            .ToList();
    }

    /// <summary>
    /// <paramref name="today"/> is the BRANCH-LOCAL date, and it is what turns a Published version past its last
    /// day into <see cref="TimetableStatus.Expired"/>. Derived here rather than stored, so the label can never
    /// disagree with the dates.
    /// </summary>
    private TimetableDto ToDto(Timetable t, int lessonCount, StaffPerformancePolicyDto policy, StaffPerformanceMapping.NameLookup names,
        Guid me, bool holdsManage, DateOnly today) => new()
    {
        Id = t.Id, BranchId = t.BranchId, Name = t.Name, PeriodKey = t.PeriodKey, PeriodName = _policy.FindPeriod(policy, t.PeriodKey)?.Name,
        CycleDays = t.CycleDays, Status = t.StatusOn(today), PublishedAt = t.PublishedAt, PublishedByName = names.Optional(t.PublishedByUserId),
        EffectiveFrom = t.EffectiveFrom, EffectiveTo = t.EffectiveTo, LessonCount = lessonCount, CreatedAt = t.CreatedAt,
        ManagerUserIds = t.ManagerUserIds.ToList(),
        ManagerNames = t.ManagerUserIds.Select(id => names[id] is { Length: > 0 } n ? n : "A former member of staff").ToList(),
        IAmManager = TimetableAccess.IsManager(t, me),
        CanIWrite = TimetableAccess.MayWrite(t, me, holdsManage),
        WouldBeOverride = TimetableAccess.IsOverride(t, me, holdsManage),
        CanIAppoint = TimetableAccess.MayAppoint(holdsManage)
    };
}
