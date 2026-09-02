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
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

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
[RequireModule(ModuleCodes.VisitorSafeguarding)]
public class WelfareController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly INotificationService _notificationService;
    private readonly IMediaStorageService _mediaStorage;
    private readonly ILogger<WelfareController> _logger;

    // 25MB — bumped from the original 10MB to admit short video/audio evidence clips. Deliberately
    // not bumped further: this app's storage is local disk with no CDN tier (see the welfare-plan's
    // §05 file-upload section), and video at any real length is a genuinely different capacity
    // question than a phone photo — worth a real conversation with whoever owns the server before
    // going higher, not a silent scope add.
    private const long MaxAttachmentSizeBytes = 25 * 1024 * 1024;
    private const int MaxDescriptionLength = 2000;
    private const int MinDescriptionLength = 10;
    private const int MaxPoints = 100;
    private const int LateEntryThresholdDays = 14;
    private static readonly string[] AllowedAttachmentMimePrefixes = { "image/", "application/pdf", "video/", "audio/" };

    public WelfareController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        INotificationService notificationService,
        IMediaStorageService mediaStorage,
        ILogger<WelfareController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _notificationService = notificationService;
        _mediaStorage = mediaStorage;
        _logger = logger;
    }

    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails { Title = "Tenant not resolved", Status = StatusCodes.Status401Unauthorized });

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
    // copy of it. No in-memory caching here (unlike the handler) since this runs at most a
    // handful of times per request, not once per every [RequirePermission]-protected call.
    private async Task<bool> CanViewConfidentialAsync()
    {
        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole)) return true;

        var userId = CurrentUserId();
        if (userId == Guid.Empty) return false;

        return await _context.Users
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions)
            .AnyAsync(rp => rp.Permission.Code == Permissions.WelfareConfidentialView);
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

    private static IActionResult? ValidatePointsSign(WelfareCaseType caseType, int? points)
    {
        if (points == null) return null;

        if (Math.Abs(points.Value) > MaxPoints)
            return new BadRequestObjectResult(new ProblemDetails { Title = $"Points must be between -{MaxPoints} and {MaxPoints}", Status = StatusCodes.Status400BadRequest });

        return caseType switch
        {
            WelfareCaseType.Achievement when points < 0 => new BadRequestObjectResult(new ProblemDetails { Title = "Achievement points must be zero or positive", Status = StatusCodes.Status400BadRequest }),
            WelfareCaseType.Behavior when points > 0 => new BadRequestObjectResult(new ProblemDetails { Title = "Behavior points must be zero or negative", Status = StatusCodes.Status400BadRequest }),
            WelfareCaseType.Welfare when points != 0 => new BadRequestObjectResult(new ProblemDetails { Title = "Welfare concerns are not scored — leave points blank", Status = StatusCodes.Status400BadRequest }),
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
    public async Task<IActionResult> GetStudentRecords(Guid branchId, Guid studentId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

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

        if (!await CanViewConfidentialAsync())
            query = query.Where(r => !r.Confidential);
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
        // Same 404-not-403 shape everywhere else in this app: a confidential record — or someone
        // else's still-in-progress draft — reads identically to one that doesn't exist.
        if (record == null || (record.Confidential && !await CanViewConfidentialAsync()) ||
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

        // --- Identity & ownership ---
        var student = await _context.Students.FirstOrDefaultAsync(s => s.Id == request.StudentId && s.BranchId == branchId && s.IsActive);
        if (student == null)
            return BadRequest(new ProblemDetails { Title = "Student not found", Detail = "The selected student does not exist in this branch, or is no longer active.", Status = StatusCodes.Status400BadRequest });

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var category = await _context.WelfareCategories.FirstOrDefaultAsync(c => c.Id == request.CategoryId && c.OrganizationId == organizationId && c.IsActive);
        if (category == null)
            return BadRequest(new ProblemDetails { Title = "Category not found", Detail = "The selected category does not exist, or is no longer active.", Status = StatusCodes.Status400BadRequest });

        if (category.CaseType != request.CaseType)
            return BadRequest(new ProblemDetails { Title = "Category does not match case type", Detail = $"'{category.Name}' is a {category.CaseType} category and can't be used for a {request.CaseType} record.", Status = StatusCodes.Status400BadRequest });

        // --- Additional linked students (optional) — a fight or a group incident that touches
        // several students at once. StudentId above stays "who this was primarily filed against";
        // these just also get it on their own timeline. See WelfareRecord.AdditionalStudentIds.
        var additionalStudentIds = (request.AdditionalStudentIds ?? new List<Guid>())
            .Where(id => id != student.Id)
            .Distinct()
            .ToList();
        if (additionalStudentIds.Count > 0)
        {
            var validCount = await _context.Students.CountAsync(s => additionalStudentIds.Contains(s.Id) && s.BranchId == branchId && s.IsActive);
            if (validCount != additionalStudentIds.Count)
                return BadRequest(new ProblemDetails { Title = "One or more additional students not found", Detail = "Every linked student must exist in this branch and be active.", Status = StatusCodes.Status400BadRequest });
        }

        // --- Content ---
        var description = (request.Description ?? "").Trim();
        // A draft is a mobile quick-log left unfinished — exempt from the length/late-entry checks
        // a finished record isn't, so a teacher can save a half-typed thought and come back to it.
        // FinalizeRecord re-runs both checks for real once the author returns to complete it.
        if (!request.SaveAsDraft && description.Length < MinDescriptionLength)
            return BadRequest(new ProblemDetails { Title = "Description is too short", Detail = $"Describe what happened in at least {MinDescriptionLength} characters.", Status = StatusCodes.Status400BadRequest });
        if (description.Length > MaxDescriptionLength)
            return BadRequest(new ProblemDetails { Title = "Description is too long", Detail = $"Keep the description under {MaxDescriptionLength} characters — use a follow-up note for more detail once the record exists.", Status = StatusCodes.Status400BadRequest });

        var occurredAt = request.OccurredAt == default ? DateTime.UtcNow : request.OccurredAt.ToUniversalTime();
        if (occurredAt > DateTime.UtcNow.AddMinutes(5)) // small clock-skew allowance, not a loophole
            return BadRequest(new ProblemDetails { Title = "Date can't be in the future", Status = StatusCodes.Status400BadRequest });
        if (!request.SaveAsDraft && occurredAt < DateTime.UtcNow.AddDays(-LateEntryThresholdDays) && !acknowledgeLateEntry)
            return BadRequest(new ProblemDetails
            {
                Title = "This looks like a late entry",
                Detail = $"The date you entered is more than {LateEntryThresholdDays} days ago. If that's correct, resubmit with acknowledgeLateEntry=true.",
                Status = StatusCodes.Status400BadRequest
            });

        var pointsError = ValidatePointsSign(request.CaseType, request.Points);
        if (pointsError != null) return pointsError;

        // --- Confidentiality: server wins, never trusted from the client ---
        var confidential = request.CaseType == WelfareCaseType.Welfare;

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
            Confidential = confidential,
            AdditionalStudentIds = additionalStudentIds.Count > 0 ? additionalStudentIds.ToArray() : null,
            ReportedByUserId = CurrentUserId(),
            CreatedBy = CurrentUserId()
        };
        _context.WelfareRecords.Add(record);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Welfare record {RecordId} ({CaseType}/{Category}, {Status}) logged for student {StudentId} in branch {BranchId}",
            record.Id, record.CaseType, category.Name, record.Status, student.Id, branchId);

        record.Student = student;
        record.Category = category;
        var userNames = await ResolveUserNamesAsync(new[] { record });
        var studentNames = await ResolveStudentNamesAsync(new[] { record });
        return CreatedAtAction(nameof(GetRecord), new { branchId, recordId = record.Id }, MapToDto(record, userNames, new Dictionary<Guid, string>(), studentNames));
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
        if (record == null || (record.Confidential && !await CanViewConfidentialAsync()))
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
            var dueText = request.ActionDueDate.HasValue ? $" — due {request.ActionDueDate.Value:MMM d}" : "";
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
        if (record == null || (record.Confidential && !await CanViewConfidentialAsync()))
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

    /// <summary>Every open (non-Resolved, non-Draft) record assigned to the caller — the staff-facing "what do I still owe follow-up on" view. Available at welfare.view, not gated behind reports.view, since this surfaces the caller's own responsibilities, not the branch's whole ledger.</summary>
    [HttpGet("branches/{branchId:guid}/welfare-records/my-actions")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(List<WelfareRecordDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyActions(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var callerId = CurrentUserId();
        var records = await _context.WelfareRecords
            .Include(r => r.Student).Include(r => r.Category).Include(r => r.Notes).Include(r => r.Notifications)
            .Where(r => r.BranchId == branchId && r.AssignedToUserId == callerId
                && r.Status != WelfareStatus.Resolved && r.Status != WelfareStatus.Draft)
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
    [RequirePermission(Permissions.WelfareReportsView)]
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

        if (!await CanViewConfidentialAsync())
            query = query.Where(r => !r.Confidential);
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
    [RequirePermission(Permissions.WelfareReportsView)]
    [ProducesResponseType(typeof(WelfareSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(Guid branchId, [FromQuery] DateTime? dateFrom = null, [FromQuery] DateTime? dateTo = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.WelfareRecords.Include(r => r.Category)
            .Where(r => r.BranchId == branchId && r.Status != WelfareStatus.Draft);
        if (!await CanViewConfidentialAsync())
            query = query.Where(r => !r.Confidential);
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
            .Select(u => new { u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.Name) ? "Unknown" : u.Name);
        var byStaff = records.GroupBy(r => r.ReportedByUserId)
            .Select(g => new WelfareStaffCountDto { StaffName = staffNames.GetValueOrDefault(g.Key, "Unknown"), Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ToList();

        return Ok(new WelfareSummaryDto
        {
            TotalRecords = records.Count,
            OpenActionsCount = records.Count(r => r.Status != WelfareStatus.Resolved),
            OverdueActionsCount = records.Count(r => r.Status != WelfareStatus.Resolved && r.ActionDueDate.HasValue && r.ActionDueDate.Value < now),
            ByCategory = byCategory,
            ByStaff = byStaff
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
        if (record == null || (record.Confidential && !await CanViewConfidentialAsync()))
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

        var author = await _context.Users.Where(u => u.Id == note.AuthorUserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync();
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
        if (record == null || (record.Confidential && !await CanViewConfidentialAsync()))
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
            FileUrl = attachment.FileUrl,
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
        if (record == null || (record.Confidential && !await CanViewConfidentialAsync()))
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
        var message = $"Q-Mgr: {record.Student!.FullName} {verb} \"{record.Category!.Name}\" on {record.OccurredAt:MMM d} at {schoolName}. " +
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
        if (record == null || (record.Confidential && !await CanViewConfidentialAsync()))
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
            .Select(u => (u.FirstName + " " + u.LastName).Trim()).FirstOrDefaultAsync();

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
            .Select(u => new { u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
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
        Confidential = r.Confidential,
        ReportedByName = userNames.GetValueOrDefault(r.ReportedByUserId, "Unknown"),
        CreatedAt = r.CreatedAt,
        ActionTaken = r.ActionTaken,
        AssignedToUserId = r.AssignedToUserId,
        AssignedToName = r.AssignedToUserId.HasValue ? userNames.GetValueOrDefault(r.AssignedToUserId.Value, "Unknown") : null,
        ActionDueDate = r.ActionDueDate,
        AdditionalStudentIds = (r.AdditionalStudentIds ?? Array.Empty<Guid>()).ToList(),
        AdditionalStudentNames = (r.AdditionalStudentIds ?? Array.Empty<Guid>()).Select(id => studentNames.GetValueOrDefault(id, "Unknown")).ToList(),
        Attachments = r.Attachments.OrderBy(a => a.CreatedAt).Select(a => new WelfareAttachmentDto
        {
            Id = a.Id,
            FileUrl = a.FileUrl,
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
}
