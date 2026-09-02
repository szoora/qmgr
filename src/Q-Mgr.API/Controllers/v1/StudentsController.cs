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
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Jobs;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Visiting-day roster management: students and the guardians authorized to visit them. The same
/// bulk-import endpoint here serves two callers — the admin UI's own upload (JWT auth) and an
/// external School Management Information System pushing a roster sync (API-key auth, "roster:write"
/// scope, see PermissionAuthorizationHandler.ScopeToPermissions) — because there's no real
/// difference between "an admin uploaded a spreadsheet" and "a partner system pushed the same
/// shaped data" once it's parsed into rows; building two separate endpoints for that distinction
/// would just be two copies of the same upsert logic to keep in sync.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action already has its own [RequirePermission]
[RequireModule(ModuleCodes.VisitorSafeguarding)]
public class StudentsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;

    private const int SearchResultLimit = 10;

    public StudentsController(QMgrDbContext context, ITenantContextAccessor tenantAccessor)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
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

    private Guid? CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : null;
    }

    private const string ClassColorSettingsKey = "ClassColors";

    private static ClassColorSettingsDto ReadClassColorSettings(string? branchSettingsJson)
    {
        if (string.IsNullOrEmpty(branchSettingsJson)) return new ClassColorSettingsDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(branchSettingsJson);
            if (root != null && root.TryGetValue(ClassColorSettingsKey, out var element))
                return JsonSerializer.Deserialize<ClassColorSettingsDto>(element.GetRawText()) ?? new ClassColorSettingsDto();
        }
        catch (JsonException) { /* malformed settings blob — treat as not configured */ }
        return new ClassColorSettingsDto();
    }

    private static string WriteClassColorSettings(string? branchSettingsJson, ClassColorSettingsDto settings)
    {
        var merged = string.IsNullOrEmpty(branchSettingsJson)
            ? new Dictionary<string, object>()
            : (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(branchSettingsJson) ?? new())
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        merged[ClassColorSettingsKey] = settings;
        return JsonSerializer.Serialize(merged);
    }

    /// <summary>
    /// Admin-defined className-to-color map for the roster table and printed visiting-day
    /// passes — deliberately not auto-derived (e.g. hashing the name to a palette slot), since
    /// that would assign colors the admin never actually chose.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/class-colors")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(ClassColorSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetClassColors(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var settingsJson = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        return Ok(ReadClassColorSettings(settingsJson));
    }

    [HttpPut("branches/{branchId:guid}/students/class-colors")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(ClassColorSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateClassColors(Guid branchId, [FromBody] ClassColorSettingsDto request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId);
        if (branch == null) return NotFound();

        branch.Settings = WriteClassColorSettings(branch.Settings, request);
        branch.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(request);
    }

    /// <summary>
    /// Letterhead info for printed Student Visitation Cards / Visiting Day Passes — the tenant's
    /// real name/address/contact details, not the app's own branding. Address prefers this
    /// specific branch's own address over the organization's (a multi-campus tenant's branches
    /// can be in different places); falls back to the organization's if the branch has none set.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/print-letterhead")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(PrintLetterheadDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrintLetterhead(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var branch = await _context.Branches.Include(b => b.Organization).FirstOrDefaultAsync(b => b.Id == branchId);
        if (branch?.Organization == null) return NotFound();

        return Ok(new PrintLetterheadDto
        {
            OrganizationName = string.IsNullOrWhiteSpace(branch.Organization.BrandName) ? branch.Organization.Name : branch.Organization.BrandName,
            Address = string.IsNullOrWhiteSpace(branch.Address) ? branch.Organization.Address : branch.Address,
            ContactPhone = branch.Organization.ContactPhone,
            ContactEmail = branch.Organization.ContactEmail,
            LogoUrl = branch.Organization.LogoUrl
        });
    }

    // ---------------------------------------------------------------------
    // Student / Guardian CRUD
    // ---------------------------------------------------------------------

    [HttpGet("branches/{branchId:guid}/students")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(List<StudentDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStudents(Guid branchId, [FromQuery] bool includeInactive = false, [FromQuery] int limit = 100)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.Students.Include(s => s.Guardians).ThenInclude(g => g.VisitorProfile)
            .Where(s => s.BranchId == branchId);
        if (!includeInactive) query = query.Where(s => s.IsActive);

        var students = await query.OrderBy(s => s.FullName).Take(Math.Clamp(limit, 1, 500)).ToListAsync();
        return Ok(students.Select(MapToDto).ToList());
    }

    /// <summary>
    /// Combined student+guardian search for the visiting-day check-in flow — a search for "kamau"
    /// matches either side. One result row per (student, guardian) pair, same wildcard/case-
    /// insensitive matching as VisitorsController.SearchVisitorProfiles.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/search")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(List<StudentGuardianSearchResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchStudents(Guid branchId, [FromQuery] string q)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Ok(new List<StudentGuardianSearchResultDto>());

        var term = q.Trim();
        var lowerTerm = term.ToLowerInvariant();
        var normPhone = VisitorMatching.NormalizePhone(term);
        var normEmail = VisitorMatching.NormalizeEmail(term);

        var results = await _context.StudentGuardians
            .Include(g => g.Student)
            .Include(g => g.VisitorProfile)
            .Where(g => g.IsActive && g.Student!.BranchId == branchId && g.Student.IsActive)
            .Where(g =>
                g.Student!.FullName.ToLower().Contains(lowerTerm) ||
                (g.Student.StudentCode != null && g.Student.StudentCode.ToLower().Contains(lowerTerm)) ||
                g.VisitorProfile!.FullName.ToLower().Contains(lowerTerm) ||
                (normPhone != null && g.VisitorProfile.NormalizedPhone != null && g.VisitorProfile.NormalizedPhone.Contains(normPhone)) ||
                (normEmail != null && g.VisitorProfile.NormalizedEmail != null && g.VisitorProfile.NormalizedEmail.Contains(normEmail)))
            .OrderBy(g => g.Student!.FullName)
            .Take(SearchResultLimit)
            .Select(g => new StudentGuardianSearchResultDto
            {
                StudentId = g.StudentId,
                StudentName = g.Student!.FullName,
                StudentCode = g.Student.StudentCode,
                ClassName = g.Student.ClassName,
                GuardianProfileId = g.VisitorProfileId,
                GuardianName = g.VisitorProfile!.FullName,
                GuardianPhone = g.VisitorProfile.Phone,
                GuardianEmail = g.VisitorProfile.Email,
                Relationship = g.Relationship,
                GuardianIsWatchlisted = g.VisitorProfile.IsWatchlisted
            })
            .ToListAsync();

        if (results.Count > 0)
        {
            var today = DateTime.UtcNow.Date;
            var guardianProfileIds = results.Select(r => r.GuardianProfileId).Distinct().ToList();
            var checkInsToday = await _context.Visitors
                .Where(v => guardianProfileIds.Contains(v.VisitorProfileId) && v.DeletedAt == null
                    && v.CheckedInAt != null && v.CheckedInAt.Value.Date == today)
                .GroupBy(v => v.VisitorProfileId)
                .Select(g => new { ProfileId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.ProfileId, g => g.Count);

            results = results
                .Select(r => r with { CheckInsToday = checkInsToday.GetValueOrDefault(r.GuardianProfileId) })
                .ToList();
        }

        return Ok(results);
    }

    [HttpPost("branches/{branchId:guid}/students")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(StudentDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateStudent(Guid branchId, [FromBody] CreateStudentRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new ProblemDetails { Title = "Full name is required", Status = StatusCodes.Status400BadRequest });

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var code = string.IsNullOrWhiteSpace(request.StudentCode) ? null : request.StudentCode.Trim();

        if (code != null && await _context.Students.AnyAsync(s => s.OrganizationId == organizationId && s.StudentCode == code && s.IsActive))
            return Conflict(new ProblemDetails { Title = $"A student with code '{code}' already exists", Status = StatusCodes.Status409Conflict });

        var student = new Student
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            FullName = request.FullName.Trim(),
            StudentCode = code,
            ClassName = string.IsNullOrWhiteSpace(request.ClassName) ? null : request.ClassName.Trim()
        };
        _context.Students.Add(student);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetStudents), new { branchId }, MapToDto(student));
    }

    [HttpPut("branches/{branchId:guid}/students/{studentId:guid}")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(StudentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStudent(Guid branchId, Guid studentId, [FromBody] UpdateStudentRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var student = await _context.Students.Include(s => s.Guardians).ThenInclude(g => g.VisitorProfile)
            .FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        student.FullName = request.FullName.Trim();
        student.StudentCode = string.IsNullOrWhiteSpace(request.StudentCode) ? null : request.StudentCode.Trim();
        student.ClassName = string.IsNullOrWhiteSpace(request.ClassName) ? null : request.ClassName.Trim();
        student.IsActive = request.IsActive;
        await _context.SaveChangesAsync();

        return Ok(MapToDto(student));
    }

    [HttpDelete("branches/{branchId:guid}/students/{studentId:guid}")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateStudent(Guid branchId, Guid studentId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var student = await _context.Students.FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        // Deactivate, not delete — a student's visit history (Visitor.StudentId) should keep
        // reading correctly after they graduate/transfer, the same reasoning as Visitor's own
        // soft-delete.
        student.IsActive = false;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("branches/{branchId:guid}/students/{studentId:guid}/guardians")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(StudentGuardianDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddGuardian(Guid branchId, Guid studentId, [FromBody] AddGuardianRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var student = await _context.Students.FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new ProblemDetails { Title = "Guardian full name is required", Status = StatusCodes.Status400BadRequest });

        var normPhone = VisitorMatching.NormalizePhone(request.Phone);
        var normEmail = VisitorMatching.NormalizeEmail(request.Email);
        if (normPhone == null && normEmail == null)
            return BadRequest(new ProblemDetails { Title = "A guardian phone or email is required", Status = StatusCodes.Status400BadRequest });

        VisitorProfile? profile = null;
        if (normEmail != null)
            profile = await _context.VisitorProfiles.FirstOrDefaultAsync(p => p.OrganizationId == student.OrganizationId && p.DeletedAt == null && p.NormalizedEmail == normEmail);
        if (profile == null && normPhone != null)
            profile = await _context.VisitorProfiles.FirstOrDefaultAsync(p => p.OrganizationId == student.OrganizationId && p.DeletedAt == null && p.NormalizedPhone == normPhone);

        if (profile == null)
        {
            profile = new VisitorProfile
            {
                OrganizationId = student.OrganizationId,
                FullName = request.FullName.Trim(),
                Phone = request.Phone,
                NormalizedPhone = normPhone,
                Email = request.Email,
                NormalizedEmail = normEmail
            };
            _context.VisitorProfiles.Add(profile);
            await _context.SaveChangesAsync();
        }

        var existingLink = await _context.StudentGuardians.FirstOrDefaultAsync(g => g.StudentId == studentId && g.VisitorProfileId == profile.Id);
        if (existingLink != null)
            return Conflict(new ProblemDetails { Title = "This person is already listed as a guardian for this student", Status = StatusCodes.Status409Conflict });

        var link = new StudentGuardian
        {
            StudentId = studentId,
            VisitorProfileId = profile.Id,
            Relationship = string.IsNullOrWhiteSpace(request.Relationship) ? "Guardian" : request.Relationship.Trim(),
            IsActive = true
        };
        _context.StudentGuardians.Add(link);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetStudents), new { branchId }, new StudentGuardianDto
        {
            Id = link.Id,
            VisitorProfileId = profile.Id,
            FullName = profile.FullName,
            Phone = profile.Phone,
            Email = profile.Email,
            Relationship = link.Relationship
        });
    }

    [HttpDelete("branches/{branchId:guid}/students/{studentId:guid}/guardians/{guardianLinkId:guid}")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveGuardian(Guid branchId, Guid studentId, Guid guardianLinkId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var link = await _context.StudentGuardians.Include(g => g.Student)
            .FirstOrDefaultAsync(g => g.Id == guardianLinkId && g.StudentId == studentId && g.Student!.BranchId == branchId);
        if (link == null) return NotFound();

        _context.StudentGuardians.Remove(link);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // ---------------------------------------------------------------------
    // Bulk roster import — background job with real-time progress
    // ---------------------------------------------------------------------

    /// <summary>
    /// Starts a background import job and returns immediately — this is the endpoint both the
    /// admin UI's "Bulk Import" file upload and an external SMIS's roster sync call. Row parsing
    /// (Excel/CSV) happens client-side (or on the partner system's side); this always receives
    /// already-structured rows, never a raw file, which is also why there's no server-side Excel
    /// parsing dependency to add.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/students/import-jobs")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(RosterImportJobDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> StartImport(Guid branchId, [FromBody] StartRosterImportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (request.Rows == null || request.Rows.Count == 0)
            return BadRequest(new ProblemDetails { Title = "No rows to import", Status = StatusCodes.Status400BadRequest });

        if (request.Rows.Count > 10000)
            return BadRequest(new ProblemDetails { Title = "A single import is capped at 10,000 rows — split larger rosters into batches", Status = StatusCodes.Status400BadRequest });

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var isApiKey = User.FindFirst("auth_method")?.Value == "api_key";

        var job = new RosterImportJob
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CreatedByUserId = isApiKey ? null : CurrentUserId(),
            SourceFileName = request.SourceFileName,
            Source = isApiKey ? "api_sync" : "admin_ui",
            Status = RosterImportStatus.Pending,
            TotalRows = request.Rows.Count,
            RowsJson = JsonSerializer.Serialize(request.Rows)
        };
        _context.RosterImportJobs.Add(job);
        await _context.SaveChangesAsync();

        BackgroundJob.Enqueue<RosterImportProcessorJob>(j => j.ProcessAsync(job.Id));

        return AcceptedAtAction(nameof(GetImportJob), new { branchId, jobId = job.Id }, MapToDto(job));
    }

    [HttpGet("branches/{branchId:guid}/students/import-jobs")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(List<RosterImportJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImportJobs(Guid branchId, [FromQuery] int limit = 50)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var jobs = await _context.RosterImportJobs
            .Where(j => j.BranchId == branchId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync();

        return Ok(jobs.Select(MapToDto).ToList());
    }

    [HttpGet("branches/{branchId:guid}/students/import-jobs/{jobId:guid}")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(RosterImportJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImportJob(Guid branchId, Guid jobId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var job = await _context.RosterImportJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.BranchId == branchId);
        if (job == null) return NotFound();
        return Ok(MapToDto(job));
    }

    /// <summary>The per-row "logger" — every row's outcome, not just the summary counts.</summary>
    [HttpGet("branches/{branchId:guid}/students/import-jobs/{jobId:guid}/entries")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(List<RosterImportJobEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImportJobEntries(Guid branchId, Guid jobId, [FromQuery] RosterImportRowOutcome? outcome = null, [FromQuery] int limit = 500)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var jobExists = await _context.RosterImportJobs.AnyAsync(j => j.Id == jobId && j.BranchId == branchId);
        if (!jobExists) return NotFound();

        var query = _context.RosterImportJobEntries.Where(e => e.RosterImportJobId == jobId);
        if (outcome.HasValue) query = query.Where(e => e.Outcome == outcome.Value);

        var entries = await query.OrderBy(e => e.RowNumber).Take(Math.Clamp(limit, 1, 5000)).ToListAsync();

        return Ok(entries.Select(e => new RosterImportJobEntryDto
        {
            RowNumber = e.RowNumber,
            StudentCode = e.StudentCode,
            StudentName = e.StudentName,
            GuardianName = e.GuardianName,
            Outcome = e.Outcome,
            Message = e.Message
        }).ToList());
    }

    private static StudentDto MapToDto(Student s) => new()
    {
        Id = s.Id,
        BranchId = s.BranchId,
        FullName = s.FullName,
        StudentCode = s.StudentCode,
        ClassName = s.ClassName,
        IsActive = s.IsActive,
        GuardianCount = s.Guardians?.Count(g => g.IsActive) ?? 0,
        Guardians = s.Guardians?.Where(g => g.IsActive).Select(g => new StudentGuardianDto
        {
            Id = g.Id,
            VisitorProfileId = g.VisitorProfileId,
            FullName = g.VisitorProfile?.FullName ?? "",
            Phone = g.VisitorProfile?.Phone,
            Email = g.VisitorProfile?.Email,
            Relationship = g.Relationship
        }).ToList() ?? new()
    };

    private static RosterImportJobDto MapToDto(RosterImportJob j) => new()
    {
        Id = j.Id,
        BranchId = j.BranchId,
        SourceFileName = j.SourceFileName,
        Source = j.Source,
        Status = j.Status,
        TotalRows = j.TotalRows,
        ProcessedRows = j.ProcessedRows,
        CreatedCount = j.CreatedCount,
        UpdatedCount = j.UpdatedCount,
        DuplicateCount = j.DuplicateCount,
        FailedCount = j.FailedCount,
        StartedAt = j.StartedAt,
        CompletedAt = j.CompletedAt,
        FailureReason = j.FailureReason,
        CreatedAt = j.CreatedAt
    };
}
