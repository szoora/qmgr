using QMgr.API.Application.Services;
using System.Globalization;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Filters;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Jobs;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The Student Welfare Ledger — achievements, behavior incidents, and welfare concerns logged
/// against a Student (see StudentsController). Direct-DbContext controller, same shape as
/// StudentsController/VisitorsController — this is CRUD-plus-notify, not a queue-transaction
/// command the Mediator/CQRS pipeline is reserved for elsewhere in this app.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action also carries its own [RequirePermission]
[RequireModule(ModuleCodes.StudentWelfare)]
public class WelfareController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly INotificationService _notificationService;
    private readonly IMediaStorageService _mediaStorage;
    private readonly IStudentScopeService _scope;
    private readonly IWelfareAlertService _alerts;
    private readonly IStaffSystemAwards _systemAwards;
    private readonly IStaffPerformancePolicyService _staffPolicy;
    private readonly IActivityLogger _activity;
    private readonly ILogger<WelfareController> _logger;

    // 25MB — bumped from the original 10MB to admit short video/audio evidence clips. Deliberately
    // not bumped further: this app's storage is local disk with no CDN tier (see the welfare-plan's
    // §05 file-upload section), and video at any real length is a genuinely different capacity
    // question than a phone photo — worth a real conversation with whoever owns the server before
    // going higher, not a silent scope add.
    private const long MaxAttachmentSizeBytes = 25 * 1024 * 1024;
    // Internal (not private) so RosterImportProcessorJob's welfare-import branch validates a
    // backfilled historical row against exactly the same limits as a record logged live here —
    // one set of numbers, not a second copy that drifts.
    internal const int MaxDescriptionLength = 2000;
    internal const int MinDescriptionLength = 10;
    internal const int MaxPoints = 100;
    private const int LateEntryThresholdDays = 14;
    private const int MaxImportRows = 10000;
    private static readonly string[] AllowedAttachmentMimePrefixes = { "image/", "application/pdf", "video/", "audio/" };

    public WelfareController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        INotificationService notificationService,
        IMediaStorageService mediaStorage,
        IStudentScopeService scope,
        IWelfareAlertService alerts,
        IStaffSystemAwards systemAwards,
        IStaffPerformancePolicyService staffPolicy,
        IActivityLogger activity,
        ILogger<WelfareController> logger)
    {
        _activity = activity;
        _staffPolicy = staffPolicy;
        _context = context;
        _tenantAccessor = tenantAccessor;
        _notificationService = notificationService;
        _mediaStorage = mediaStorage;
        _scope = scope;
        _alerts = alerts;
        _systemAwards = systemAwards;
        _logger = logger;
    }

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

    // Permissions aren't carried as JWT claims in this app (see PermissionAuthorizationHandler) —
    // resolved by role lookup, matching that handler's own shape rather than a second, drifting
    // copy of it. Memoised per request (this is a scoped controller) because the visibility ceiling
    // is now read on essentially every action rather than a handful of times.
    private bool? _canViewConfidential;
    private bool? _canViewRestricted;

    private async Task<bool> HasPermissionAsync(string code)
    {
        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole)) return true;

        var userId = CurrentUserId();
        if (userId == Guid.Empty) return false;

        // Role AND posts, through PostPermissionService.EffectiveCodesAsync — the one home for that union, and
        // THE MOST IMPORTANT OF THE FOURTEEN readers that had to move: the whole derived-post feature exists so
        // that being the class teacher of S4B confers welfare.view, and this is the gate the welfare endpoints
        // ask. Reading the role alone meant the attribute let a class teacher in and the checks inside then
        // behaved as though they held nothing.
        _effectiveCodes ??= await PostPermissionService.EffectiveCodesAsync(_context, userId);
        return _effectiveCodes.Contains(code);
    }

    private HashSet<string>? _effectiveCodes;

    private async Task<bool> CanViewConfidentialAsync()
        => _canViewConfidential ??= await HasPermissionAsync(Permissions.WelfareConfidentialView);

    private async Task<bool> CanViewRestrictedAsync()
        => _canViewRestricted ??= await HasPermissionAsync(Permissions.WelfareRestrictedView);

    /// <summary>
    /// The highest rung this caller may see. ONE place computes it, and every query and by-ID check
    /// compares against it — rather than each site re-deriving "confidential OR restricted OR…",
    /// which is how a fourth call site eventually gets the boolean algebra wrong.
    ///
    /// Note the rungs are NOT nested permissions: holding welfare.restricted.view without
    /// welfare.confidential.view is a legitimate (if odd) configuration, and it must not
    /// accidentally grant the rung below. Hence Max over what is actually held, not a ladder.
    /// </summary>
    private async Task<WelfareVisibility> MaxVisibilityAsync()
    {
        if (await CanViewRestrictedAsync()) return WelfareVisibility.Restricted;
        if (await CanViewConfidentialAsync()) return WelfareVisibility.Confidential;
        return WelfareVisibility.Standard;
    }

    /// <summary>
    /// The set of levels this caller may see. Used instead of <c>&lt;= max</c> so the
    /// "restricted but not confidential" configuration above behaves correctly rather than
    /// silently widening.
    /// </summary>
    private async Task<List<WelfareVisibility>> VisibleLevelsAsync()
    {
        var levels = new List<WelfareVisibility> { WelfareVisibility.Standard };
        if (await CanViewConfidentialAsync()) levels.Add(WelfareVisibility.Confidential);
        if (await CanViewRestrictedAsync()) levels.Add(WelfareVisibility.Restricted);
        return levels;
    }

    private async Task<bool> CanSeeAsync(WelfareVisibility visibility) => visibility switch
    {
        WelfareVisibility.Standard => true,
        WelfareVisibility.Confidential => await CanViewConfidentialAsync(),
        WelfareVisibility.Restricted => await CanViewRestrictedAsync(),
        _ => false // an unrecognised level fails closed rather than defaulting to visible
    };

    /// <summary>
    /// Combines the branch check with the row-level student scope. Every action that reaches a
    /// student — directly or through a record — calls this rather than VerifyBranchOwnership alone,
    /// so a new endpoint gets both guards from one call. Returns NotFound (never Forbid) for an
    /// out-of-scope student: the 404-not-403 shape this controller already uses everywhere.
    /// </summary>
    private async Task<IActionResult?> VerifyStudentAccess(Guid branchId, Guid studentId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        return await _scope.VerifyStudentAccessAsync(branchId, studentId);
    }

    /// <summary>
    /// Narrows a WelfareRecord query to the students the caller may see. The BRANCH-WIDE
    /// counterpart to <see cref="VerifyStudentAccess"/>: that one guards a single student reached
    /// by ID, this one guards a query that spans students.
    ///
    /// Every branch-wide read must call it — the reports list, the dashboard summary, the cohort
    /// breakdown, my-actions. The live e2e on 2026-09-09 found all four unscoped while every
    /// per-student read was correctly guarded, which is exactly the shape this helper exists to
    /// stop recurring: the per-student paths are the obvious ones to remember.
    ///
    /// Also matches AdditionalStudentIds, so a record filed against another class but LINKED to
    /// one of the caller's students still reaches them — the same rule the timeline already uses.
    /// </summary>
    private async Task<IQueryable<WelfareRecord>> ApplyStudentScopeAsync(IQueryable<WelfareRecord> query, Guid branchId)
    {
        var visible = await _scope.GetVisibleStudentIdsAsync(branchId);
        if (visible == null) return query; // unscoped caller

        // FAILS CLOSED: an empty set is an empty result, never an unfiltered one.
        if (visible.Count == 0) return query.Where(_ => false);

        var ids = visible.ToList();
        return query.Where(r => ids.Contains(r.StudentId)
            || (r.AdditionalStudentIds != null && r.AdditionalStudentIds.Any(a => ids.Contains(a))));
    }

    /// <summary>
    /// The import log's counterpart to <see cref="ApplyStudentScopeAsync"/>. A welfare import job's
    /// per-row entries carry <c>StudentName</c>, <c>GuardianName</c> and the row message for every
    /// row of a whole-branch backfill, so an unscoped import log hands a class-scoped caller the
    /// entire school roll and the descriptions that came with it — the same leak
    /// <see cref="ApplyStudentScopeAsync"/> exists to stop, reached by a different route.
    ///
    /// There is no student ID to filter on here (a failed row may have matched no student at all),
    /// so the rule is ownership rather than class membership: a scoped caller sees the jobs they
    /// started themselves and nothing else. FAILS CLOSED — an unidentifiable caller sees none.
    /// </summary>
    private async Task<IQueryable<RosterImportJob>> ApplyImportJobScopeAsync(IQueryable<RosterImportJob> query)
    {
        if (await _scope.IsUnscopedAsync()) return query;

        var userId = CurrentUserId();
        if (userId == Guid.Empty) return query.Where(_ => false);

        return query.Where(j => j.CreatedByUserId == userId);
    }

    // ---------------------------------------------------------------------
    // Categories (org-scoped, admin-managed — same "admin picks it" convention as ClassColors)
    // ---------------------------------------------------------------------

    [HttpGet("branches/{branchId:guid}/welfare/categories")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(List<WelfareCategoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCategories(Guid branchId, [FromQuery] WelfareCaseType? caseType = null, [FromQuery] bool includeInactive = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var query = _context.WelfareCategories.Where(c => c.OrganizationId == organizationId);
        if (!includeInactive) query = query.Where(c => c.IsActive);
        if (caseType.HasValue) query = query.Where(c => c.CaseType == caseType.Value);

        var categories = await query.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        return Ok(categories.Select(MapToDto).ToList());
    }

    [HttpPost("branches/{branchId:guid}/welfare/categories")]
    [RequirePermission(Permissions.WelfareCategoriesManage)]
    [ProducesResponseType(typeof(WelfareCategoryDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateCategory(Guid branchId, [FromBody] CreateWelfareCategoryRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ProblemDetails { Title = "Category name is required", Status = StatusCodes.Status400BadRequest });

        var pointsError = ValidatePointsSign(request.CaseType, request.DefaultPoints);
        if (pointsError != null) return pointsError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);

        if (await _context.WelfareCategories.AnyAsync(c => c.OrganizationId == organizationId && c.CaseType == request.CaseType && c.Name.ToLower() == request.Name.Trim().ToLower() && c.IsActive))
            return Conflict(new ProblemDetails { Title = $"A {request.CaseType} category named '{request.Name.Trim()}' already exists", Status = StatusCodes.Status409Conflict });

        var category = new WelfareCategory
        {
            OrganizationId = organizationId,
            CaseType = request.CaseType,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            DefaultTier = request.DefaultTier,
            DefaultPoints = request.DefaultPoints,
            Color = string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.Trim(),
            SortOrder = request.SortOrder
        };
        _context.WelfareCategories.Add(category);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetCategories), new { branchId }, MapToDto(category));
    }

    [HttpPut("branches/{branchId:guid}/welfare/categories/{categoryId:guid}")]
    [RequirePermission(Permissions.WelfareCategoriesManage)]
    [ProducesResponseType(typeof(WelfareCategoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCategory(Guid branchId, Guid categoryId, [FromBody] UpdateWelfareCategoryRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var category = await _context.WelfareCategories.FirstOrDefaultAsync(c => c.Id == categoryId && c.OrganizationId == organizationId);
        if (category == null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ProblemDetails { Title = "Category name is required", Status = StatusCodes.Status400BadRequest });

        var pointsError = ValidatePointsSign(category.CaseType, request.DefaultPoints);
        if (pointsError != null) return pointsError;

        category.Name = request.Name.Trim();
        category.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        category.DefaultTier = request.DefaultTier;
        category.DefaultPoints = request.DefaultPoints;
        category.Color = string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.Trim();
        category.SortOrder = request.SortOrder;
        category.IsActive = request.IsActive;
        category.UpdatedAt = DateTime.UtcNow;
        category.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        return Ok(MapToDto(category));
    }

    [HttpPatch("branches/{branchId:guid}/welfare/categories/{categoryId:guid}/toggle")]
    [RequirePermission(Permissions.WelfareCategoriesManage)]
    [ProducesResponseType(typeof(WelfareCategoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleCategory(Guid branchId, Guid categoryId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var category = await _context.WelfareCategories.FirstOrDefaultAsync(c => c.Id == categoryId && c.OrganizationId == organizationId);
        if (category == null) return NotFound();

        category.IsActive = !category.IsActive;
        category.UpdatedAt = DateTime.UtcNow;
        category.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        return Ok(MapToDto(category));
    }

    /// <summary>
    /// "a" or "an" for a case-type name rendered into operator-facing copy. Vowel-initial is the
    /// whole rule here because the values are a closed, known set (Achievement, Behavior, Welfare,
    /// SupportPlan) with no "hour"/"union" style exceptions among them.
    /// </summary>
    private static string Article(WelfareCaseType caseType) =>
        "AEIOU".Contains(caseType.ToString()[0]) ? "an" : "a";

    private static IActionResult? ValidatePointsSign(WelfareCaseType caseType, int? points)
    {
        var error = PointsSignError(caseType, points);
        return error == null ? null : new BadRequestObjectResult(new ProblemDetails { Title = error, Status = StatusCodes.Status400BadRequest });
    }

    /// <summary>The points-sign rule as a plain message (null = valid) — shared with the historical-import processor, which has no HTTP result to hand back, only a per-row log line.</summary>
    internal static string? PointsSignError(WelfareCaseType caseType, int? points)
    {
        if (points == null) return null;

        if (Math.Abs(points.Value) > MaxPoints)
            return $"Points must be between -{MaxPoints} and {MaxPoints}";

        return caseType switch
        {
            WelfareCaseType.Achievement when points < 0 => "Achievement points must be zero or positive",
            WelfareCaseType.Behavior when points > 0 => "Behavior points must be zero or negative",
            WelfareCaseType.Welfare when points != 0 => "Welfare concerns are not scored — leave points blank",
            _ => null
        };
    }

    private static WelfareCategoryDto MapToDto(WelfareCategory c) => new()
    {
        Id = c.Id,
        CaseType = c.CaseType,
        Name = c.Name,
        Description = c.Description,
        DefaultTier = c.DefaultTier,
        DefaultPoints = c.DefaultPoints,
        Color = c.Color,
        SortOrder = c.SortOrder,
        IsActive = c.IsActive
    };

    // ---------------------------------------------------------------------
    // Records — the chronology
    // ---------------------------------------------------------------------

    /// <summary>
    /// A student's full chronology, every case type together in one reverse-chronological
    /// timeline (the CPOMS lesson — see the welfare-plan). Welfare-tier (confidential) records
    /// are silently omitted for a caller without welfare.confidential.view, exactly like a
    /// cross-tenant record 404s instead of confirming existence — a Staff member should never be
    /// able to tell a hidden concern exists at all, not even that "something" was filtered out.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/{studentId:guid}/welfare-records")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(List<WelfareRecordDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentRecords(
        Guid branchId, Guid studentId,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var accessError = await VerifyStudentAccess(branchId, studentId);
        if (accessError != null) return accessError;

        var studentExists = await _context.Students.AnyAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (!studentExists) return NotFound(new ProblemDetails { Title = "Student not found", Status = StatusCodes.Status404NotFound });

        var callerId = CurrentUserId();
        var isSuperAdmin = RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole);
        var query = _context.WelfareRecords
            .Include(r => r.Student)
            .Include(r => r.Category)
            .Include(r => r.Attachments)
            .Include(r => r.Notes)
            .Include(r => r.Notifications)
            // Includes records where studentId is the primary owner OR one of the linked
            // AdditionalStudentIds — the whole point of that column is "also show this on the
            // other student's timeline too" (see WelfareRecord.AdditionalStudentIds).
            .Where(r => r.BranchId == branchId && (r.StudentId == studentId || (r.AdditionalStudentIds != null && r.AdditionalStudentIds.Contains(studentId))));

        // Optional period filter, for a student whose timeline covers several years. Filtered here
        // rather than in the browser so a six-year history is not shipped in full just to show one
        // term of it — the whole reason this exists.
        //
        // Bounds are on OccurredAt (when the thing happened) rather than CreatedAt (when somebody
        // typed it up). A concern logged three weeks late belongs in the week it happened, which is
        // the week a reader is looking for it in.
        //
        // Npgsql rejects a Kind=Unspecified DateTime against a "timestamp with time zone" column,
        // and DateOnly.ToDateTime always produces Unspecified — so both bounds are stamped Utc
        // explicitly rather than compared raw.
        if (from.HasValue)
        {
            var fromUtc = DateTime.SpecifyKind(from.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(r => r.OccurredAt >= fromUtc);
        }

        if (to.HasValue)
        {
            // Exclusive upper bound on the NEXT day, so a record at 14:30 on the "to" date is
            // included — an inclusive-looking range that silently drops its own last day is the
            // classic off-by-one in date filtering.
            var toUtc = DateTime.SpecifyKind(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(r => r.OccurredAt < toUtc);
        }

        var levels = await VisibleLevelsAsync();
        query = query.Where(r => levels.Contains(r.Visibility));
        // A draft is only visible to the person still writing it — see FinalizeRecord.
        if (!isSuperAdmin)
            query = query.Where(r => r.Status != WelfareStatus.Draft || r.ReportedByUserId == callerId);

        var records = await query.OrderByDescending(r => r.OccurredAt).ToListAsync();

        var userNames = await ResolveUserNamesAsync(records);
        var guardianNames = await ResolveGuardianNamesAsync(records);
        var studentNames = await ResolveStudentNamesAsync(records);
        return Ok(records.Select(r => MapToDto(r, userNames, guardianNames, studentNames)).ToList());
    }

    [HttpGet("branches/{branchId:guid}/welfare-records/{recordId:guid}")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(WelfareRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecord(Guid branchId, Guid recordId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await _context.WelfareRecords
            .Include(r => r.Student)
            .Include(r => r.Category)
            .Include(r => r.Attachments)
            .Include(r => r.Notes)
            .Include(r => r.Notifications)
            .FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);

        var isSuperAdmin = RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole);
        // THE AUTHOR RULE (duty rota plan §5.3): reporting a concern is not reading one, but the person who
        // wrote a record keeps read access to it below Restricted — a subject teacher who logged a concern
        // about a child they teach (forced Confidential for a welfare case) can read back what they wrote,
        // and nothing else about that child.
        var isAuthor = record != null && record.ReportedByUserId == CurrentUserId() && record.Visibility != WelfareVisibility.Restricted;
        // Same 404-not-403 shape everywhere else in this app: a record above the caller's
        // visibility ceiling, one for a student outside their class scope, or someone else's
        // still-in-progress draft all read identically to a record that doesn't exist.
        if (record == null || (!isAuthor && !await CanSeeAsync(record.Visibility)) ||
            (!isAuthor && !await _scope.CanSeeStudentAsync(branchId, record.StudentId)) ||
            (record.Status == WelfareStatus.Draft && record.ReportedByUserId != CurrentUserId() && !isSuperAdmin))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });

        var userNames = await ResolveUserNamesAsync(new[] { record });
        var guardianNames = await ResolveGuardianNamesAsync(new[] { record });
        var studentNames = await ResolveStudentNamesAsync(new[] { record });
        return Ok(MapToDto(record, userNames, guardianNames, studentNames));
    }

    [HttpPost("branches/{branchId:guid}/welfare-records")]
    [RequirePermission(Permissions.WelfareCreate)]
    [ProducesResponseType(typeof(WelfareRecordDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateRecord(Guid branchId, [FromBody] CreateWelfareRecordRequest request, [FromQuery] bool acknowledgeLateEntry = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        // Every rule a record obeys lives in BuildRecordAsync, which the group log calls too — so a
        // rule added here binds both, and neither can drift into a second copy.
        var (error, built) = await BuildRecordAsync(branchId, request, acknowledgeLateEntry);
        if (error != null) return error;

        var record = built!.Record;
        _context.WelfareRecords.Add(record);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Welfare record {RecordId} ({CaseType}/{Category}, {Status}) logged for student {StudentId} in branch {BranchId}",
            record.Id, record.CaseType, built.Category.Name, record.Status, built.Student.Id, branchId);

        // Tell the class teacher. AFTER the commit, and by a service that never throws — this
        // project's standing rule is that a side effect running after a committed transaction must
        // not be able to fail the request (the ProtectSystem=strict badge-token bug). A draft
        // alerts nobody; so does anything above Standard visibility. Both are decided inside.
        await _alerts.NotifyRecordLoggedAsync(record.Id);

        // Staff Performance system award (policy-gated, off by default; never throws). A draft earns
        // nothing — FinalizeRecord credits it when it becomes real.
        if (record.Status != WelfareStatus.Draft)
            await _systemAwards.CreditAsync(record.OrganizationId, branchId, record.ReportedByUserId, StaffSystemAwards.WelfareRecordFiled,
                $"Filed a {built.Category.Name} record. Credited automatically.");

        record.Student = built.Student;
        record.Category = built.Category;
        var userNames = await ResolveUserNamesAsync(new[] { record });
        var studentNames = await ResolveStudentNamesAsync(new[] { record });
        return CreatedAtAction(nameof(GetRecord), new { branchId, recordId = record.Id }, MapToDto(record, userNames, new Dictionary<Guid, string>(), studentNames));
    }

    private const string AdditionalStudentsNotFoundTitle = "One or more additional students not found";

    /// <summary>
    /// Logs the same Achievement or Behaviour record for a group of students — a merit for a whole class, or a
    /// behaviour incident several students were part of (plan STUDENT_ROSTER_AND_LIST_STANDARD §3).
    ///
    /// The rules, each deliberate:
    /// <list type="bullet">
    /// <item>A WELFARE CONCERN IS NEVER BULK (decision L7): a confidential concern is that child's own record.</item>
    /// <item>SCOPE, THE WHOLE BATCH: every student is checked exactly as the single create checks its student,
    /// and one that is unknown or out of the caller's scope refuses the batch with the SAME unknown-student
    /// wording — a distinct "not your class" message would confirm the child is on the roll.</item>
    /// <item>SYNCHRONOUS, ONE TRANSACTION, AT MOST 200. The Hangfire batch path refuses a scoped caller because
    /// the scope service does not exist in a worker; this runs in the request, so a class teacher can log a merit
    /// for their own class, and it is all or nothing.</item>
    /// <item>THE SAME CREATION CODE per record (<see cref="BuildRecordAsync"/>): late entry, category, points,
    /// visibility and the rest bind exactly as they do for one.</item>
    /// <item>ALERTS COALESCED (L5): one message per recipient, never forty. No guardian message — a category
    /// carries no "tell the guardians" setting, and the single create sends none either.</item>
    /// </list>
    /// </summary>
    [HttpPost("branches/{branchId:guid}/welfare-records/bulk")]
    [RequirePermission(Permissions.WelfareCreate)]
    [ProducesResponseType(typeof(BulkWelfareRecordResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateRecordsBulk(Guid branchId, [FromBody] BulkWelfareRecordRequest request, [FromQuery] bool acknowledgeLateEntry = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (request?.Record == null)
            return FieldProblem("Record", "Describe the record to log.");

        var ids = (request.StudentIds ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return FieldProblem("StudentIds", "Choose at least one student.");
        if (ids.Count > WelfareBulkLimits.MaxStudents)
            return FieldProblem("StudentIds", WelfareBulkLimits.CapMessage);

        var caseType = request.Record.CaseType;
        if (caseType == WelfareCaseType.Welfare)
            return FieldProblem("Record.CaseType", WelfareBulkLimits.WelfareRefusal);
        if (caseType != WelfareCaseType.Achievement && caseType != WelfareCaseType.Behavior)
            return FieldProblem("Record.CaseType", "Only an achievement or a behaviour record can be logged for a group.");
        if (request.OneIncident && caseType != WelfareCaseType.Behavior)
            return FieldProblem("OneIncident", "One incident involving all of them is for a behaviour record only.");
        if (request.Record.SaveAsDraft)
            return FieldProblem("Record.SaveAsDraft", "A record for a group cannot be saved as a draft.");

        // --- SCOPE, THE WHOLE BATCH, BEFORE ANYTHING ELSE ---
        var resolved = new List<(Student Student, bool ByTeachingTier)>(ids.Count);
        foreach (var id in ids)
        {
            var (student, byTeachingTier) = await ResolveStudentForWriteAsync(branchId, id);
            if (student == null) return StudentNotFound();
            resolved.Add((student, byTeachingTier));
        }

        // --- Build every record through the one creation path. Nothing is written until all have passed. ---
        var built = new List<BuiltWelfareRecord>();
        if (request.OneIncident)
        {
            var incident = request.Record with { StudentId = ids[0], AdditionalStudentIds = ids.Skip(1).ToList() };
            var (error, one) = await BuildRecordAsync(branchId, incident, acknowledgeLateEntry, resolved[0]);
            // The linked-students check words its refusal differently; every student already passed the
            // primary check above, so any refusal about who is in the incident reads as the unknown student.
            if (error is ObjectResult { Value: ProblemDetails { Title: AdditionalStudentsNotFoundTitle } }) return StudentNotFound();
            if (error != null) return error;
            built.Add(one!);
        }
        else
        {
            foreach (var r in resolved)
            {
                var single = request.Record with { StudentId = r.Student.Id, AdditionalStudentIds = new List<Guid>() };
                var (error, one) = await BuildRecordAsync(branchId, single, acknowledgeLateEntry, r);
                if (error != null) return error;
                built.Add(one!);
            }
        }

        // --- One transaction, through the execution strategy: all or nothing. ---
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync();
            foreach (var b in built)
                if (_context.Entry(b.Record).State == EntityState.Detached)
                    _context.WelfareRecords.Add(b.Record);
            await _context.SaveChangesAsync();
            await tx.CommitAsync();
        });

        var batchId = Guid.NewGuid();
        var category = built[0].Category;
        var organizationId = built[0].Record.OrganizationId;
        var recordIds = built.Select(b => b.Record.Id).ToList();

        _logger.LogInformation("Welfare batch {BatchId}: {Records} {CaseType}/{Category} record(s) logged for {Students} student(s) in branch {BranchId}",
            batchId, recordIds.Count, caseType, category.Name, ids.Count, branchId);

        // --- After the commit: nothing below may fail the request. ---
        var peopleAlerted = await _alerts.NotifyRecordsLoggedAsync(recordIds);

        // ONE award for ONE act of filing. Crediting per record would let a single press on a class of forty
        // move a member of staff's score forty times, which is a lever nobody should be handed.
        await _systemAwards.CreditAsync(organizationId, branchId, CurrentUserId(), StaffSystemAwards.WelfareRecordFiled,
            $"Filed {category.Name} for a group of {ids.Count}. Credited automatically.");

        // ONE line on the welfare activity log for the batch, at the highest rung of what was written, naming no
        // child — who was in it is in the DetailJson, which no endpoint returns.
        var rung = built.Max(b => b.Record.Visibility);
        var kind = caseType == WelfareCaseType.Behavior ? "behaviour" : "achievement";
        var what = request.OneIncident
            ? $"one {kind} incident ({category.Name}) involving {ids.Count} students"
            : $"{category.Name} ({kind}) for {ids.Count} student{(ids.Count == 1 ? "" : "s")}";
        await _activity.RecordAsync(WelfareActivityActions.RecordsBulkLogged, "welfare-record-batch", batchId, null,
            "Logged " + what,
            new { BatchId = batchId, CaseType = caseType.ToString(), Category = category.Name, request.OneIncident, Students = ids, RecordIds = recordIds },
            branchId, organizationId, visibility: rung);

        return StatusCode(StatusCodes.Status201Created, new BulkWelfareRecordResultDto
        {
            Created = recordIds.Count,
            Students = ids.Count,
            RecordIds = recordIds,
            BatchId = batchId,
            PeopleAlerted = peopleAlerted
        });
    }

    private static IActionResult FieldProblem(string field, string message)
        => new BadRequestObjectResult(new ValidationProblemDetails(new Dictionary<string, string[]> { [field] = new[] { message } })
        {
            Title = message,
            Status = StatusCodes.Status400BadRequest
        });

    /// <summary>
    /// The ONE refusal for a student the caller may not write about. Deliberately the same for an unknown
    /// student and an out-of-scope one: an out-of-scope child must read as one that is not there, or the
    /// error itself confirms the student exists.
    /// </summary>
    private static IActionResult StudentNotFound()
        => new BadRequestObjectResult(new ProblemDetails
        {
            Title = "Student not found",
            Detail = "The selected student does not exist in this branch, or is no longer active.",
            Status = StatusCodes.Status400BadRequest
        });

    /// <summary>A record that has passed every rule and has not been saved.</summary>
    private sealed record BuiltWelfareRecord(WelfareRecord Record, Student Student, WelfareCategory Category);

    private bool? _subjectTeachersMayLogConcerns;

    /// <summary>
    /// The student a record may be filed against, or null. A class-scoped caller may only file against a
    /// student they hold. Reads were scoped from Phase 77 but the single create's WRITE was not, so a form
    /// tutor could log a safeguarding record against any child in the school by ID — and learn their name
    /// from the response.
    /// REPORTING A CONCERN IS NOT READING ONE (duty rota plan §5.3, CPOMS's model): a SUBJECT teacher may log
    /// a concern about a student they teach when the tenant allows it (default on). The pastoral scope still
    /// governs everything they could READ; the author rule on GetRecord gives back only this record.
    /// </summary>
    private async Task<(Student? Student, bool ByTeachingTier)> ResolveStudentForWriteAsync(Guid branchId, Guid studentId)
    {
        var student = await _context.Students.FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId && s.IsActive);
        if (student == null) return (null, false);
        if (await _scope.CanSeeStudentAsync(branchId, student.Id)) return (student, false);

        if (await _scope.GetTierAsync(branchId, student.Id) != StudentAccessTier.Teaching) return (null, false);
        _subjectTeachersMayLogConcerns ??= (await _staffPolicy.GetAsync(await ResolveOrganizationIdAsync(branchId))).SubjectTeachersMayLogConcerns;
        return _subjectTeachersMayLogConcerns.Value ? (student, true) : (null, false);
    }

    /// <summary>
    /// EVERY RULE ONE WELFARE RECORD OBEYS, in one place: the student and its scope, the category, the linked
    /// students, the content, late entry, points, the graduated response, visibility — and the entity built from
    /// them. Writes nothing. The single create and the group log both call it, so a rule added here binds both.
    /// <paramref name="resolvedStudent"/> lets a caller that has already run <see cref="ResolveStudentForWriteAsync"/>
    /// for this student skip running it twice.
    /// </summary>
    private async Task<(IActionResult? Error, BuiltWelfareRecord? Built)> BuildRecordAsync(
        Guid branchId, CreateWelfareRecordRequest request, bool acknowledgeLateEntry,
        (Student Student, bool ByTeachingTier)? resolvedStudent = null)
    {
        static IActionResult Fail(string title, string? detail = null)
            => new BadRequestObjectResult(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status400BadRequest });

        // --- Identity & ownership ---
        Student? student;
        bool concernByTeachingTier;
        if (resolvedStudent is { } pre) (student, concernByTeachingTier) = (pre.Student, pre.ByTeachingTier);
        else (student, concernByTeachingTier) = await ResolveStudentForWriteAsync(branchId, request.StudentId);
        if (student == null) return (StudentNotFound(), null);

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var category = await _context.WelfareCategories.FirstOrDefaultAsync(c => c.Id == request.CategoryId && c.OrganizationId == organizationId && c.IsActive);
        if (category == null)
            return (Fail("Category not found", "The selected category does not exist, or is no longer active."), null);

        if (category.CaseType != request.CaseType)
            // Article agreement matters here because the case-type names are shown verbatim and
            // "a Achievement record" is what an operator sees on a validation they hit often.
            return (Fail("Category does not match case type", $"'{category.Name}' is {Article(category.CaseType)} {category.CaseType} category and can't be used for {Article(request.CaseType)} {request.CaseType} record."), null);

        // --- Additional linked students (optional) — a fight or a group incident that touches
        // several students at once. StudentId above stays "who this was primarily filed against";
        // these just also get it on their own timeline. See WelfareRecord.AdditionalStudentIds.
        var additionalStudentIds = (request.AdditionalStudentIds ?? new List<Guid>())
            .Where(id => id != student.Id)
            .Distinct()
            .ToList();
        if (additionalStudentIds.Count > 0)
        {
            var validQuery = _context.Students.Where(s => additionalStudentIds.Contains(s.Id) && s.BranchId == branchId && s.IsActive);
            // Same scope rule as the primary student above. Without this, the linked-students list
            // is a side door onto exactly what the primary check refuses -- a record on the timeline
            // of any child in the branch.
            // A teaching-tier concern may link other students the author teaches (a fight in their lesson), and
            // no one else; every other caller links only students they hold pastorally.
            validQuery = concernByTeachingTier ? await _scope.ApplyAnyTierAsync(validQuery, branchId) : await _scope.ApplyAsync(validQuery, branchId);
            var validCount = await validQuery.CountAsync();
            if (validCount != additionalStudentIds.Count)
                return (Fail(AdditionalStudentsNotFoundTitle, "Every linked student must exist in this branch and be active."), null);
        }

        // --- Content ---
        var description = (request.Description ?? "").Trim();
        // A draft is a mobile quick-log left unfinished — exempt from the length/late-entry checks
        // a finished record isn't, so a teacher can save a half-typed thought and come back to it.
        // FinalizeRecord re-runs both checks for real once the author returns to complete it.
        if (!request.SaveAsDraft && description.Length < MinDescriptionLength)
            return (Fail("Description is too short", $"Describe what happened in at least {MinDescriptionLength} characters."), null);
        if (description.Length > MaxDescriptionLength)
            return (Fail("Description is too long", $"Keep the description under {MaxDescriptionLength} characters — use a follow-up note for more detail once the record exists."), null);

        var occurredAt = request.OccurredAt == default ? DateTime.UtcNow : request.OccurredAt.ToUniversalTime();
        if (occurredAt > DateTime.UtcNow.AddMinutes(5)) // small clock-skew allowance, not a loophole
            return (Fail("Date can't be in the future"), null);
        if (!request.SaveAsDraft && occurredAt < DateTime.UtcNow.AddDays(-LateEntryThresholdDays) && !acknowledgeLateEntry)
            return (Fail("This looks like a late entry",
                $"The date you entered is more than {LateEntryThresholdDays} days ago. If that's correct, resubmit with acknowledgeLateEntry=true."), null);

        var pointsError = ValidatePointsSign(request.CaseType, request.Points);
        if (pointsError != null) return (pointsError, null);

        // --- The graduated response ---
        if (request.ResponseStage is { } stage && !Enum.IsDefined(stage))
            return (Fail("Unrecognised response stage"), null);
        if (request.PerceivedFunction is { } fn && !Enum.IsDefined(fn))
            return (Fail("Unrecognised perceived function"), null);

        // An achievement is not a response to anything, so a stage on one is meaningless and
        // would pollute the very reports the stage exists to make possible.
        if (request.ResponseStage.HasValue && request.CaseType == WelfareCaseType.Achievement)
            return (Fail("An achievement has no response stage"), null);

        // A support plan IS the corrective rung — it needs an owner and a review date, because a
        // plan with neither is a sentence in a text box that nobody will ever come back to.
        if (request.CaseType == WelfareCaseType.SupportPlan)
        {
            if (request.AssignedToUserId is null || request.AssignedToUserId == Guid.Empty)
                return (Fail("A support plan needs an owner", "Assign the plan to the member of staff responsible for it."), null);
            if (request.ActionDueDate is null)
                return (Fail("A support plan needs a review date", "Set the date the plan will be reviewed — assess, plan, do, review."), null);
        }

        if (request.ActionDueDate is { } due && due.Date < DateTime.UtcNow.Date.AddDays(-1))
            return (Fail("A review date in the past would be overdue immediately"), null);

        if (request.AssignedToUserId is { } assignee && assignee != Guid.Empty)
        {
            var assigneeExists = await _context.Users.AnyAsync(u => u.Id == assignee && u.OrganizationId == organizationId && u.IsActive);
            if (!assigneeExists)
                return (Fail("The assigned member of staff was not found"), null);
        }

        // --- Visibility: server wins, never trusted from the client ---
        //
        // A safeguarding case is forced to at least Confidential regardless of what was sent, which
        // is exactly what the old Confidential bool did. What is NEW is that a caller may ASK for a
        // higher rung — but only one they hold the permission for, otherwise they could file a
        // record into a tier they cannot then read, which is a way to hide something from everyone
        // including themselves.
        var requested = request.Visibility;
        if (!Enum.IsDefined(requested))
            return (Fail("Unrecognised visibility"), null);

        if (requested == WelfareVisibility.Restricted && !await CanViewRestrictedAsync())
            return (Fail("You cannot mark a record restricted",
                "Restricted records are administrator-only. Log it normally and ask an administrator to restrict it."), null);

        if (requested == WelfareVisibility.Confidential && !await CanViewConfidentialAsync()
            && request.CaseType != WelfareCaseType.Welfare)
            return (Fail("You cannot mark a record confidential",
                "Log it as a Welfare concern instead — those are made confidential automatically."), null);

        var visibility = request.CaseType == WelfareCaseType.Welfare && requested < WelfareVisibility.Confidential
            ? WelfareVisibility.Confidential
            : requested;

        var record = new WelfareRecord
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            StudentId = student.Id,
            CategoryId = category.Id,
            CaseType = request.CaseType,
            Tier = request.Tier,
            Points = request.Points,
            Description = description,
            Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            OccurredAt = occurredAt,
            // Phase 2: case workflow is real now — a finished record starts Open, not Resolved,
            // so staff can actually move it through Under review / Action taken / Resolved.
            Status = request.SaveAsDraft ? WelfareStatus.Draft : WelfareStatus.Open,
            Visibility = visibility,
            AdditionalStudentIds = additionalStudentIds.Count > 0 ? additionalStudentIds.ToArray() : null,
            ReportedByUserId = CurrentUserId(),
            CreatedBy = CurrentUserId(),

            ResponseStage = request.CaseType == WelfareCaseType.Achievement ? null : request.ResponseStage,
            Antecedent = string.IsNullOrWhiteSpace(request.Antecedent) ? null : request.Antecedent.Trim(),
            PerceivedFunction = request.PerceivedFunction,
            ActionTaken = string.IsNullOrWhiteSpace(request.ActionTaken) ? null : request.ActionTaken.Trim(),
            AssignedToUserId = request.AssignedToUserId == Guid.Empty ? null : request.AssignedToUserId,
            ActionDueDate = request.ActionDueDate
        };

        return (null, new BuiltWelfareRecord(record, student, category));
    }

    /// <summary>
    /// Turns a Draft into a real record — re-runs the description-length and late-entry checks
    /// CreateRecord skipped for it, and only then flips Status to Open. Restricted to the draft's
    /// own author (or SuperAdmin): a draft is explicitly "visible only to its author until
    /// finalized," so nobody else gets to finish someone else's half-written entry.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/welfare-records/{recordId:guid}/finalize")]
    [RequirePermission(Permissions.WelfareCreate)]
    [ProducesResponseType(typeof(WelfareRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FinalizeRecord(Guid branchId, Guid recordId, [FromQuery] bool acknowledgeLateEntry = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await _context.WelfareRecords.Include(r => r.Student).Include(r => r.Category)
            .FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);
        if (record == null || record.Status != WelfareStatus.Draft ||
            (record.ReportedByUserId != CurrentUserId() && !RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole)))
            return NotFound(new ProblemDetails { Title = "Draft not found", Status = StatusCodes.Status404NotFound });

        if (record.Description.Trim().Length < MinDescriptionLength)
            return BadRequest(new ProblemDetails { Title = "Description is too short", Detail = $"Describe what happened in at least {MinDescriptionLength} characters before finalizing.", Status = StatusCodes.Status400BadRequest });
        if (record.OccurredAt < DateTime.UtcNow.AddDays(-LateEntryThresholdDays) && !acknowledgeLateEntry)
            return BadRequest(new ProblemDetails
            {
                Title = "This looks like a late entry",
                Detail = $"The date on this draft is more than {LateEntryThresholdDays} days ago. If that's correct, resubmit with acknowledgeLateEntry=true.",
                Status = StatusCodes.Status400BadRequest
            });

        record.Status = WelfareStatus.Open;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        // This is the moment a draft becomes a real record, so it is the moment the class teacher
        // is told — CreateRecord deliberately alerts nobody for a draft.
        await _alerts.NotifyRecordLoggedAsync(record.Id);

        await _systemAwards.CreditAsync(record.OrganizationId, branchId, record.ReportedByUserId, StaffSystemAwards.WelfareRecordFiled,
            $"Filed a {record.Category?.Name ?? "welfare"} record. Credited automatically.");

        var userNames = await ResolveUserNamesAsync(new[] { record });
        var studentNames = await ResolveStudentNamesAsync(new[] { record });
        return Ok(MapToDto(record, userNames, new Dictionary<Guid, string>(), studentNames));
    }

    /// <summary>
    /// Sets the intervention/consequence and who owns following up — Phase 2's action-assignment,
    /// enhancing the existing WelfareRecord table with three nullable columns rather than a new
    /// admin-managed intervention-type table (see the welfare-plan §05). Fires an in-app
    /// notification to the new assignee via the notification pipe every other real-time event in
    /// this app already uses — not a new one.
    /// </summary>
    [HttpPatch("branches/{branchId:guid}/welfare-records/{recordId:guid}/action")]
    [RequirePermission(Permissions.WelfareEdit)]
    [ProducesResponseType(typeof(WelfareRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAction(Guid branchId, Guid recordId, [FromBody] UpdateWelfareActionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await _context.WelfareRecords.Include(r => r.Student).Include(r => r.Category)
            .FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);
        if (record == null || !await CanSeeAsync(record.Visibility) || !await _scope.CanSeeStudentAsync(branchId, record.StudentId))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        if (request.AssignedToUserId.HasValue)
        {
            var assigneeValid = await _context.Users.AnyAsync(u => u.Id == request.AssignedToUserId.Value && u.OrganizationId == organizationId && u.IsActive);
            if (!assigneeValid)
                return BadRequest(new ProblemDetails { Title = "Assignee not found", Detail = "The selected staff member does not exist in this organization, or is no longer active.", Status = StatusCodes.Status400BadRequest });
        }

        var previousAssignee = record.AssignedToUserId;
        record.ActionTaken = string.IsNullOrWhiteSpace(request.ActionTaken) ? null : request.ActionTaken.Trim();
        record.AssignedToUserId = request.AssignedToUserId;
        // A due date is a calendar date, not a specific instant — DateTimeKind.Utc stamped
        // directly (not ToUniversalTime(), which would shift the clock time) so Npgsql doesn't
        // reject the Kind=Unspecified value System.Text.Json deserializes by default against this
        // timestamptz column. Same bug class as the Phase 59 fix already recorded in this
        // project's TASK_TRACKER.md — caught live here via the identical DbUpdateException.
        record.ActionDueDate = request.ActionDueDate.HasValue
            ? DateTime.SpecifyKind(request.ActionDueDate.Value, DateTimeKind.Utc)
            : null;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        if (request.AssignedToUserId.HasValue && request.AssignedToUserId != previousAssignee && request.AssignedToUserId != CurrentUserId())
        {
            var dueText = request.ActionDueDate.HasValue ? string.Create(CultureInfo.InvariantCulture, $" — due {request.ActionDueDate.Value:MMM d}") : "";
            await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = request.AssignedToUserId,
                BranchId = branchId,
                OrganizationId = organizationId,
                Title = "Welfare follow-up assigned to you",
                Message = $"{record.Student?.FullName ?? "A student"}'s \"{record.Category?.Name ?? "record"}\"{dueText}",
                Type = NotificationType.Custom,
                Priority = NotificationPriority.Normal,
                ActionUrl = $"/admin/students/{record.StudentId}/welfare"
            });
        }

        var userNames = await ResolveUserNamesAsync(new[] { record });
        var studentNames = await ResolveStudentNamesAsync(new[] { record });
        return Ok(MapToDto(record, userNames, new Dictionary<Guid, string>(), studentNames));
    }

    /// <summary>
    /// Moves a record through Open → Under review → Action taken → Resolved. Draft is deliberately
    /// excluded here — a draft only ever leaves that state via FinalizeRecord, never a generic
    /// status PATCH, so "still a draft" and "workflow status" stay two different questions.
    /// </summary>
    [HttpPatch("branches/{branchId:guid}/welfare-records/{recordId:guid}/status")]
    [RequirePermission(Permissions.WelfareEdit)]
    [ProducesResponseType(typeof(WelfareRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(Guid branchId, Guid recordId, [FromBody] UpdateWelfareStatusRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (request.Status == WelfareStatus.Draft)
            return BadRequest(new ProblemDetails { Title = "Draft is not a settable status", Detail = "A record leaves Draft only via the finalize action.", Status = StatusCodes.Status400BadRequest });

        var record = await _context.WelfareRecords.Include(r => r.Student).Include(r => r.Category)
            .FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);
        if (record == null || !await CanSeeAsync(record.Visibility) || !await _scope.CanSeeStudentAsync(branchId, record.StudentId))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });
        if (record.Status == WelfareStatus.Draft)
            return BadRequest(new ProblemDetails { Title = "This record is still a draft", Detail = "Finalize it first before changing its workflow status.", Status = StatusCodes.Status400BadRequest });

        record.Status = request.Status;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        var userNames = await ResolveUserNamesAsync(new[] { record });
        var studentNames = await ResolveStudentNamesAsync(new[] { record });
        return Ok(MapToDto(record, userNames, new Dictionary<Guid, string>(), studentNames));
    }

    /// <summary>
    /// Moves a record between visibility rungs — Standard / Confidential / Restricted.
    ///
    /// This is the ONE mutable field on an otherwise append-only record, and deliberately so: a
    /// record whose sensitivity is only understood later must be raisable in place, and the
    /// alternative (log a second record at the right level and leave the first one readable) would
    /// be worse in every way.
    ///
    /// Three rules the endpoint enforces, each closing a hole:
    ///  - You must be able to see the record's CURRENT level to change it. Otherwise a caller could
    ///    guess an ID and downgrade a safeguarding record they cannot read.
    ///  - You must hold the permission for the level you are moving it TO. Otherwise you could file
    ///    something into a tier you cannot then read — hiding it from everyone including yourself.
    ///  - A Welfare (safeguarding) case cannot be lowered below Confidential at all. That floor is
    ///    forced at creation and this endpoint is not a way around it.
    ///
    /// Every change writes a WelfareNote saying who moved it, from what to what, and why — so the
    /// chronology still records it even though the field itself moved.
    /// </summary>
    [HttpPatch("branches/{branchId:guid}/welfare-records/{recordId:guid}/visibility")]
    [RequirePermission(Permissions.WelfareEdit)]
    [ProducesResponseType(typeof(WelfareRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateVisibility(Guid branchId, Guid recordId, [FromBody] UpdateWelfareVisibilityRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (!Enum.IsDefined(request.Visibility))
            return BadRequest(new ProblemDetails { Title = "Unrecognised visibility", Status = StatusCodes.Status400BadRequest });

        var record = await _context.WelfareRecords.Include(r => r.Student).Include(r => r.Category)
            .FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);

        // Must be able to see it as it stands — a 404 either way, so guessing an ID tells you nothing.
        if (record == null || !await CanSeeAsync(record.Visibility) || !await _scope.CanSeeStudentAsync(branchId, record.StudentId))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });

        if (record.Visibility == request.Visibility)
            return BadRequest(new ProblemDetails { Title = "No change", Detail = $"This record is already {request.Visibility}.", Status = StatusCodes.Status400BadRequest });

        // ...and must hold the permission for where it is going.
        if (!await CanSeeAsync(request.Visibility))
            return BadRequest(new ProblemDetails
            {
                Title = $"You cannot move a record to {request.Visibility}",
                Detail = "You would no longer be able to read it. Ask someone who holds that level to make the change.",
                Status = StatusCodes.Status400BadRequest
            });

        if (record.CaseType == WelfareCaseType.Welfare && request.Visibility < WelfareVisibility.Confidential)
            return BadRequest(new ProblemDetails
            {
                Title = "A safeguarding record cannot be made standard",
                Detail = "Welfare concerns are confidential by design. It can be raised to Restricted, but not lowered.",
                Status = StatusCodes.Status400BadRequest
            });

        var lowering = request.Visibility < record.Visibility;
        if (lowering && string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ProblemDetails
            {
                Title = "Say why you are widening who can read this",
                Detail = "Lowering a record's visibility lets more people read it. Give a reason for the record.",
                Status = StatusCodes.Status400BadRequest
            });

        var from = record.Visibility;
        record.Visibility = request.Visibility;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = CurrentUserId();

        // The chronology keeps carrying it even though the field itself moved.
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason!.Trim();
        _context.WelfareNotes.Add(new WelfareNote
        {
            RecordId = record.Id,
            AuthorUserId = CurrentUserId(),
            Kind = WelfareNoteKind.Note,
            Body = reason == null
                ? $"Visibility changed from {from} to {request.Visibility}."
                : $"Visibility changed from {from} to {request.Visibility}. Reason: {reason}"
        });

        await _context.SaveChangesAsync();

        _logger.LogInformation("Welfare record {RecordId} visibility {From} → {To} by user {UserId}",
            record.Id, from, request.Visibility, CurrentUserId());

        var vUserNames = await ResolveUserNamesAsync(new[] { record });
        var vStudentNames = await ResolveStudentNamesAsync(new[] { record });
        return Ok(MapToDto(record, vUserNames, new Dictionary<Guid, string>(), vStudentNames));
    }

    /// <summary>
    /// Refines a record's <b>interpretation</b>: what led up to it (<c>Antecedent</c>), what staff
    /// read it as achieving (<c>PerceivedFunction</c>), and what was done about it
    /// (<c>ResponseStage</c>).
    ///
    /// Until 2026-09-06 all three could be set only when the record was first created, and all
    /// three are optional there. Anything logged without them was stuck that way — there was no
    /// update endpoint on a welfare record at all — and that silently starved the two features
    /// built on them: the Student Picture's "What has been tried" ladder (ResponseStage) and
    /// GetPatterns' insights (Antecedent → "N share the same trigger", PerceivedFunction →
    /// "Staff read N of these as …"). Staff logging an incident in the moment routinely do not
    /// yet know any of this, so filling it in later is the normal case, not an edge one.
    ///
    /// <b>Why these three and nothing else.</b> The ledger is append-only by design — no DELETE,
    /// and the record of what happened is not rewritable. These fields are not that record: they
    /// are staff's reading of it, which is exactly the thing that legitimately changes as more is
    /// understood. The form itself says so, labelling PerceivedFunction "Your read, not a
    /// diagnosis." The factual account — Description, OccurredAt, Category, Tier, Points,
    /// Location, Confidential — stays immutable, and no endpoint here should start changing it.
    ///
    /// This is a full replace of those three, not a merge: the dialog loads the current values and
    /// posts all three back, so a save is always a complete statement of the interpretation and
    /// null genuinely means "clear this", matching the create form's clearable pickers.
    ///
    /// Deliberately unlike the status PATCH above, a Draft is NOT rejected. Status excludes drafts
    /// because leaving Draft is finalize's job alone; refining a draft is just the author working
    /// on their own unfinished note, which is what a draft is for.
    /// </summary>
    [HttpPatch("branches/{branchId:guid}/welfare-records/{recordId:guid}/interpretation")]
    [RequirePermission(Permissions.WelfareEdit)]
    [ProducesResponseType(typeof(WelfareRecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateInterpretation(Guid branchId, Guid recordId, [FromBody] UpdateWelfareInterpretationRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        // Same validation the create path applies to this field, so the two cannot disagree.
        if (request.PerceivedFunction is { } fn && !Enum.IsDefined(fn))
            return BadRequest(new ProblemDetails { Title = "Unknown perceived function", Status = StatusCodes.Status400BadRequest });

        var record = await _context.WelfareRecords.Include(r => r.Student).Include(r => r.Category)
            .FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);
        if (record == null || !await CanSeeAsync(record.Visibility) || !await _scope.CanSeeStudentAsync(branchId, record.StudentId))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });

        record.ResponseStage = request.ResponseStage;
        record.Antecedent = string.IsNullOrWhiteSpace(request.Antecedent) ? null : request.Antecedent.Trim();
        record.PerceivedFunction = request.PerceivedFunction;
        record.UpdatedAt = DateTime.UtcNow;
        record.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        _logger.LogInformation("Welfare record {RecordId} interpretation updated: response {Stage}, function {Function}",
            recordId, request.ResponseStage?.ToString() ?? "(cleared)", request.PerceivedFunction?.ToString() ?? "(cleared)");

        var userNames = await ResolveUserNamesAsync(new[] { record });
        var studentNames = await ResolveStudentNamesAsync(new[] { record });
        return Ok(MapToDto(record, userNames, new Dictionary<Guid, string>(), studentNames));
    }

    /// <summary>Every open (non-Resolved, non-Draft) record assigned to the caller — the staff-facing "what do I still owe follow-up on" view. Available at welfare.view, not gated behind reports.view, since this surfaces the caller's own responsibilities, not the branch's whole ledger.</summary>
    [HttpGet("branches/{branchId:guid}/welfare-records/my-actions")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(List<WelfareRecordDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyActions(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var callerId = CurrentUserId();
        var myActions = _context.WelfareRecords
            .Include(r => r.Student).Include(r => r.Category).Include(r => r.Notes).Include(r => r.Notifications)
            .Where(r => r.BranchId == branchId && r.AssignedToUserId == callerId
                && r.Status != WelfareStatus.Resolved && r.Status != WelfareStatus.Draft);

        // Already narrowed to the caller's own assignments, so this is belt-and-braces — but a
        // record can be assigned to someone whose scope no longer covers that student (a class
        // handover mid-term), and this is the endpoint where that would still surface.
        myActions = await ApplyStudentScopeAsync(myActions, branchId);

        var visibleActionLevels = await VisibleLevelsAsync();
        myActions = myActions.Where(r => visibleActionLevels.Contains(r.Visibility));

        var records = await myActions
            .OrderBy(r => r.ActionDueDate ?? DateTime.MaxValue)
            .ToListAsync();

        var userNames = await ResolveUserNamesAsync(records);
        var studentNames = await ResolveStudentNamesAsync(records);
        return Ok(records.Select(r => MapToDto(r, userNames, new Dictionary<Guid, string>(), studentNames)).ToList());
    }

    /// <summary>
    /// Branch-wide search/filter across every student's records — Phase 3, and the data behind the
    /// dashboard and CSV/PDF export. Gated at welfare.reports.view (Manager/Admin), not the plain
    /// welfare.view Staff already has for a single student's own timeline: seeing every student's
    /// non-confidential record in one searchable list is a materially bigger exposure than
    /// navigating to one student at a time via the roster, and this is exactly the "Manager
    /// reviews" half of the escalation path — Tier and Status filters below are how a Manager
    /// actually finds what needs review, not a separate escalation endpoint.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/welfare-records")]
    [RequirePermissionAny(Permissions.WelfareReportsView, Permissions.WelfareReportsOwn)]
    [ProducesResponseType(typeof(List<WelfareRecordDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchRecords(
        Guid branchId,
        [FromQuery] string? keyword = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] WelfareCaseType? caseType = null,
        [FromQuery] WelfareStatus? status = null,
        [FromQuery] WelfareTier? tier = null,
        [FromQuery] Guid? studentId = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.WelfareRecords
            .Include(r => r.Student).Include(r => r.Category).Include(r => r.Notes).Include(r => r.Notifications)
            .Where(r => r.BranchId == branchId && r.Status != WelfareStatus.Draft);

        // Row-level scope. This endpoint returns FULL record detail for every student in the
        // branch, so without this a class-scoped caller reads the whole school's chronology
        // through the reports page — the widest leak in this controller. Found by the live e2e:
        // the per-student reads were all scoped while the branch-wide ones were not.
        query = await ApplyStudentScopeAsync(query, branchId);

        var visibleLevels = await VisibleLevelsAsync();
        query = query.Where(r => visibleLevels.Contains(r.Visibility));
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(r => r.Description.Contains(keyword) || (r.ActionTaken != null && r.ActionTaken.Contains(keyword)));
        if (dateFrom.HasValue)
            query = query.Where(r => r.OccurredAt >= dateFrom.Value.ToUniversalTime());
        if (dateTo.HasValue)
            query = query.Where(r => r.OccurredAt <= dateTo.Value.ToUniversalTime());
        if (caseType.HasValue)
            query = query.Where(r => r.CaseType == caseType.Value);
        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value);
        if (tier.HasValue)
            query = query.Where(r => r.Tier == tier.Value);
        if (studentId.HasValue)
            query = query.Where(r => r.StudentId == studentId.Value || (r.AdditionalStudentIds != null && r.AdditionalStudentIds.Contains(studentId.Value)));

        var records = await query.OrderByDescending(r => r.OccurredAt).Take(500).ToListAsync();

        var userNames = await ResolveUserNamesAsync(records);
        var studentNames = await ResolveStudentNamesAsync(records);
        return Ok(records.Select(r => MapToDto(r, userNames, new Dictionary<Guid, string>(), studentNames)).ToList());
    }

    /// <summary>The Welfare Dashboard's numbers — category mix and the per-staff category distribution the equity/consistency-audit case (welfare-plan §03) argues a school should be able to check on its own process. Same permission as SearchRecords, since it's the same audience and the same underlying data.</summary>
    [HttpGet("branches/{branchId:guid}/welfare/summary")]
    [RequirePermissionAny(Permissions.WelfareReportsView, Permissions.WelfareReportsOwn)]
    [ProducesResponseType(typeof(WelfareSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(Guid branchId, [FromQuery] DateTime? dateFrom = null, [FromQuery] DateTime? dateTo = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.WelfareRecords.Include(r => r.Category)
            .Where(r => r.BranchId == branchId && r.Status != WelfareStatus.Draft);

        // Counts are data too. Unscoped, this told a class teacher how many incidents the whole
        // school has and how they break down by category and by member of staff.
        query = await ApplyStudentScopeAsync(query, branchId);

        var visibleLevels = await VisibleLevelsAsync();
        query = query.Where(r => visibleLevels.Contains(r.Visibility));
        if (dateFrom.HasValue)
            query = query.Where(r => r.OccurredAt >= dateFrom.Value.ToUniversalTime());
        if (dateTo.HasValue)
            query = query.Where(r => r.OccurredAt <= dateTo.Value.ToUniversalTime());

        var records = await query.ToListAsync();
        var now = DateTime.UtcNow;

        var byCategory = records.GroupBy(r => new { r.CategoryId, Name = r.Category?.Name ?? "Unknown", r.CaseType })
            .Select(g => new WelfareCategoryCountDto { CategoryName = g.Key.Name, CaseType = g.Key.CaseType, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ToList();

        var staffIds = records.Select(r => r.ReportedByUserId).Distinct().ToList();
        var staffNames = await _context.Users.Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, Name = PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName) })
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.Name) ? "Unknown" : u.Name);
        var byStaff = records.GroupBy(r => r.ReportedByUserId)
            .Select(g => new WelfareStaffCountDto { StaffName = staffNames.GetValueOrDefault(g.Key, "Unknown"), Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ToList();

        // Told to the client so the reports page can say whose figures these are. A class teacher
        // reading their own class's total as the school's is a wrong conclusion from a correct
        // query, and nothing on the page said otherwise.
        var scopedClasses = (await _scope.IsUnscopedAsync())
            ? new List<string>()
            : (await _scope.GetPastoralClassNamesAsync(branchId)).ToList(); // welfare reports stay pastoral (duty rota plan §5.3)

        return Ok(new WelfareSummaryDto
        {
            TotalRecords = records.Count,
            OpenActionsCount = records.Count(r => r.Status != WelfareStatus.Resolved),
            OverdueActionsCount = records.Count(r => r.Status != WelfareStatus.Resolved && r.ActionDueDate.HasValue && r.ActionDueDate.Value < now),
            ByCategory = byCategory,
            ByStaff = byStaff,
            ScopedToClasses = scopedClasses
        });
    }

    [HttpPost("branches/{branchId:guid}/welfare-records/{recordId:guid}/notes")]
    [RequirePermission(Permissions.WelfareEdit)]
    [ProducesResponseType(typeof(WelfareNoteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddNote(Guid branchId, Guid recordId, [FromBody] AddWelfareNoteRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await _context.WelfareRecords.FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);
        if (record == null || !await CanSeeAsync(record.Visibility) || !await _scope.CanSeeStudentAsync(branchId, record.StudentId))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });

        var body = (request.Body ?? "").Trim();
        if (body.Length < MinDescriptionLength)
            return BadRequest(new ProblemDetails { Title = "Note is too short", Detail = $"Notes need at least {MinDescriptionLength} characters.", Status = StatusCodes.Status400BadRequest });
        if (body.Length > MaxDescriptionLength)
            return BadRequest(new ProblemDetails { Title = "Note is too long", Status = StatusCodes.Status400BadRequest });

        var note = new WelfareNote
        {
            RecordId = recordId,
            Body = body,
            AuthorUserId = CurrentUserId(),
            Kind = request.Kind,
            // IsFinal/AttributedToName only mean anything for a Statement — silently ignored
            // (not rejected) on a plain Note, same tolerance the rest of this app shows a caller
            // who sends an irrelevant field rather than erroring on it.
            IsFinal = request.Kind == WelfareNoteKind.Statement && request.IsFinal,
            AttributedToName = request.Kind == WelfareNoteKind.Statement && !string.IsNullOrWhiteSpace(request.AttributedToName)
                ? request.AttributedToName.Trim() : null
        };
        _context.WelfareNotes.Add(note);
        await _context.SaveChangesAsync();

        var author = await _context.Users.Where(u => u.Id == note.AuthorUserId).Select(u => PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName)).FirstOrDefaultAsync();
        return CreatedAtAction(nameof(GetRecord), new { branchId, recordId }, new WelfareNoteDto
        {
            Id = note.Id,
            Body = note.Body,
            AuthorName = author?.Trim() ?? "Unknown",
            Kind = note.Kind,
            IsFinal = note.IsFinal,
            AttributedToName = note.AttributedToName,
            CreatedAt = note.CreatedAt
        });
    }

    // ---------------------------------------------------------------------
    // Historical-records import — the roster import's job table, processor, progress channel
    // and history UI, reused wholesale with Kind=Welfare (see RosterImportKind)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Starts a background import of historical welfare records (a school backfilling its
    /// previous system's ledger) and returns 202 immediately — identical shape to
    /// StudentsController.StartImport: rows are parsed client-side (rosterImport.js, kind
    /// "welfare"), the job row stashes them, and RosterImportProcessorJob does the per-row
    /// validation/matching, logging every row's outcome to RosterImportJobEntry and broadcasting
    /// live progress over the same "RosterImportProgress" hub event. Poll/list via
    /// StudentsController.GetImportJobs with ?kind=Welfare.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/welfare-records/import-jobs")]
    [RequirePermission(Permissions.WelfareCreate)]
    [ProducesResponseType(typeof(RosterImportJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> StartImport(Guid branchId, [FromBody] StartWelfareImportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (request.Rows == null || request.Rows.Count == 0)
            return BadRequest(new ProblemDetails { Title = "No rows to import", Status = StatusCodes.Status400BadRequest });

        if (request.Rows.Count > MaxImportRows)
            return BadRequest(new ProblemDetails { Title = $"A single import is capped at {MaxImportRows:N0} rows — split larger files into batches", Status = StatusCodes.Status400BadRequest });

        // Every imported record is attributed (ReportedByUserId) to whoever ran the import — that
        // column is non-nullable, so an unattributable caller (API-key auth carries no user) can't
        // start one. The roster import tolerates this; a welfare record must always have a reporter.
        var userId = CurrentUserId();
        if (userId == Guid.Empty)
            return BadRequest(new ProblemDetails { Title = "Historical imports must be started by a signed-in user", Detail = "Imported records are attributed to the importing staff member; API-key callers can't be attributed.", Status = StatusCodes.Status400BadRequest });

        // A class-scoped caller cannot start one at all. The processor matches rows to students by
        // code and name inside a Hangfire worker, where IStudentScopeService — which reads the
        // caller's HTTP context — does not exist, so there is nowhere downstream to enforce the
        // row-level scope. Rather than let a bulk write bypass the scope every read respects, the
        // whole operation is refused here: a backfill of a school's previous ledger is an
        // administrative act, not a form tutor's.
        //
        // A 403 with a reason, not the 404 this controller uses elsewhere: there is no record whose
        // existence a 403 would confirm here — the caller's own role is the answer, and they need
        // to be told which so they ask an administrator rather than retrying the upload.
        if (!await _scope.IsUnscopedAsync())
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "A class teacher cannot bulk-import welfare records",
                Detail = "An import writes records for any student in the file, which would reach beyond your classes. Ask an administrator to run the backfill.",
                Status = StatusCodes.Status403Forbidden
            });

        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var job = new RosterImportJob
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CreatedByUserId = userId,
            SourceFileName = request.SourceFileName,
            Source = "admin_ui",
            Kind = RosterImportKind.Welfare,
            Status = RosterImportStatus.Pending,
            TotalRows = request.Rows.Count,
            RowsJson = JsonSerializer.Serialize(request.Rows)
        };
        _context.RosterImportJobs.Add(job);
        await _context.SaveChangesAsync();

        BackgroundJob.Enqueue<RosterImportProcessorJob>(j => j.ProcessAsync(job.Id));

        _logger.LogInformation("Welfare history import job {JobId} queued for branch {BranchId}: {Rows} rows from {File}",
            job.Id, branchId, job.TotalRows, job.SourceFileName ?? "(unnamed)");

        return Accepted(StudentsController.MapToDto(job));
    }

    /// <summary>
    /// Who this timeline is about, plus the branch's intervention suggestions — everything the
    /// welfare timeline needs that it used to take from the roster.
    /// </summary>
    /// <remarks>
    /// See <see cref="WelfareTimelineContextDto"/> for why this is gated at <c>welfare.view</c>
    /// and for what it refuses to carry. In short: the timeline endpoint next door already hands
    /// a plain <c>welfare.view</c> caller the student's name on every record, so requiring
    /// <c>students.view</c> to put that same name in the page header withheld nothing and broke
    /// the page. Enumerating the branch stays a roster capability.
    /// </remarks>
    [HttpGet("branches/{branchId:guid}/students/{studentId:guid}/welfare-context")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(WelfareTimelineContextDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTimelineContext(Guid branchId, Guid studentId)
    {
        // VerifyStudentAccess, not VerifyBranchOwnership. This is a per-student endpoint and it
        // hands back the child's name, code and class, so on the branch-only guard a class teacher
        // could read the identity of any student in the school by ID. Same 404-not-403 shape as the
        // rest of the per-student surface: an out-of-scope student must read as one that is not there.
        var accessError = await VerifyStudentAccess(branchId, studentId);
        if (accessError != null) return accessError;

        // Inactive students are included on purpose: a child who has left still has a ledger, and
        // a safeguarding record does not stop being readable because the roster row was retired.
        var student = await _context.Students
            .AsNoTracking()
            .Where(s => s.Id == studentId && s.BranchId == branchId)
            .Select(s => new { s.Id, s.FullName, s.StudentCode, s.ClassName, s.IsActive })
            .FirstOrDefaultAsync();

        if (student == null) return NotFound();

        var settingsJson = await _context.Branches
            .Where(b => b.Id == branchId)
            .Select(b => b.Settings)
            .FirstOrDefaultAsync();

        return Ok(new WelfareTimelineContextDto
        {
            StudentId = student.Id,
            FullName = student.FullName,
            StudentCode = student.StudentCode,
            ClassName = student.ClassName,
            IsActive = student.IsActive,
            // Read through StudentsController's own parser rather than a second copy of it — the
            // vocabulary lives in one place in Branch.Settings and is parsed in one place too.
            ActionsTaken = StudentsController.ReadVocabularies(settingsJson).ActionsTaken
        });
    }

    /// <summary>
    /// Welfare-scoped history for the imports the endpoint above starts.
    ///
    /// These three reads exist because the welfare page was calling StudentsController's copies,
    /// which are gated on <c>students.view</c> — so a welfare-reports user without it saw an empty
    /// history rather than the jobs they had just run themselves. The permission attribute cannot
    /// express "students.view OR welfare.view", and widening the roster endpoints would hand every
    /// welfare user the roster's import log too. A welfare-scoped copy is the narrower answer: it
    /// is gated on <c>welfare.view</c> and hard-filtered to <see cref="RosterImportKind.Welfare"/>,
    /// so it can only ever return the jobs this controller creates. Mapping is shared with
    /// StudentsController rather than re-written here.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/welfare-records/import-jobs")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(List<RosterImportJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImportJobs(Guid branchId, [FromQuery] int limit = 50)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.RosterImportJobs
            .Where(j => j.BranchId == branchId && j.Kind == RosterImportKind.Welfare);
        query = await ApplyImportJobScopeAsync(query);

        var jobs = await query
            .OrderByDescending(j => j.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync();

        return Ok(jobs.Select(StudentsController.MapToDto).ToList());
    }

    /// <summary>One welfare import job — what the page polls while a backfill is running.</summary>
    [HttpGet("branches/{branchId:guid}/welfare-records/import-jobs/{jobId:guid}")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(RosterImportJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImportJob(Guid branchId, Guid jobId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var jobQuery = _context.RosterImportJobs.Where(j =>
            j.Id == jobId && j.BranchId == branchId && j.Kind == RosterImportKind.Welfare);
        jobQuery = await ApplyImportJobScopeAsync(jobQuery);

        var job = await jobQuery.FirstOrDefaultAsync();
        if (job == null) return NotFound();
        return Ok(StudentsController.MapToDto(job));
    }

    /// <summary>Every row's outcome for one welfare import — the per-row log, not the counts.</summary>
    [HttpGet("branches/{branchId:guid}/welfare-records/import-jobs/{jobId:guid}/entries")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(List<RosterImportJobEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImportJobEntries(Guid branchId, Guid jobId,
        [FromQuery] RosterImportRowOutcome? outcome = null, [FromQuery] int limit = 500)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var existsQuery = _context.RosterImportJobs.Where(j =>
            j.Id == jobId && j.BranchId == branchId && j.Kind == RosterImportKind.Welfare);
        existsQuery = await ApplyImportJobScopeAsync(existsQuery);

        if (!await existsQuery.AnyAsync()) return NotFound();

        var query = _context.RosterImportJobEntries.Where(e => e.RosterImportJobId == jobId);
        if (outcome.HasValue) query = query.Where(e => e.Outcome == outcome.Value);

        var entries = await query
            .OrderBy(e => e.RowNumber)
            .Take(Math.Clamp(limit, 1, StudentsController.MaxImportEntriesPerPage))
            .ToListAsync();

        return Ok(entries.Select(StudentsController.MapToDto).ToList());
    }

    // ---------------------------------------------------------------------
    // Attachments — same IMediaStorageService every other upload in this app already uses
    // ---------------------------------------------------------------------

    [HttpPost("branches/{branchId:guid}/welfare-records/{recordId:guid}/attachments")]
    [RequirePermission(Permissions.WelfareCreate)]
    [RequestSizeLimit(MaxAttachmentSizeBytes)]
    [ProducesResponseType(typeof(WelfareAttachmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadAttachment(Guid branchId, Guid recordId, IFormFile file)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await _context.WelfareRecords.FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);
        if (record == null || !await CanSeeAsync(record.Visibility) || !await _scope.CanSeeStudentAsync(branchId, record.StudentId))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });

        if (file == null || file.Length == 0)
            return BadRequest(new ProblemDetails { Title = "No file was provided", Status = StatusCodes.Status400BadRequest });
        if (file.Length > MaxAttachmentSizeBytes)
            return BadRequest(new ProblemDetails { Title = $"File exceeds the {MaxAttachmentSizeBytes / 1024 / 1024}MB size limit", Status = StatusCodes.Status400BadRequest });

        var mimeType = file.ContentType ?? "";
        if (!AllowedAttachmentMimePrefixes.Any(p => mimeType.StartsWith(p)))
            return BadRequest(new ProblemDetails { Title = "Only images, PDF documents, video, or audio are accepted as evidence", Status = StatusCodes.Status400BadRequest });

        await using var uploadStream = file.OpenReadStream();
        var uploadResult = await _mediaStorage.UploadAsync(uploadStream, file.FileName, mimeType);
        if (!uploadResult.Success)
        {
            _logger.LogError("Welfare attachment upload failed for record {RecordId}: {Error}", recordId, uploadResult.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "Failed to store the file" });
        }

        var attachment = new WelfareAttachment
        {
            RecordId = recordId,
            FileUrl = uploadResult.FileUrl!,
            FileName = file.FileName,
            ContentType = mimeType,
            FileSizeBytes = file.Length,
            UploadedByUserId = CurrentUserId()
        };
        _context.WelfareAttachments.Add(attachment);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetRecord), new { branchId, recordId }, new WelfareAttachmentDto
        {
            Id = attachment.Id,
            FileUrl = QMgr.Infrastructure.Services.Storage.UploadLinks.Sign(attachment.FileUrl),
            FileName = attachment.FileName,
            ContentType = attachment.ContentType,
            FileSizeBytes = attachment.FileSizeBytes,
            CreatedAt = attachment.CreatedAt
        });
    }

    // ---------------------------------------------------------------------
    // Guardian notification — always reviewed, never automatic
    // ---------------------------------------------------------------------

    /// <summary>Returns an editable draft — the staff member reviews (and can edit) this before SendNotification actually fires it.</summary>
    [HttpGet("branches/{branchId:guid}/welfare-records/{recordId:guid}/notify-draft")]
    [RequirePermission(Permissions.WelfareNotify)]
    [ProducesResponseType(typeof(WelfareNotificationDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetNotificationDraft(Guid branchId, Guid recordId, [FromQuery] Guid guardianLinkId, [FromQuery] string channel = "Sms")
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await _context.WelfareRecords.Include(r => r.Category).Include(r => r.Student).Include(r => r.Branch)
            .FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);
        if (record == null || !await CanSeeAsync(record.Visibility) || !await _scope.CanSeeStudentAsync(branchId, record.StudentId))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });

        var guardian = await _context.StudentGuardians.Include(g => g.VisitorProfile)
            .FirstOrDefaultAsync(g => g.Id == guardianLinkId && g.StudentId == record.StudentId);
        if (guardian?.VisitorProfile == null)
            return NotFound(new ProblemDetails { Title = "Guardian not found", Status = StatusCodes.Status404NotFound });

        var isSms = channel.Equals("Sms", StringComparison.OrdinalIgnoreCase);
        var hasContact = isSms ? !string.IsNullOrWhiteSpace(guardian.VisitorProfile.Phone) : !string.IsNullOrWhiteSpace(guardian.VisitorProfile.Email);

        var verb = record.CaseType switch
        {
            WelfareCaseType.Achievement => "was recognized for",
            WelfareCaseType.Behavior => "was involved in an incident regarding",
            _ => "has a welfare note regarding"
        };
        var schoolName = record.Branch?.Name ?? "the school";
        var message = string.Create(CultureInfo.InvariantCulture,
                          $"Q-Mgr: {record.Student!.FullName} {verb} \"{record.Category!.Name}\" on {record.OccurredAt:MMM d} at {schoolName}. ") +
                      "Please contact the school office if you have any questions.";

        return Ok(new WelfareNotificationDraftDto
        {
            GuardianLinkId = guardian.Id,
            GuardianName = guardian.VisitorProfile.FullName,
            Channel = isSms ? "Sms" : "Email",
            SuggestedMessage = message,
            HasContactInfo = hasContact
        });
    }

    [HttpPost("branches/{branchId:guid}/welfare-records/{recordId:guid}/notify")]
    [RequirePermission(Permissions.WelfareNotify)]
    [ProducesResponseType(typeof(WelfareNotificationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendNotification(Guid branchId, Guid recordId, [FromBody] SendWelfareNotificationRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var record = await _context.WelfareRecords.FirstOrDefaultAsync(r => r.Id == recordId && r.BranchId == branchId);
        if (record == null || !await CanSeeAsync(record.Visibility) || !await _scope.CanSeeStudentAsync(branchId, record.StudentId))
            return NotFound(new ProblemDetails { Title = "Record not found", Status = StatusCodes.Status404NotFound });

        var guardian = await _context.StudentGuardians.Include(g => g.VisitorProfile)
            .FirstOrDefaultAsync(g => g.Id == request.GuardianLinkId && g.StudentId == record.StudentId);
        if (guardian?.VisitorProfile == null)
            return NotFound(new ProblemDetails { Title = "Guardian not found", Status = StatusCodes.Status404NotFound });

        var message = (request.Message ?? "").Trim();
        if (string.IsNullOrWhiteSpace(message))
            return BadRequest(new ProblemDetails { Title = "Message is required", Status = StatusCodes.Status400BadRequest });

        var isSms = request.Channel.Equals("Sms", StringComparison.OrdinalIgnoreCase);
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        bool success;

        // BUG-CLASS GUARD: a missing guardian contact must never look like a successful send —
        // distinct from "no SMS gateway configured" (NotificationService's own concern, which
        // logs and no-ops), this is "we don't even have a number/address to try."
        if (isSms)
        {
            if (string.IsNullOrWhiteSpace(guardian.VisitorProfile.Phone))
                return BadRequest(new ProblemDetails { Title = "No phone number on file", Detail = $"{guardian.VisitorProfile.FullName} has no phone number on file — add one before sending an SMS.", Status = StatusCodes.Status400BadRequest });
            success = await _notificationService.SendSmsAsync(organizationId, guardian.VisitorProfile.Phone, message);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(guardian.VisitorProfile.Email))
                return BadRequest(new ProblemDetails { Title = "No email address on file", Detail = $"{guardian.VisitorProfile.FullName} has no email address on file — add one before sending an email.", Status = StatusCodes.Status400BadRequest });
            success = await _notificationService.SendEmailAsync(organizationId, guardian.VisitorProfile.Email, "A note about your child from the school", message, isHtml: false);
        }

        var notification = new WelfareNotification
        {
            RecordId = recordId,
            GuardianVisitorProfileId = guardian.VisitorProfileId,
            Channel = isSms ? NotificationChannel.Sms : NotificationChannel.Email,
            Message = message,
            Success = success,
            SentByUserId = CurrentUserId()
        };
        _context.WelfareNotifications.Add(notification);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Welfare notification for record {RecordId} to guardian {GuardianId} via {Channel}: {Result}",
            recordId, guardian.VisitorProfileId, notification.Channel, success ? "sent" : "failed");

        // Looked up from the DB rather than trusted from a JWT claim — ClaimTypes.Name carries
        // the username here, not the display name, and every other "who did this" field in this
        // controller (ReportedByName, note AuthorName) is resolved the same DB-backed way.
        var senderName = await _context.Users.Where(u => u.Id == notification.SentByUserId)
            .Select(u => PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName)).FirstOrDefaultAsync();

        return CreatedAtAction(nameof(GetRecord), new { branchId, recordId }, new WelfareNotificationDto
        {
            Id = notification.Id,
            GuardianName = guardian.VisitorProfile.FullName,
            Channel = notification.Channel.ToString(),
            Message = notification.Message,
            Success = notification.Success,
            SentByName = string.IsNullOrWhiteSpace(senderName) ? "Unknown" : senderName,
            CreatedAt = notification.CreatedAt
        });
    }

    // ---------------------------------------------------------------------
    // Mapping helpers
    // ---------------------------------------------------------------------

    private async Task<Dictionary<Guid, string>> ResolveUserNamesAsync(IEnumerable<WelfareRecord> records)
    {
        var recordList = records.ToList();
        var userIds = recordList.SelectMany(r => new[] { r.ReportedByUserId }
                .Concat(r.AssignedToUserId.HasValue ? new[] { r.AssignedToUserId.Value } : Array.Empty<Guid>())
                .Concat(r.Notes.Select(n => n.AuthorUserId))
                .Concat(r.Notifications.Select(n => n.SentByUserId)))
            .Distinct()
            .ToList();

        return await _context.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, Name = PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName) })
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.Name) ? "Unknown" : u.Name);
    }

    private async Task<Dictionary<Guid, string>> ResolveGuardianNamesAsync(IEnumerable<WelfareRecord> records)
    {
        var profileIds = records.SelectMany(r => r.Notifications.Select(n => n.GuardianVisitorProfileId))
            .Distinct()
            .ToList();

        return await _context.VisitorProfiles
            .Where(v => profileIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.FullName);
    }

    /// <summary>Names for AdditionalStudentIds — display-only, so a fetch failure for a since-deleted student just falls back to omitting the name rather than the whole record.</summary>
    private async Task<Dictionary<Guid, string>> ResolveStudentNamesAsync(IEnumerable<WelfareRecord> records)
    {
        var studentIds = records.SelectMany(r => r.AdditionalStudentIds ?? Array.Empty<Guid>())
            .Distinct()
            .ToList();
        if (studentIds.Count == 0) return new Dictionary<Guid, string>();

        return await _context.Students
            .Where(s => studentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName);
    }

    private static WelfareRecordDto MapToDto(WelfareRecord r, Dictionary<Guid, string> userNames, Dictionary<Guid, string> guardianNames, Dictionary<Guid, string> studentNames) => new()
    {
        Id = r.Id,
        StudentId = r.StudentId,
        StudentName = r.Student?.FullName ?? "",
        CategoryId = r.CategoryId,
        CategoryName = r.Category?.Name ?? "",
        CategoryColor = r.Category?.Color,
        CaseType = r.CaseType,
        Tier = r.Tier,
        Points = r.Points,
        Description = r.Description,
        Location = r.Location,
        OccurredAt = r.OccurredAt,
        Status = r.Status,
        Visibility = r.Visibility,
        ReportedByName = userNames.GetValueOrDefault(r.ReportedByUserId, "Unknown"),
        CreatedAt = r.CreatedAt,
        ActionTaken = r.ActionTaken,
        AssignedToUserId = r.AssignedToUserId,
        AssignedToName = r.AssignedToUserId.HasValue ? userNames.GetValueOrDefault(r.AssignedToUserId.Value, "Unknown") : null,
        ActionDueDate = r.ActionDueDate,
        ResponseStage = r.ResponseStage,
        Antecedent = r.Antecedent,
        PerceivedFunction = r.PerceivedFunction,
        AdditionalStudentIds = (r.AdditionalStudentIds ?? Array.Empty<Guid>()).ToList(),
        AdditionalStudentNames = (r.AdditionalStudentIds ?? Array.Empty<Guid>()).Select(id => studentNames.GetValueOrDefault(id, "Unknown")).ToList(),
        // Evidence links are gated (UploadsController): the token minted here is what lets the
        // browser open a file this caller has just been allowed to see the record of.
        Attachments = r.Attachments.OrderBy(a => a.CreatedAt).Select(a => new WelfareAttachmentDto
        {
            Id = a.Id,
            FileUrl = QMgr.Infrastructure.Services.Storage.UploadLinks.Sign(a.FileUrl) ?? a.FileUrl,
            FileName = a.FileName,
            ContentType = a.ContentType,
            FileSizeBytes = a.FileSizeBytes,
            CreatedAt = a.CreatedAt
        }).ToList(),
        Notes = r.Notes.OrderBy(n => n.CreatedAt).Select(n => new WelfareNoteDto
        {
            Id = n.Id,
            Body = n.Body,
            AuthorName = userNames.GetValueOrDefault(n.AuthorUserId, "Unknown"),
            Kind = n.Kind,
            IsFinal = n.IsFinal,
            AttributedToName = n.AttributedToName,
            CreatedAt = n.CreatedAt
        }).ToList(),
        Notifications = r.Notifications.OrderBy(n => n.CreatedAt).Select(n => new WelfareNotificationDto
        {
            Id = n.Id,
            GuardianName = guardianNames.GetValueOrDefault(n.GuardianVisitorProfileId, "Unknown"),
            Channel = n.Channel.ToString(),
            Message = n.Message,
            Success = n.Success,
            SentByName = userNames.GetValueOrDefault(n.SentByUserId, "Unknown"),
            CreatedAt = n.CreatedAt
        }).ToList()
    };

    // =========================================================================================
    // The graduated response: prevention reporting and the escalation check.
    // =========================================================================================

    /// <summary>
    /// Answers "what was tried before this?" for one student over a rolling window, so the form
    /// can ask before a punitive response is filed against a child nothing has been tried for.
    ///
    /// GUIDANCE, NEVER A BARRIER. It returns a flag and a sentence; the client shows a
    /// confirmation the user can accept. A member of staff dealing with a real emergency must not
    /// be argued with by a form, so nothing here blocks a save and no permission is required
    /// beyond the one needed to create the record in the first place.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/{studentId:guid}/escalation-check")]
    [RequirePermission(Permissions.WelfareCreate)]
    [ProducesResponseType(typeof(EscalationCheckDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEscalationCheck(Guid branchId, Guid studentId, [FromQuery] int windowDays = 120)
    {
        // Per-student, and the sentence it returns names the child and counts their prior responses,
        // so it needs the student guard rather than the branch one. "Guidance, never a barrier" is
        // about not blocking a save; it was never a reason to answer for a student out of scope.
        var accessError = await VerifyStudentAccess(branchId, studentId);
        if (accessError != null) return accessError;

        var student = await _context.Students.FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        // A term, roughly. Long enough that last month's restorative conversation still counts,
        // short enough that something from two years ago does not excuse escalating today.
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(windowDays, 7, 365));

        var stages = await _context.WelfareRecords
            .Where(r => r.StudentId == studentId && r.BranchId == branchId
                        && r.Status != WelfareStatus.Draft && r.OccurredAt >= since
                        && r.ResponseStage != null)
            .GroupBy(r => r.ResponseStage!.Value)
            .Select(g => new { Stage = g.Key, Count = g.Count() })
            .ToListAsync();

        int CountOf(WelfareResponseStage s) => stages.FirstOrDefault(x => x.Stage == s)?.Count ?? 0;

        var openPlans = await _context.WelfareRecords.CountAsync(r =>
            r.StudentId == studentId && r.BranchId == branchId &&
            r.CaseType == WelfareCaseType.SupportPlan &&
            r.Status != WelfareStatus.Resolved && r.Status != WelfareStatus.Draft);

        var preventive = CountOf(WelfareResponseStage.Preventive);
        var restorative = CountOf(WelfareResponseStage.Restorative);
        var corrective = CountOf(WelfareResponseStage.Corrective);
        var punitive = CountOf(WelfareResponseStage.Punitive);

        // Prompt only when nothing softer has been tried and no plan is running. A school that
        // has already tried the earlier rungs is not second-guessed.
        var nothingTried = restorative == 0 && corrective == 0 && preventive == 0 && openPlans == 0;

        // Short on purpose. A paragraph in a prompt is a paragraph nobody reads, and this one has
        // to be read in the second before somebody presses Save.
        var days = Math.Clamp(windowDays, 7, 365);
        var message = nothingTried
            ? $"No restorative, corrective or preventive response for {student.FullName} in {days} days."
            : $"Already tried: {restorative} restorative, {corrective} corrective, {preventive} preventive, {openPlans} open plan(s).";

        return Ok(new EscalationCheckDto
        {
            WouldPrompt = nothingTried,
            PreventiveCount = preventive,
            RestorativeCount = restorative,
            CorrectiveCount = corrective,
            PunitiveCount = punitive,
            OpenSupportPlans = openPlans,
            Message = message
        });
    }

    /// <summary>
    /// Cohort and disproportionality reporting. The uncomfortable one is deliberate: if boarders,
    /// or one sex, or one house are escalated to a punitive response faster for comparable
    /// incidents, only a report will ever show it.
    ///
    /// Gated at welfare.reports.view, NOT the plain welfare.view it carried until 2026-09-10. This
    /// is a branch-wide aggregate rendered only by the reports page, and it was the one report
    /// endpoint any welfare.view holder could call directly — the whole point of a separate
    /// reports permission is that seeing everyone's records in one view is a bigger exposure than
    /// visiting one student at a time. The row-level class scope below is a separate axis and does
    /// not substitute for the gate.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/welfare/cohorts")]
    [RequirePermissionAny(Permissions.WelfareReportsView, Permissions.WelfareReportsOwn)]
    [ProducesResponseType(typeof(WelfareCohortReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCohortReport(Guid branchId, [FromQuery] int days = 90)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 7, 730));

        var q = _context.WelfareRecords
            .Include(r => r.Student)
            .Where(r => r.BranchId == branchId && r.Status != WelfareStatus.Draft && r.OccurredAt >= since);

        // The confidentiality gate applies to reports exactly as it does to the timeline —
        // aggregate counts of safeguarding concerns are still safeguarding information. The same
        // reasoning applies to the row-level class scope: a cohort breakdown by house, sex and
        // fees status across the whole school is exactly the sort of thing a class-scoped role
        // should not be able to assemble.
        q = await ApplyStudentScopeAsync(q, branchId);

        var reportLevels = await VisibleLevelsAsync();
        q = q.Where(r => reportLevels.Contains(r.Visibility));

        var rows = await q.Select(r => new
        {
            r.Student!.House,
            r.Student.Residency,
            r.Student.Sex,
            r.Student.FeesStatus,
            r.ResponseStage,
            r.CaseType
        }).ToListAsync();

        static List<CohortSliceDto> Slice<TKey>(IEnumerable<dynamic> src, Func<dynamic, TKey> key, Func<TKey, string> label)
        {
            return src.GroupBy(r => key(r))
                .Select(g =>
                {
                    var total = g.Count();
                    var punitive = g.Count(r => r.ResponseStage == WelfareResponseStage.Punitive);
                    return new CohortSliceDto
                    {
                        Label = label(g.Key),
                        TotalRecords = total,
                        PunitiveCount = punitive,
                        PunitiveShare = total == 0 ? 0 : Math.Round(punitive * 100.0 / total, 1)
                    };
                })
                .OrderByDescending(s => s.TotalRecords)
                .ToList();
        }

        var report = new WelfareCohortReportDto
        {
            WindowDays = Math.Clamp(days, 7, 730),
            TotalRecords = rows.Count,
            ByHouse = Slice(rows, r => (string?)r.House, k => string.IsNullOrWhiteSpace(k) ? "No house set" : k!),
            ByResidency = Slice(rows, r => (StudentResidency?)r.Residency, k => k?.ToString() ?? "Not recorded"),
            BySex = Slice(rows, r => (PersonSex?)r.Sex, k => k?.ToString() ?? "Not recorded"),
            ByFeesStatus = Slice(rows, r => (StudentFeesStatus?)r.FeesStatus, k => k?.ToString() ?? "Not recorded")
        };

        return Ok(report);
    }

    /// <summary>
    /// The one-page student picture: flags, context, open plans, a twelve-month trend and the
    /// plain-language patterns a GROUP BY can see. What a house parent reads in ninety seconds
    /// before a difficult conversation.
    ///
    /// Assembled server-side in one call rather than leaving the page to fan out to five
    /// endpoints and stitch the answer together — and, more importantly, so the confidentiality
    /// gate is applied once, here, instead of five times in a Razor file.
    ///
    /// Reuses StudentsController's mapper rather than writing a second one, the same reasoning
    /// that already makes its RosterImportJob mapper internal: two mappers for one shape is how a
    /// field gets added to one and forgotten on the other.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/{studentId:guid}/picture")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(StudentPictureDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStudentPicture(Guid branchId, Guid studentId)
    {
        // VerifyStudentAccess, not VerifyBranchOwnership. This endpoint was the one per-student
        // read that kept the branch-only guard through the first pass of the scope work, and the
        // e2e caught it returning 200 for a student outside the caller's classes — the single
        // widest leak available, since the Student Picture assembles the pastoral tier, the flags,
        // the guardians and the recent chronology into one response.
        var accessError = await VerifyStudentAccess(branchId, studentId);
        if (accessError != null) return accessError;

        var student = await _context.Students
            .Include(s => s.Guardians).ThenInclude(g => g.VisitorProfile)
            .Include(s => s.Flags).ThenInclude(f => f.Category)
            .FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        var confidential = await CanViewConfidentialAsync();
        var restricted = await CanViewRestrictedAsync();
        var pictureLevels = await VisibleLevelsAsync();

        var query = _context.WelfareRecords
            .Include(r => r.Category)
            .Where(r => r.BranchId == branchId && r.Status != WelfareStatus.Draft
                        && (r.StudentId == studentId || (r.AdditionalStudentIds != null && r.AdditionalStudentIds.Contains(studentId))));

        query = query.Where(r => pictureLevels.Contains(r.Visibility));

        var records = await query.OrderByDescending(r => r.OccurredAt).ToListAsync();

        var now = DateTime.UtcNow;
        var openStatuses = new[] { WelfareStatus.Open, WelfareStatus.UnderReview, WelfareStatus.ActionTaken };

        // --- Twelve monthly buckets, oldest first. Built from a fixed calendar walk rather than
        // from whatever months happen to have records, so a quiet month renders as a gap in the
        // sparkline instead of silently collapsing the axis.
        var trend = new List<StudentTrendPointDto>();
        for (var i = 11; i >= 0; i--)
        {
            var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-i);
            var next = month.AddMonths(1);
            var inMonth = records.Where(r => r.OccurredAt >= month && r.OccurredAt < next).ToList();
            trend.Add(new StudentTrendPointDto
            {
                Year = month.Year,
                Month = month.Month,
                Label = month.ToString("MMM", CultureInfo.InvariantCulture),
                Achievements = inMonth.Count(r => r.CaseType == WelfareCaseType.Achievement),
                Behaviors = inMonth.Count(r => r.CaseType == WelfareCaseType.Behavior),
                Concerns = inMonth.Count(r => r.CaseType == WelfareCaseType.Welfare)
            });
        }

        var byStage = records.Where(r => r.ResponseStage.HasValue)
            .GroupBy(r => r.ResponseStage!.Value)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var userNames = await ResolveUserNamesAsync(records.Take(10));
        var studentNames = await ResolveStudentNamesAsync(records.Take(10));

        var dto = new StudentPictureDto
        {
            Student = StudentsController.MapToDto(student, pastoral: true, confidential: confidential, restricted: restricted),
            TotalRecords = records.Count,
            OpenActions = records.Count(r => openStatuses.Contains(r.Status) && r.AssignedToUserId.HasValue),
            OverdueActions = records.Count(r => openStatuses.Contains(r.Status) && r.ActionDueDate.HasValue && r.ActionDueDate.Value < now),
            OpenSupportPlans = records.Count(r => r.CaseType == WelfareCaseType.SupportPlan && openStatuses.Contains(r.Status)),
            AchievementCount = records.Count(r => r.CaseType == WelfareCaseType.Achievement),
            BehaviorCount = records.Count(r => r.CaseType == WelfareCaseType.Behavior),
            WelfareConcernCount = records.Count(r => r.CaseType == WelfareCaseType.Welfare),
            NetPoints = records.Sum(r => r.Points ?? 0),
            LastRecordAt = records.FirstOrDefault()?.OccurredAt,
            Trend = trend,
            ByResponseStage = byStage,
            Patterns = DetectPatterns(records),
            RecentRecords = records.Take(10).Select(r => MapToDto(r, userNames, new Dictionary<Guid, string>(), studentNames)).ToList()
        };

        return Ok(dto);
    }

    /// <summary>
    /// Plain observations from a GROUP BY — never a prediction, never a score, and deliberately
    /// phrased as counts a human can check rather than a judgement they have to trust. Three or
    /// more in one bucket is the threshold: two of anything is a coincidence.
    /// </summary>
    private static List<string> DetectPatterns(List<WelfareRecord> records)
    {
        var patterns = new List<string>();

        // Only behaviour and concerns — an achievement clustering on Fridays is not a problem to
        // surface to a house parent.
        var relevant = records
            .Where(r => r.CaseType == WelfareCaseType.Behavior || r.CaseType == WelfareCaseType.Welfare)
            .ToList();

        if (relevant.Count < 3) return patterns;

        var byLocation = relevant.Where(r => !string.IsNullOrWhiteSpace(r.Location))
            .GroupBy(r => r.Location!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (byLocation != null)
            patterns.Add($"{byLocation.Count()} of {relevant.Count} records are in the same place: {byLocation.Key}.");

        var byDay = relevant.GroupBy(r => r.OccurredAt.DayOfWeek)
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (byDay != null && byDay.Count() >= Math.Max(3, relevant.Count / 3))
            patterns.Add($"{byDay.Count()} fall on a {byDay.Key}.");

        var byHour = relevant.GroupBy(r => r.OccurredAt.Hour / 2)
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (byHour != null)
            patterns.Add($"{byHour.Count()} happen between {byHour.Key * 2:00}:00 and {byHour.Key * 2 + 2:00}:00.");

        var byAntecedent = relevant.Where(r => !string.IsNullOrWhiteSpace(r.Antecedent))
            .GroupBy(r => r.Antecedent!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (byAntecedent != null)
            patterns.Add($"{byAntecedent.Count()} share the same trigger: {byAntecedent.Key}.");

        var byFunction = relevant.Where(r => r.PerceivedFunction.HasValue && r.PerceivedFunction != WelfarePerceivedFunction.Unclear)
            .GroupBy(r => r.PerceivedFunction!.Value)
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (byFunction != null)
            patterns.Add($"Staff read {byFunction.Count()} of these as {byFunction.Key.ToString().ToLowerInvariant()}.");

        // The one that matters most for the user's actual question: escalation with nothing tried.
        var punitive = relevant.Count(r => r.ResponseStage == WelfareResponseStage.Punitive);
        var softer = relevant.Count(r => r.ResponseStage == WelfareResponseStage.Restorative
                                      || r.ResponseStage == WelfareResponseStage.Corrective
                                      || r.ResponseStage == WelfareResponseStage.Preventive);
        if (punitive >= 3 && softer == 0)
            patterns.Add($"All {punitive} recorded responses have been punitive — nothing restorative, corrective or preventive is on file.");

        return patterns;
    }

    // ---- Welfare activity log (2026-09-18) -----------------------------------------------------
    // WelfareController had no IActivityLogger at all, so publishing a named child's full welfare
    // chronology to the Document Library as a shareable PDF, and every CSV/XLSX export of the
    // records search, happened with nothing anywhere recording that it had. The MediaContent row
    // carries PublishedAt/PublishedByUserId, so the DOCUMENT is traceable — but nothing on the
    // welfare side said a child's file had left the ledger.
    //
    // Deliberately NOT routed through POST …/staff/activity/exports: that endpoint resolves every
    // Kind to a staff.* permission and writes an event about a MEMBER OF STAFF. The subject here is
    // a student, which is why ActivityEvent gained a nullable SubjectStudentId.

    /// <summary>
    /// Records an export or a Publish to Library that happened in the browser. The file is produced
    /// client-side (QDataExport, and reportPublish.js for the PDF), so the server never sees it — the
    /// page reports it here straight afterwards.
    ///
    /// The caller must hold the permission the exported thing itself needs, and a named student must
    /// be in their scope, so this cannot be used to write a line about a child the caller could not
    /// have exported. Out of scope answers 404, never 403 — a 403 confirms the student exists.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/welfare/activity/exports")]
    [RequirePermissionAny(Permissions.WelfareReportsView, Permissions.WelfareReportsOwn)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordWelfareExport(Guid branchId, [FromBody] RecordWelfareExportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var label = request.Kind switch
        {
            WelfareExportKinds.Records => "a welfare records search",
            WelfareExportKinds.Timeline => "a student welfare report",
            WelfareExportKinds.OpenActions => "the open welfare actions",
            _ => null
        };
        if (label == null)
            return BadRequest(new ProblemDetails { Title = "Unrecognised export kind", Status = StatusCodes.Status400BadRequest });

        // A student-specific export is checked against the class scope, not just the branch.
        string? subjectName = null;
        var visibility = WelfareVisibility.Standard;
        if (request.SubjectStudentId is { } studentId)
        {
            var scopeError = await VerifyStudentAccess(branchId, studentId);
            if (scopeError != null) return scopeError;

            var student = await _context.Students
                .Where(s => s.Id == studentId)
                .Select(s => new { s.FullName, s.RestrictedNotes })
                .FirstOrDefaultAsync();
            if (student == null)
                return NotFound(new ProblemDetails { Title = "Student not found", Status = StatusCodes.Status404NotFound });

            subjectName = student.FullName.Trim();

            // The rung of the thing the event is about, per the standing rule that any new event
            // about a record passes its rung. A chronology of a student who carries restricted notes
            // is itself confidential: a reader of the log should not learn from it what the export
            // contained.
            if (!string.IsNullOrWhiteSpace(student.RestrictedNotes)) visibility = WelfareVisibility.Confidential;
        }

        var format = string.IsNullOrWhiteSpace(request.Format) ? null : request.Format.Trim().ToUpperInvariant();
        var what = subjectName != null ? label + " for " + subjectName : label;
        var period = string.IsNullOrWhiteSpace(request.PeriodCaption) ? null : request.PeriodCaption.Trim();

        string summary;
        if (request.Published)
        {
            summary = "Published " + what + " to the Library";
            if (!string.IsNullOrWhiteSpace(request.DocumentName))
                summary += " as \"" + TruncateText(request.DocumentName.Trim(), 120) + "\"";
        }
        else
        {
            summary = "Exported " + what;
            if (format != null) summary += " as " + format;
            if (request.RowCount is { } rows) summary += " (" + rows + " row(s))";
        }
        if (period != null) summary += ", " + period;

        var action = request.Published
            ? WelfareActivityActions.ReportPublished
            : request.Kind == WelfareExportKinds.Timeline
                ? WelfareActivityActions.TimelineExported
                : WelfareActivityActions.ListExported;

        await _activity.RecordAsync(action, request.Kind, request.MediaContentId, null, summary,
            new { request.Kind, Format = format, request.RowCount, request.Published, request.MediaContentId, Period = period },
            branchId, organizationId, visibility: visibility, subjectStudentId: request.SubjectStudentId);

        return NoContent();
    }

    /// <summary>
    /// The welfare activity log: who exported or published what, about which child.
    ///
    /// Gated on <c>welfare.reports.view</c> or <c>welfare.reports.own</c> AND the class scope — a
    /// scoped caller sees only events about students in their own classes, plus their own
    /// subject-less actions. The staff log's gate (staff.records.view plus the staff scope) would be
    /// the wrong one entirely: this is a log about children.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/welfare/activity")]
    [RequirePermissionAny(Permissions.WelfareReportsView, Permissions.WelfareReportsOwn)]
    [ProducesResponseType(typeof(ActivityLogPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWelfareActivity(
        Guid branchId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;

        var welfareActions = new[]
        {
            WelfareActivityActions.ReportPublished,
            WelfareActivityActions.ListExported,
            WelfareActivityActions.TimelineExported,
            WelfareActivityActions.RecordsBulkLogged
        };

        var query = _context.ActivityEvents
            .Where(e => e.BranchId == branchId && welfareActions.Contains(e.Action));

        if (from.HasValue) query = query.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue) query = query.Where(e => e.OccurredAt <= to.Value);

        // The row scope. A scoped caller sees an event about a student only when that student is one
        // of theirs; an event with no student subject (a records-search export) is admitted only when
        // they are the actor, so a class teacher cannot read the whole school's export history.
        var allStudents = _context.Students.Where(s => s.BranchId == branchId);
        var scopedStudents = await _scope.ApplyAsync(allStudents, branchId);
        var isScoped = !ReferenceEquals(scopedStudents, allStudents);
        if (isScoped)
        {
            var me = CurrentUserId();
            var allowedIds = await scopedStudents.Select(s => s.Id).ToListAsync();
            query = query.Where(e => (e.SubjectStudentId != null && allowedIds.Contains(e.SubjectStudentId.Value))
                                     || (e.SubjectStudentId == null && e.ActorUserId == me));
        }

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id, e.ActorUserId, e.SubjectStudentId, e.Action, e.EntityType, e.EntityId,
                e.Summary, e.Visibility, e.IpAddress, e.UserAgent, e.OccurredAt
            })
            .ToListAsync();

        var actorIds = rows.Where(r => r.ActorUserId != null).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var actorNames = await _context.Users
            .Where(u => actorIds.Contains(u.Id))
            .Select(u => new { u.Id, Name = PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName) })
            .ToDictionaryAsync(u => u.Id, u => u.Name);

        var studentIds = rows.Where(r => r.SubjectStudentId != null).Select(r => r.SubjectStudentId!.Value).Distinct().ToList();
        var studentNames = await _context.Students
            .Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, Name = s.FullName })
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        return Ok(new ActivityLogPageDto
        {
            Items = rows.Select(r => new ActivityEventDto
            {
                Id = r.Id,
                ActorUserId = r.ActorUserId,
                ActorName = r.ActorUserId != null && actorNames.ContainsKey(r.ActorUserId.Value) ? actorNames[r.ActorUserId.Value] : null,
                SubjectUserId = r.SubjectStudentId,
                SubjectName = r.SubjectStudentId != null && studentNames.ContainsKey(r.SubjectStudentId.Value) ? studentNames[r.SubjectStudentId.Value] : null,
                Action = r.Action,
                EntityType = r.EntityType,
                EntityId = r.EntityId,
                Summary = r.Summary,
                Visibility = r.Visibility,
                IpAddress = r.IpAddress,
                UserAgent = r.UserAgent,
                OccurredAt = r.OccurredAt
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            CountsByAction = rows.GroupBy(r => r.Action).ToDictionary(g => g.Key, g => g.Count())
        });
    }

    /// <summary>Same shape as the staff controller's Truncate — a summary must fit its column.</summary>
    private static string TruncateText(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
