using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Who holds pastoral responsibility for each class. Two things read this: the alert fan-out when
/// a student has a case logged, and <see cref="IStudentScopeService"/>, which narrows what a
/// class-scoped user can see to the students in their own classes.
///
/// The class VOCABULARY itself is not here — it lives in <c>Branch.Settings</c> and is edited by
/// <c>StudentsController.UpdateVocabularies</c>. This controller only ever references a class by a
/// name that already exists there, validated on write.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action also carries its own [RequirePermission]
[RequireModule(ModuleCodes.StudentWelfare)]
public class ClassTeachersController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IStudentScopeService _scope;
    private readonly INotificationHubService _hubService;
    private readonly IStaffScopeService _staffScope;
    private readonly IActivityLogger _activity;
    private readonly ILogger<ClassTeachersController> _logger;

    public ClassTeachersController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IStudentScopeService scope,
        INotificationHubService hubService,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        ILogger<ClassTeachersController> logger)
    {
        _staffScope = staffScope;
        _activity = activity;
        _context = context;
        _tenantAccessor = tenantAccessor;
        _scope = scope;
        _hubService = hubService;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Helpers — same shapes as StudentsController/WelfareController rather than a second copy
    // with its own opinions.
    // ---------------------------------------------------------------------

    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails { Title = "Organization not resolved", Status = StatusCodes.Status401Unauthorized });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var superAdminBranchExists = await _context.Branches.AnyAsync(b => b.Id == branchId);
            return superAdminBranchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
        }

        var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        return branchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
    }

    private async Task<Guid> ResolveOrganizationIdAsync(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext!;
        return RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? (await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync())
            : tenantContext.OrganizationId;
    }

    private Guid CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : Guid.Empty;
    }

    /// <summary>
    /// The one comparison rule for class names, everywhere. Student.ClassName is free text and the
    /// vocabulary is user-typed, so "S4B", "s4b" and " S4B " must all be the same class — a student
    /// invisible to their own class teacher because of a stray space is a safeguarding failure, not
    /// a cosmetic one.
    /// </summary>
    internal static string NormalizeClassName(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    private async Task<bool> HasPermissionAsync(string code)
    {
        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole)) return true;
        var userId = CurrentUserId();
        return userId != Guid.Empty && await _context.Users
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions)
            .AnyAsync(rp => rp.Permission.Code == code);
    }

    // ---------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------

    /// <summary>Every live assignment in the branch, with the teacher's contact card.</summary>
    [HttpGet("branches/{branchId:guid}/class-teachers")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(List<ClassTeacherDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAssignments(Guid branchId, [FromQuery] bool includeEnded = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.ClassTeacherAssignments
            .AsNoTracking()
            .Where(a => a.BranchId == branchId);

        if (!includeEnded) query = query.Where(a => a.EndedAt == null);

        // A class-scoped caller sees only the assignments for their own classes — otherwise the
        // roster of who teaches what would be a way around the scope for everything else.
        if (!await _scope.IsUnscopedAsync())
        {
            // Both tiers: a subject teacher needs to know who the class teacher of a class they teach is,
            // which is who they tell about a concern. The list is names and contact cards, not the pupils.
            var mine = (await _scope.GetPastoralClassNamesAsync(branchId)).Concat(await _scope.GetTeachingClassNamesAsync(branchId)).Distinct().ToList();
            if (mine.Count == 0) return Ok(new List<ClassTeacherDto>());
            var normalized = mine.Select(NormalizeClassName).ToList();
            query = query.Where(a => normalized.Contains(a.ClassName.Trim().ToLower()));
        }

        var rows = await query.OrderBy(a => a.ClassName).ThenBy(a => a.Role).ToListAsync();
        return Ok(await MapAsync(rows, branchId));
    }

    /// <summary>
    /// What the CALLER teaches. Deliberately gated on nothing but <c>welfare.view</c>: a class
    /// teacher must be able to read their own assignments to render their dashboard, and this
    /// returns only rows that are already theirs.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/class-teachers/mine")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(List<ClassTeacherDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyAssignments(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var callerId = CurrentUserId();
        var rows = await _context.ClassTeacherAssignments
            .AsNoTracking()
            .Where(a => a.BranchId == branchId && a.UserId == callerId && a.EndedAt == null)
            .OrderBy(a => a.ClassName)
            .ToListAsync();

        return Ok(await MapAsync(rows, branchId));
    }

    /// <summary>
    /// The management view: every configured class, who holds it, and every way this feature can
    /// silently fail — a class with no teacher gets no alerts, a scoped user with no class sees
    /// nothing at all, and a student whose free-text class matches no configured entry is invisible
    /// to their own class teacher.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/class-teachers/coverage")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [ProducesResponseType(typeof(ClassTeacherCoverageReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCoverage(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var settingsJson = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var vocabClasses = StudentsController.ReadVocabularies(settingsJson).Classes;

        var studentClasses = await _context.Students
            .AsNoTracking()
            .Where(s => s.BranchId == branchId && s.IsActive && s.ClassName != null && s.ClassName != "")
            .GroupBy(s => s.ClassName!)
            .Select(g => new { ClassName = g.Key, Count = g.Count() })
            .ToListAsync();

        var countsByNormalized = studentClasses
            .GroupBy(x => NormalizeClassName(x.ClassName))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

        var live = await _context.ClassTeacherAssignments
            .AsNoTracking()
            .Where(a => a.BranchId == branchId && a.EndedAt == null)
            .ToListAsync();

        var mapped = await MapAsync(live, branchId);
        var byClass = mapped.GroupBy(a => NormalizeClassName(a.ClassName)).ToDictionary(g => g.Key, g => g.ToList());

        var classes = new List<ClassTeacherCoverageDto>();
        var noTeacher = new List<string>();

        foreach (var vc in vocabClasses.Where(c => c.IsActive).OrderBy(c => c.SortOrder))
        {
            var key = NormalizeClassName(vc.Name);
            byClass.TryGetValue(key, out var assigned);
            assigned ??= new List<ClassTeacherDto>();

            var primary = assigned.FirstOrDefault(a => a.Role == ClassTeacherRole.ClassTeacher);
            var assistants = assigned.Where(a => a.Role == ClassTeacherRole.Assistant).ToList();

            classes.Add(new ClassTeacherCoverageDto
            {
                ClassName = vc.Name,
                Color = vc.Color,
                Level = vc.Level,
                StudentCount = countsByNormalized.GetValueOrDefault(key),
                ClassTeacher = primary,
                Assistants = assistants,
                SubjectTeachers = assigned.Where(a => a.Role == ClassTeacherRole.SubjectTeacher).OrderBy(a => a.SubjectName).ThenBy(a => a.FullName).ToList()
            });

            if (primary == null && assistants.Count == 0) noTeacher.Add(vc.Name);
        }

        // Students whose class matches no configured vocabulary entry. This is the silent one:
        // "S4 B" against a vocabulary of "S4B" means that child never reaches their class teacher.
        var vocabKeys = vocabClasses.Select(c => NormalizeClassName(c.Name)).ToHashSet();
        var unknown = studentClasses
            .Where(x => !vocabKeys.Contains(NormalizeClassName(x.ClassName)))
            .GroupBy(x => x.ClassName)
            .Select(g => new ClassTeacherUnknownClassDto { ClassName = g.Key, StudentCount = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.StudentCount)
            .ToList();

        // Users on a class-scoped role with no live assignment anywhere in this branch. They can
        // currently see nothing at all, which reads to them as "the app is broken".
        var assignedUserIds = live.Select(a => a.UserId).ToHashSet();
        var orphans = await _context.Users
            .AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive && u.Role.DataScope == RoleDataScope.AssignedClasses)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
            .ToListAsync();

        var placed = await TimetableChecker.PlacedPerWeekAsync(_context, branchId);
        var subjectTeachers = mapped.Where(a => a.Role == ClassTeacherRole.SubjectTeacher && a.SubjectId != null).ToList();
        decimal PlacedFor(ClassTeacherDto a) => placed.GetValueOrDefault((a.UserId, NormalizeClassName(a.ClassName), a.SubjectId!.Value));

        return Ok(new ClassTeacherCoverageReportDto
        {
            Classes = classes,
            ClassesWithNoTeacher = noTeacher,
            ScopedUsersWithNoClass = orphans
                .Where(u => !assignedUserIds.Contains(u.Id))
                .Select(u => new ClassTeacherOrphanUserDto
                {
                    UserId = u.Id,
                    FullName = $"{u.FirstName} {u.LastName}".Trim(),
                    Email = u.Email ?? string.Empty
                })
                .ToList(),
            UnknownStudentClasses = unknown,
            SubjectGaps = SubjectGaps(classes),
            // Filled from the published timetable in force today (duty rota plan §5.2); both stay empty until one is.
            SubjectTeachersWithNoLessons = placed.Count == 0 ? new() : subjectTeachers.Where(a => PlacedFor(a) == 0).ToList(),
            PlannedPeriodMismatches = placed.Count == 0 ? new() : subjectTeachers
                .Where(a => a.PeriodsPerWeek is > 0 && PlacedFor(a) != a.PeriodsPerWeek)
                .Select(a => new PlannedPeriodsMismatchDto { Assignment = a, Planned = a.PeriodsPerWeek ?? 0, Timetabled = (int)Math.Round(PlacedFor(a)) })
                .ToList()
        });
    }

    /// <summary>A stream's curriculum is what its level teaches anywhere (see <see cref="ClassSubjectGapDto"/>).</summary>
    private static List<ClassSubjectGapDto> SubjectGaps(List<ClassTeacherCoverageDto> classes)
    {
        var gaps = new List<ClassSubjectGapDto>();
        foreach (var level in classes.Where(c => !string.IsNullOrWhiteSpace(c.Level)).GroupBy(c => c.Level!.Trim().ToLowerInvariant()))
        {
            var streams = level.ToList();
            if (streams.Count < 2) continue;
            var taught = streams.SelectMany(c => c.SubjectTeachers).Where(t => t.SubjectName != null).Select(t => t.SubjectName!).Distinct().ToList();
            foreach (var stream in streams)
            {
                var mine = stream.SubjectTeachers.Select(t => t.SubjectName).ToHashSet();
                var missing = taught.Where(s => !mine.Contains(s)).OrderBy(s => s).ToList();
                if (missing.Count > 0)
                    gaps.Add(new ClassSubjectGapDto { ClassName = stream.ClassName, Level = stream.Level!, MissingSubjects = missing });
            }
        }
        return gaps;
    }

    /// <summary>Full history for one class, ended assignments included — the audit answer to "who could see this class last term".</summary>
    [HttpGet("branches/{branchId:guid}/class-teachers/history")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [ProducesResponseType(typeof(List<ClassTeacherDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(Guid branchId, [FromQuery] string? className = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.ClassTeacherAssignments.AsNoTracking().Where(a => a.BranchId == branchId);

        if (!string.IsNullOrWhiteSpace(className))
        {
            var key = NormalizeClassName(className);
            query = query.Where(a => a.ClassName.Trim().ToLower() == key);
        }

        var rows = await query.OrderByDescending(a => a.AssignedAt).Take(500).ToListAsync();
        return Ok(await MapAsync(rows, branchId));
    }

    // ---------------------------------------------------------------------
    // Writes
    // ---------------------------------------------------------------------

    [HttpPost("branches/{branchId:guid}/class-teachers")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [ProducesResponseType(typeof(ClassTeacherDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Assign(Guid branchId, [FromBody] AssignClassTeacherRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);

        if (!Enum.IsDefined(request.Role))
            return BadRequest(new ProblemDetails { Title = "Unrecognised assignment role", Status = StatusCodes.Status400BadRequest });
        // A subject teacher needs a subject; that is its own endpoint so the request shape says so.
        if (request.Role == ClassTeacherRole.SubjectTeacher)
            return BadRequest(new ProblemDetails { Title = "Pick a subject", Detail = "Subject teachers are assigned with a subject: POST …/class-teachers/subject-teachers.", Status = StatusCodes.Status400BadRequest });

        // The class must already exist in the branch's vocabulary. Assigning a teacher to a class
        // that does not exist produces an assignment that matches no student and never fires —
        // it would look configured and do nothing.
        var settingsJson = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var vocab = StudentsController.ReadVocabularies(settingsJson).Classes;
        var match = vocab.FirstOrDefault(c => NormalizeClassName(c.Name) == NormalizeClassName(request.ClassName));
        if (match == null)
            return BadRequest(new ProblemDetails
            {
                Title = "Class not found",
                Detail = $"\"{request.ClassName?.Trim()}\" is not one of this branch's classes. Add it under Classes, houses & lists first.",
                Status = StatusCodes.Status400BadRequest
            });

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == request.UserId && u.OrganizationId == organizationId && u.IsActive);
        if (user == null)
            return BadRequest(new ProblemDetails { Title = "Member of staff not found", Detail = "The selected user does not exist in this organization, or is no longer active.", Status = StatusCodes.Status400BadRequest });

        var normalized = NormalizeClassName(match.Name);

        var duplicate = await _context.ClassTeacherAssignments
            .AnyAsync(a => a.BranchId == branchId && a.EndedAt == null && a.UserId == user.Id && a.Role != ClassTeacherRole.SubjectTeacher && a.ClassName.Trim().ToLower() == normalized);
        if (duplicate)
            return Conflict(new ProblemDetails { Title = "Already assigned", Detail = $"{user.FullName} already holds {match.Name}.", Status = StatusCodes.Status409Conflict });

        if (request.Role == ClassTeacherRole.ClassTeacher)
        {
            var existingPrimary = await _context.ClassTeacherAssignments
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.BranchId == branchId && a.EndedAt == null
                    && a.Role == ClassTeacherRole.ClassTeacher && a.ClassName.Trim().ToLower() == normalized);
            if (existingPrimary != null)
                return Conflict(new ProblemDetails
                {
                    Title = "That class already has a class teacher",
                    Detail = $"{existingPrimary.User?.FullName ?? "Someone"} holds {match.Name}. End that assignment first, or add this person as an assistant.",
                    Status = StatusCodes.Status409Conflict
                });
        }

        var assignment = new ClassTeacherAssignment
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            // Stored as the vocabulary spells it, not as the caller typed it — so the UI always
            // reads back the canonical name and two assignments to the same class never look
            // different on screen.
            ClassName = match.Name,
            UserId = user.Id,
            Role = request.Role,
            AssignedAt = DateTime.UtcNow,
            AssignedByUserId = CurrentUserId(),
            CreatedBy = CurrentUserId()
        };

        _context.ClassTeacherAssignments.Add(assignment);
        await _context.SaveChangesAsync();

        // The assignment IS this user's data scope, so a stale client would show them a roster they
        // can no longer load (or hide one they now can). Same push the role editor already uses.
        await SafePushPermissionsChangedAsync(user.Id);

        _logger.LogInformation("Class teacher assigned: user {UserId} → {ClassName} ({Role}) in branch {BranchId}",
            user.Id, match.Name, request.Role, branchId);

        var dto = (await MapAsync(new[] { assignment }, branchId)).Single();
        return CreatedAtAction(nameof(GetAssignments), new { branchId }, dto);
    }

    /// <summary>
    /// Assigns a SUBJECT teacher (duty rota plan §5.2): the Teaching tier of the student scope for that class —
    /// roster basics, never welfare. Any number of subject teachers per class; the same person teaching the same
    /// subject in the same class twice is refused here and by <c>ux_subject_teacher_once_per_class_subject</c>.
    /// Holding the class pastorally as well is allowed: the higher tier wins per student.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/class-teachers/subject-teachers")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [ProducesResponseType(typeof(ClassTeacherDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignSubjectTeacher(Guid branchId, [FromBody] AssignSubjectTeacherRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var result = await TeachingAssignments.AssignSubjectTeacherAsync(_context, organizationId, branchId,
            request.UserId, request.SubjectId, request.ClassName, request.PeriodsPerWeek, CurrentUserId());
        if (result.Assignment == null)
            return result.IsConflict
                ? Conflict(new ProblemDetails { Title = result.Title, Detail = result.Detail, Status = StatusCodes.Status409Conflict })
                : BadRequest(new ProblemDetails { Title = result.Title, Detail = result.Detail, Status = StatusCodes.Status400BadRequest });

        var assignment = result.Assignment;
        await SafePushPermissionsChangedAsync(assignment.UserId);
        var dto = (await MapAsync(new[] { assignment }, branchId)).Single();
        await _activity.RecordAsync(ActivityActions.SubjectTeacherAssigned, nameof(ClassTeacherAssignment), assignment.Id, assignment.UserId,
            $"{dto.FullName} assigned to teach {dto.SubjectName} in {dto.ClassName}", new { assignment.ClassName, assignment.SubjectId, assignment.PeriodsPerWeek },
            branchId, organizationId);
        return CreatedAtAction(nameof(GetAssignments), new { branchId }, dto);
    }

    /// <summary>Changes the planned periods a week on a live subject-teacher assignment. Everything else is end-and-reassign, so the history stays true.</summary>
    [HttpPatch("branches/{branchId:guid}/class-teachers/{assignmentId:guid}/periods")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [ProducesResponseType(typeof(ClassTeacherDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdatePeriods(Guid branchId, Guid assignmentId, [FromBody] UpdateSubjectPeriodsRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var assignment = await _context.ClassTeacherAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.BranchId == branchId && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher);
        if (assignment == null)
            return NotFound(new ProblemDetails { Title = "Assignment not found", Status = StatusCodes.Status404NotFound });
        if (request.PeriodsPerWeek is < 0 or > 60)
            return BadRequest(new ProblemDetails { Title = "Periods a week must be between 0 and 60", Status = StatusCodes.Status400BadRequest });

        assignment.PeriodsPerWeek = request.PeriodsPerWeek;
        assignment.UpdatedAt = DateTime.UtcNow;
        assignment.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();
        return Ok((await MapAsync(new[] { assignment }, branchId)).Single());
    }

    /// <summary>
    /// A teacher's teaching, grouped by subject: "Mathematics — S2A, S2B (12 periods)" (plan §5.2). The caller's
    /// own needs nothing; somebody else's needs classes.teachers.manage, or staff.records.view with that person
    /// inside the caller's staff scope (404 otherwise).
    /// </summary>
    [HttpGet("branches/{branchId:guid}/class-teachers/teaching")]
    [ProducesResponseType(typeof(List<TeachingSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTeaching(Guid branchId, [FromQuery] Guid? userId = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var me = CurrentUserId();
        var who = userId ?? me;
        if (who != me && !await HasPermissionAsync(Permissions.ClassTeachersManage)
            && !(await HasPermissionAsync(Permissions.StaffRecordsView) && await _staffScope.CanSeeStaffAsync(branchId, who)))
            return NotFound(new ProblemDetails { Title = "Staff member not found", Status = StatusCodes.Status404NotFound });

        return Ok(await TeachingAssignments.SummaryAsync(_context, branchId, who));
    }

    [HttpDelete("branches/{branchId:guid}/class-teachers/{assignmentId:guid}")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [ProducesResponseType(typeof(ClassTeacherDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> End(Guid branchId, Guid assignmentId, [FromBody] EndClassTeacherRequest? request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var assignment = await _context.ClassTeacherAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.BranchId == branchId);

        if (assignment == null)
            return NotFound(new ProblemDetails { Title = "Assignment not found", Status = StatusCodes.Status404NotFound });

        if (assignment.EndedAt != null)
            return BadRequest(new ProblemDetails { Title = "Already ended", Detail = "This assignment was already ended.", Status = StatusCodes.Status400BadRequest });

        // Ended, never deleted — the history of who could see a child's file is the audit answer.
        assignment.EndedAt = DateTime.UtcNow;
        assignment.EndedByUserId = CurrentUserId();
        assignment.EndReason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request!.Reason!.Trim();
        assignment.UpdatedAt = DateTime.UtcNow;
        assignment.UpdatedBy = CurrentUserId();

        await _context.SaveChangesAsync();

        await SafePushPermissionsChangedAsync(assignment.UserId);

        _logger.LogInformation("Class teacher assignment {AssignmentId} ended for user {UserId} ({ClassName})",
            assignment.Id, assignment.UserId, assignment.ClassName);

        var dto = (await MapAsync(new[] { assignment }, branchId)).Single();
        if (assignment.Role == ClassTeacherRole.SubjectTeacher)
            await _activity.RecordAsync(ActivityActions.SubjectTeacherEnded, nameof(ClassTeacherAssignment), assignment.Id, assignment.UserId,
                $"{dto.FullName} no longer teaches {dto.SubjectName} in {dto.ClassName}", new { assignment.ClassName, assignment.SubjectId, assignment.EndReason },
                branchId, assignment.OrganizationId);
        return Ok(dto);
    }

    /// <summary>
    /// The teacher's contact card. Separate from the user editor because the people who maintain
    /// "who do I call about this child" are not always the people who administer accounts — and it
    /// keeps <c>classes.teachers.manage</c> from having to imply <c>users.edit</c>.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/class-teachers/staff/{userId:guid}/contact")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateContact(Guid branchId, Guid userId, [FromBody] UpdateStaffContactRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == organizationId);
        if (user == null)
            return NotFound(new ProblemDetails { Title = "Member of staff not found", Status = StatusCodes.Status404NotFound });

        // A full replace of the contact block, not a merge — the form has to be able to blank a
        // field, and a merge would make clearing a stale phone number impossible.
        user.Phone = Trim(request.Phone);
        user.AlternatePhone = Trim(request.AlternatePhone);
        user.OfficeLocation = Trim(request.OfficeLocation);
        user.JobTitle = Trim(request.JobTitle);
        user.UpdatedAt = DateTime.UtcNow;
        user.UpdatedBy = CurrentUserId();

        await _context.SaveChangesAsync();
        return NoContent();

        static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    // ---------------------------------------------------------------------
    // Mapping
    // ---------------------------------------------------------------------

    private async Task<List<ClassTeacherDto>> MapAsync(IEnumerable<ClassTeacherAssignment> rows, Guid branchId)
    {
        var list = rows.ToList();
        if (list.Count == 0) return new List<ClassTeacherDto>();

        var userIds = list.Select(a => a.UserId)
            .Concat(list.Select(a => a.AssignedByUserId))
            .Concat(list.Where(a => a.EndedByUserId.HasValue).Select(a => a.EndedByUserId!.Value))
            .Distinct()
            .ToList();

        var users = await _context.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new
            {
                u.Id, u.FirstName, u.LastName, u.Username, u.Email, u.Phone,
                u.AlternatePhone, u.OfficeLocation, u.JobTitle, u.EmployeeNumber,
                u.IsActive, RoleCode = u.Role.Code
            })
            .ToDictionaryAsync(u => u.Id);

        var subjectIds = list.Where(a => a.SubjectId.HasValue).Select(a => a.SubjectId!.Value).Distinct().ToList();
        var subjects = subjectIds.Count == 0
            ? new Dictionary<Guid, (string Name, string Code)>()
            : await _context.Subjects.IgnoreQueryFilters().AsNoTracking().Where(s => subjectIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name, s.Code })
                .ToDictionaryAsync(s => s.Id, s => (s.Name, s.Code));

        // One grouped count for every class in the batch rather than a query per row.
        var classKeys = list.Select(a => NormalizeClassName(a.ClassName)).Distinct().ToList();
        var counts = (await _context.Students
                .AsNoTracking()
                .Where(s => s.BranchId == branchId && s.IsActive && s.ClassName != null)
                .GroupBy(s => s.ClassName!)
                .Select(g => new { ClassName = g.Key, Count = g.Count() })
                .ToListAsync())
            .GroupBy(x => NormalizeClassName(x.ClassName))
            .Where(g => classKeys.Contains(g.Key))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

        return list.Select(a =>
        {
            users.TryGetValue(a.UserId, out var u);
            var assignedBy = users.GetValueOrDefault(a.AssignedByUserId);
            var endedBy = a.EndedByUserId.HasValue ? users.GetValueOrDefault(a.EndedByUserId.Value) : null;

            return new ClassTeacherDto
            {
                Id = a.Id,
                BranchId = a.BranchId,
                ClassName = a.ClassName,
                UserId = a.UserId,
                Role = a.Role,
                SubjectId = a.SubjectId,
                SubjectName = a.SubjectId is { } sid && subjects.TryGetValue(sid, out var subject) ? subject.Name : null,
                SubjectCode = a.SubjectId is { } sid2 && subjects.TryGetValue(sid2, out var subject2) ? subject2.Code : null,
                PeriodsPerWeek = a.PeriodsPerWeek,
                FullName = u == null ? "Unknown" : $"{u.FirstName} {u.LastName}".Trim(),
                Username = u?.Username ?? "",
                Email = u?.Email ?? "",
                Phone = u?.Phone,
                AlternatePhone = u?.AlternatePhone,
                OfficeLocation = u?.OfficeLocation,
                JobTitle = u?.JobTitle,
                EmployeeNumber = u?.EmployeeNumber,
                RoleCode = u?.RoleCode ?? "",
                UserIsActive = u?.IsActive ?? false,
                AssignedAt = a.AssignedAt,
                AssignedByName = assignedBy == null ? "Unknown" : $"{assignedBy.FirstName} {assignedBy.LastName}".Trim(),
                EndedAt = a.EndedAt,
                EndedByName = endedBy == null ? null : $"{endedBy.FirstName} {endedBy.LastName}".Trim(),
                EndReason = a.EndReason,
                StudentCount = counts.GetValueOrDefault(NormalizeClassName(a.ClassName))
            };
        }).ToList();
    }

    /// <summary>
    /// The push is a courtesy — the server re-resolves scope on every request regardless, so a
    /// failed push costs a stale menu until the next navigation, not a security hole. It runs after
    /// the assignment has already been committed, so under this project's standing rule it must not
    /// be able to fail the request.
    /// </summary>
    private async Task SafePushPermissionsChangedAsync(Guid userId)
    {
        try
        {
            await _hubService.NotifyPermissionsChangedAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not push a permissions-changed signal to user {UserId}; their client will pick the change up on its next request", userId);
        }
    }
}
