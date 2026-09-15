using System.Linq.Expressions;
using QMgr.Domain.Entities.Welfare;
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
using QMgr.Infrastructure.Services;

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
[RequireModule(ModuleCodes.StudentWelfare)]
public class StudentsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IStudentScopeService _scope;

    private const int SearchResultLimit = 10;

    private readonly ILogger<StudentsController> _logger;

    public StudentsController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IStudentScopeService scope,
        ILogger<StudentsController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _scope = scope;
        _logger = logger;
    }

    /// <summary>
    /// Branch ownership AND the row-level student scope, in one call. Every action that reaches a
    /// specific student uses this instead of <see cref="VerifyBranchOwnership"/> alone, so a new
    /// endpoint gets both guards without having to remember the second one. Returns NotFound (never
    /// Forbid) for an out-of-scope student: a 403 would confirm the student exists.
    /// </summary>
    private async Task<IActionResult?> VerifyStudentAccess(Guid branchId, Guid studentId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        return await _scope.VerifyStudentAccessAsync(branchId, studentId);
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
            .Include(s => s.Flags).ThenInclude(f => f.Category)
            .Where(s => s.BranchId == branchId);
        if (!includeInactive) query = query.Where(s => s.IsActive);

        // Row-level scope: a class teacher's roster is their own classes and nothing else. Fails
        // closed — no assignments means an empty list, never the whole branch.
        query = await _scope.ApplyAsync(query, branchId);

        var students = await query.OrderBy(s => s.FullName).Take(Math.Clamp(limit, 1, 500)).ToListAsync();

        var pastoral = await CanViewPastoralAsync();
        var confidential = await CanViewConfidentialAsync();
        var restricted = await CanViewRestrictedAsync();
        return Ok(students.Select(s => MapToDto(s, pastoral, confidential, restricted)).ToList());
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

        // Row-level scope. A class-scoped caller must not be able to find students outside their
        // classes through the check-in search either — this endpoint returns the student's name,
        // code and class alongside guardian contact details, so it is a full roster read by
        // another name. Null means unscoped; an empty set means sees nothing.
        var visibleStudentIds = await _scope.GetVisibleStudentIdsAsync(branchId);
        if (visibleStudentIds is { Count: 0 }) return Ok(new List<StudentGuardianSearchResultDto>());

        var results = await _context.StudentGuardians
            .Include(g => g.Student)
            .Include(g => g.VisitorProfile)
            .Where(g => g.IsActive && g.Student!.BranchId == branchId && g.Student.IsActive)
            .Where(g => visibleStudentIds == null || visibleStudentIds.Contains(g.StudentId))
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

        // The MaxLength/Range attributes on the request are already enforced by ModelState; these
        // are the rules attributes cannot express, and they run server-side regardless of what
        // the browser did.
        var profileError = ValidateProfile(request);
        if (profileError != null)
            return BadRequest(new ProblemDetails { Title = profileError, Status = StatusCodes.Status400BadRequest });

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
        ApplyProfile(student, request);

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
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new ProblemDetails { Title = "Full name is required", Status = StatusCodes.Status400BadRequest });

        var profileError = ValidateProfile(request);
        if (profileError != null)
            return BadRequest(new ProblemDetails { Title = profileError, Status = StatusCodes.Status400BadRequest });

        var student = await _context.Students.Include(s => s.Guardians).ThenInclude(g => g.VisitorProfile)
            .Include(s => s.Flags).ThenInclude(f => f.Category)
            .FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        var newCode = string.IsNullOrWhiteSpace(request.StudentCode) ? null : request.StudentCode.Trim();
        if (newCode != null && newCode != student.StudentCode &&
            await _context.Students.AnyAsync(s => s.OrganizationId == student.OrganizationId && s.StudentCode == newCode && s.IsActive && s.Id != studentId))
        {
            return Conflict(new ProblemDetails { Title = $"A student with code '{newCode}' already exists", Status = StatusCodes.Status409Conflict });
        }

        student.FullName = request.FullName.Trim();
        student.StudentCode = newCode;
        student.ClassName = string.IsNullOrWhiteSpace(request.ClassName) ? null : request.ClassName.Trim();
        student.IsActive = request.IsActive;

        // A caller who cannot see the pastoral tier receives null for those fields, so writing
        // the request back wholesale would let a Reception user silently erase a child's medical
        // summary just by saving the name. Only a caller who can SEE a tier may write it.
        var pastoral = await CanViewPastoralAsync();
        ApplyProfile(student, request, includePastoral: pastoral);

        await _context.SaveChangesAsync();

        return Ok(MapToDto(student, pastoral, await CanViewConfidentialAsync()));
    }

    [HttpDelete("branches/{branchId:guid}/students/{studentId:guid}")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateStudent(Guid branchId, Guid studentId)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
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

    /// <summary>
    /// Records or withdraws data-processing consent for a student — three nullable columns on
    /// the Student row (see Student.DataConsentGivenAt), not a consent-log table. Given=true
    /// stamps now + the caller (re-recording refreshes both); Given=false clears everything,
    /// including the notes, so "withdrawn" and "never asked" read identically.
    /// </summary>
    [HttpPatch("branches/{branchId:guid}/students/{studentId:guid}/consent")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(StudentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateConsent(Guid branchId, Guid studentId, [FromBody] UpdateStudentConsentRequest request)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        var student = await _context.Students.Include(s => s.Guardians).ThenInclude(g => g.VisitorProfile)
            .FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (notes != null && notes.Length > MaxConsentNotesLength)
            return BadRequest(new ProblemDetails { Title = $"Consent notes must be {MaxConsentNotesLength} characters or fewer", Status = StatusCodes.Status400BadRequest });

        if (request.Given)
        {
            student.DataConsentGivenAt = DateTime.UtcNow;
            student.DataConsentRecordedByUserId = CurrentUserId();
            student.DataConsentNotes = notes;
        }
        else
        {
            student.DataConsentGivenAt = null;
            student.DataConsentRecordedByUserId = null;
            student.DataConsentNotes = null;
        }
        student.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(MapToDto(student));
    }

    /// <summary>
    /// Subject-access-request export: everything this system holds about one student, as a
    /// single JSON document — the student row (incl. consent), guardians, visiting-day visits,
    /// and welfare records with their notes/statements, guardian notifications and attachment
    /// metadata (never file bytes). Requires BOTH students.view and welfare.view (each
    /// [RequirePermission] is its own policy; ASP.NET Core ANDs them). Confidential (safeguarding)
    /// records are included only when the caller also holds welfare.confidential.view — the
    /// same rule as WelfareController's timeline — and drafts are never included (an unfinished
    /// quick-log isn't a record yet).
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/{studentId:guid}/data-export")]
    [RequirePermission(Permissions.StudentsView)]
    [RequirePermission(Permissions.WelfareView)]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportStudentData(Guid branchId, Guid studentId)
    {
        var accessError = await VerifyStudentAccess(branchId, studentId);
        if (accessError != null) return accessError;

        var student = await _context.Students.Include(s => s.Guardians).ThenInclude(g => g.VisitorProfile)
            .FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        var canViewConfidential = await CanViewConfidentialAsync();
        var exportLevels = await VisibleLevelsAsync();
        var callerId = CurrentUserId();

        // Live and ended alike — see the note on the flags block below.
        var studentFlags = await _context.StudentFlags
            .Include(f => f.Category)
            .Where(f => f.StudentId == studentId)
            .OrderByDescending(f => f.RaisedAt)
            .ToListAsync();

        // --- Guardians (active links only — a removed guardian is no longer "held about" this student) ---
        var guardianLinks = student.Guardians.Where(g => g.IsActive && g.VisitorProfile != null).ToList();
        var guardianProfileIds = guardianLinks.Select(g => g.VisitorProfileId).Distinct().ToList();

        // --- Visits: any visit filed against this student, plus this student's guardians' own
        // check-ins that weren't for a *different* student (a sibling's visiting-day visit is
        // that sibling's data, not this student's). One indexed query on the org's visitor log.
        var visits = await _context.Visitors
            .Include(v => v.VisitorProfile)
            .Where(v => v.OrganizationId == student.OrganizationId && v.DeletedAt == null
                && (v.StudentId == studentId
                    || (guardianProfileIds.Contains(v.VisitorProfileId) && (v.StudentId == null || v.StudentId == studentId))))
            .OrderByDescending(v => v.CheckedInAt ?? v.ScheduledAt ?? v.CreatedAt)
            .Take(2000)
            .ToListAsync();

        // --- Welfare records (primary or linked via AdditionalStudentIds), same visibility rules as the timeline ---
        var recordsQuery = _context.WelfareRecords
            .Include(r => r.Category)
            .Include(r => r.Attachments)
            .Include(r => r.Notes)
            .Include(r => r.Notifications)
            .Where(r => r.BranchId == branchId && r.Status != WelfareStatus.Draft
                && (r.StudentId == studentId || (r.AdditionalStudentIds != null && r.AdditionalStudentIds.Contains(studentId))));
        recordsQuery = recordsQuery.Where(r => exportLevels.Contains(r.Visibility));
        var records = await recordsQuery.OrderBy(r => r.OccurredAt).ToListAsync();

        // Names for every user id referenced anywhere in the document, resolved in one query.
        var userIds = records.SelectMany(r => new[] { r.ReportedByUserId }
                .Concat(r.AssignedToUserId.HasValue ? new[] { r.AssignedToUserId.Value } : Array.Empty<Guid>())
                .Concat(r.Notes.Select(n => n.AuthorUserId))
                .Concat(r.Notifications.Select(n => n.SentByUserId)))
            .Concat(student.DataConsentRecordedByUserId.HasValue ? new[] { student.DataConsentRecordedByUserId.Value } : Array.Empty<Guid>())
            .Concat(callerId.HasValue ? new[] { callerId.Value } : Array.Empty<Guid>())
            .Distinct()
            .ToList();
        var userNames = await _context.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.Name) ? "Unknown" : u.Name);

        var notifiedProfileIds = records.SelectMany(r => r.Notifications.Select(n => n.GuardianVisitorProfileId)).Distinct().ToList();
        var notifiedNames = notifiedProfileIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.VisitorProfiles.Where(p => notifiedProfileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName);

        var linkedStudentIds = records.SelectMany(r => r.AdditionalStudentIds ?? Array.Empty<Guid>()).Concat(records.Select(r => r.StudentId)).Distinct().ToList();
        var studentNames = await _context.Students.Where(s => linkedStudentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FullName);

        string NameOf(Guid id) => userNames.GetValueOrDefault(id, "Unknown");

        var document = new
        {
            exportType = "SubjectAccessRequest",
            exportedAt = DateTime.UtcNow,
            exportedBy = callerId.HasValue ? NameOf(callerId.Value) : "Unknown",
            organizationId = student.OrganizationId,
            branchId,
            // The caller's own visibility, not a count of what was hidden — a count would confirm
            // a confidential record exists to someone not cleared to know that.
            confidentialRecordsIncluded = canViewConfidential,
            student = new
            {
                student.Id,
                student.FullName,
                student.StudentCode,
                student.ClassName,
                student.IsActive,
                student.CreatedAt,
                student.UpdatedAt,

                // Every background field is exported, deliberately without the visibility tiers
                // the read endpoints apply. A subject access request is the data subject asking
                // what is held about them, not a member of staff browsing — returning a redacted
                // answer here is the worst possible failure, because it is the one that gets
                // discovered by a parent's lawyer. The [RequirePermission] pair on this endpoint
                // is what governs who may run it at all.
                background = new
                {
                    student.DateOfBirth,
                    age = AgeFrom(student.DateOfBirth),
                    sex = student.Sex?.ToString(),
                    student.AdmissionDate,
                    residency = student.Residency?.ToString(),
                    student.House,
                    student.DormitoryOrStream,
                    student.PhotoUrl,
                    student.HomeCountry,
                    student.HomeDistrict,
                    student.HomeAddress,
                    livesWith = student.LivesWith?.ToString(),
                    student.HomeLanguage,
                    student.Religion,
                    student.MedicalConditions,
                    student.Allergies,
                    student.RegularMedication,
                    student.DisabilityOrLearningNeed,
                    feesStatus = student.FeesStatus?.ToString(),
                    student.SponsorName,
                    transportMode = student.TransportMode?.ToString(),
                    student.PreviousSchool
                },

                dataConsent = new
                {
                    given = student.DataConsentGivenAt.HasValue,
                    givenAt = student.DataConsentGivenAt,
                    recordedBy = student.DataConsentRecordedByUserId.HasValue ? NameOf(student.DataConsentRecordedByUserId.Value) : null,
                    notes = student.DataConsentNotes
                }
            },

            // Flags, live AND ended: "we flagged this child as a young carer for two years and
            // then stopped" is squarely within what is held about them.
            flags = studentFlags.Select(f => new
            {
                f.Id,
                category = f.Category?.Name,
                tier = f.Tier.ToString(),
                f.Notes,
                raisedBy = NameOf(f.RaisedByUserId),
                f.RaisedAt,
                f.ReviewDueDate,
                f.EndedAt,
                endedBy = f.EndedByUserId.HasValue ? NameOf(f.EndedByUserId.Value) : null,
                f.EndReason
            }).ToList(),

            guardians = guardianLinks.Select(g => new
            {
                linkId = g.Id,
                visitorProfileId = g.VisitorProfileId,
                fullName = g.VisitorProfile!.FullName,
                phone = g.VisitorProfile.Phone,
                email = g.VisitorProfile.Email,
                relationship = g.Relationship,
                contactRestriction = g.ContactRestriction.ToString(),
                g.RestrictionReason,
                g.HasLegalCustody,
                g.IsPrimaryContact,
                g.ContactPriority,
                g.LivesWithStudent,
                linkedAt = g.CreatedAt
            }).ToList(),
            visits = visits.Select(v => new
            {
                v.Id,
                v.BadgeCode,
                visitorName = v.VisitorProfile?.FullName,
                v.Purpose,
                v.HostName,
                v.StudentName,
                status = v.Status.ToString(),
                v.ScheduledAt,
                v.CheckedInAt,
                v.CheckedOutAt,
                v.Notes,
                visitorConsentGivenAt = v.ConsentGivenAt
            }).ToList(),
            welfareRecords = records.Select(r => new
            {
                r.Id,
                studentName = studentNames.GetValueOrDefault(r.StudentId, "Unknown"),
                isPrimaryStudent = r.StudentId == studentId,
                caseType = r.CaseType.ToString(),
                category = r.Category?.Name,
                tier = r.Tier.ToString(),
                r.Points,
                r.Description,
                r.Location,
                r.OccurredAt,
                status = r.Status.ToString(),
                visibility = r.Visibility.ToString(),
                reportedBy = NameOf(r.ReportedByUserId),
                loggedAt = r.CreatedAt,
                r.ActionTaken,
                assignedTo = r.AssignedToUserId.HasValue ? NameOf(r.AssignedToUserId.Value) : null,
                r.ActionDueDate,
                alsoAppliesTo = (r.AdditionalStudentIds ?? Array.Empty<Guid>()).Select(id => studentNames.GetValueOrDefault(id, "Unknown")).ToList(),
                notes = r.Notes.OrderBy(n => n.CreatedAt).Select(n => new
                {
                    n.Id,
                    kind = n.Kind.ToString(),
                    n.Body,
                    author = NameOf(n.AuthorUserId),
                    n.AttributedToName,
                    n.IsFinal,
                    n.CreatedAt
                }).ToList(),
                notificationsSent = r.Notifications.OrderBy(n => n.CreatedAt).Select(n => new
                {
                    n.Id,
                    guardian = notifiedNames.GetValueOrDefault(n.GuardianVisitorProfileId, "Unknown"),
                    channel = n.Channel.ToString(),
                    n.Message,
                    n.Success,
                    sentBy = NameOf(n.SentByUserId),
                    n.CreatedAt
                }).ToList(),
                attachments = r.Attachments.OrderBy(a => a.CreatedAt).Select(a => new
                {
                    a.Id,
                    a.FileName,
                    a.ContentType,
                    a.FileSizeBytes,
                    uploadedBy = NameOf(a.UploadedByUserId),
                    uploadedAt = a.CreatedAt
                }).ToList()
            }).ToList()
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ExportJsonOptions);
        var codePart = string.IsNullOrWhiteSpace(student.StudentCode)
            ? student.Id.ToString("N")[..8]
            : new string(student.StudentCode.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
        return File(bytes, "application/json", $"student-{codePart}-data-export.json");
    }

    private const int MaxConsentNotesLength = 500;

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    // Copied from WelfareController.CanViewConfidentialAsync (same role-lookup shape as
    // PermissionAuthorizationHandler) — the SAR export has to apply the same confidentiality
    // gate as the timeline, and reaching into another controller's private helper isn't an option.
    private async Task<bool> CanViewConfidentialAsync() => await HasPermissionAsync(Permissions.WelfareConfidentialView);

    /// <summary>
    /// The administrator-only rung: a student's restricted note, and any flag or record marked
    /// Restricted. Seeded to Tenant Admin and SuperAdmin only.
    /// </summary>
    private async Task<bool> CanViewRestrictedAsync() => await HasPermissionAsync(Permissions.WelfareRestrictedView);

    /// <summary>
    /// Which visibility levels this caller may see. Mirrors WelfareController's own helper — the
    /// two controllers genuinely need the same rule and neither can reach the other's privates, so
    /// the duplication is the same deliberate one the confidential check above already carries.
    /// Written as a SET rather than a ceiling so that holding welfare.restricted.view without
    /// welfare.confidential.view does not accidentally grant the rung below it.
    /// </summary>
    private async Task<List<WelfareVisibility>> VisibleLevelsAsync()
    {
        var levels = new List<WelfareVisibility> { WelfareVisibility.Standard };
        if (await CanViewConfidentialAsync()) levels.Add(WelfareVisibility.Confidential);
        if (await CanViewRestrictedAsync()) levels.Add(WelfareVisibility.Restricted);
        return levels;
    }

    /// <summary>Whether this caller may see one specific level. Fails closed on an unrecognised value.</summary>
    private async Task<bool> CanSeeLevelAsync(WelfareVisibility visibility) => visibility switch
    {
        WelfareVisibility.Standard => true,
        WelfareVisibility.Confidential => await CanViewConfidentialAsync(),
        WelfareVisibility.Restricted => await CanViewRestrictedAsync(),
        _ => false
    };

    /// <summary>
    /// The pastoral tier — health summary, family context and flags. Built on the existing
    /// <c>welfare.view</c> permission rather than a new one: anyone trusted to read a child's
    /// welfare chronology is by definition trusted with the background that chronology is read
    /// against, and inventing a fourth permission for the same audience only creates a role
    /// matrix an administrator will eventually get wrong.
    /// </summary>
    private async Task<bool> CanViewPastoralAsync() => await HasPermissionAsync(Permissions.WelfareView);

    private async Task<bool> HasPermissionAsync(string permissionCode)
    {
        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole)) return true;

        var userId = CurrentUserId();
        if (!userId.HasValue) return false;

        return await _context.Users
            .Where(u => u.Id == userId.Value && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions)
            .AnyAsync(rp => rp.Permission.Code == permissionCode);
    }

    [HttpPost("branches/{branchId:guid}/students/{studentId:guid}/guardians")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(StudentGuardianDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddGuardian(Guid branchId, Guid studentId, [FromBody] AddGuardianRequest request)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
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

    /// <summary>
    /// Everything about the LINK between one guardian and one child — relationship, contact
    /// ordering, custody, and the per-child contact restriction.
    ///
    /// The restriction has to live here and nowhere else: <c>VisitorProfile.IsWatchlisted</c> is
    /// organization-scoped, so it can bar a person from the site but cannot express the ordinary
    /// shape of a custody order, where a parent may visit one child and must not have contact
    /// with a sibling. Before this endpoint that situation was unrepresentable, and a school
    /// would have discovered the gap at the gate on visiting day with the child present.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/students/{studentId:guid}/guardians/{guardianLinkId:guid}")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(StudentGuardianDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateGuardianLink(Guid branchId, Guid studentId, Guid guardianLinkId, [FromBody] UpdateGuardianLinkRequest request)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.Relationship))
            return BadRequest(new ProblemDetails { Title = "Relationship is required", Status = StatusCodes.Status400BadRequest });

        if (!Enum.IsDefined(request.ContactRestriction))
            return BadRequest(new ProblemDetails { Title = "Unrecognised contact restriction", Status = StatusCodes.Status400BadRequest });

        // A restriction with no reason is a note nobody can act on: the member of staff refusing
        // a parent at the gate has to be able to say why, and "the system says no" is not a
        // defensible answer to a person being turned away from their own child.
        if (request.ContactRestriction != GuardianContactRestriction.None && string.IsNullOrWhiteSpace(request.RestrictionReason))
            return BadRequest(new ProblemDetails { Title = "A contact restriction needs a reason", Status = StatusCodes.Status400BadRequest });

        if (request.ContactPriority is { } prio && (prio < 1 || prio > 99))
            return BadRequest(new ProblemDetails { Title = "Contact priority must be between 1 and 99", Status = StatusCodes.Status400BadRequest });

        var link = await _context.StudentGuardians
            .Include(g => g.VisitorProfile)
            .Include(g => g.Student)
            .FirstOrDefaultAsync(g => g.Id == guardianLinkId && g.StudentId == studentId && g.Student!.BranchId == branchId);
        if (link == null) return NotFound();

        // Setting a restriction is a safeguarding act, and its reason is confidential-tier data.
        // Someone who cannot read the existing reason must not be able to overwrite it either.
        if (request.ContactRestriction != link.ContactRestriction || request.RestrictionReason != link.RestrictionReason)
        {
            if (!await CanViewConfidentialAsync())
                return Forbid();
        }

        link.Relationship = request.Relationship.Trim();
        link.ContactRestriction = request.ContactRestriction;
        link.RestrictionReason = request.ContactRestriction == GuardianContactRestriction.None
            ? null
            : Clean(request.RestrictionReason);
        link.HasLegalCustody = request.HasLegalCustody;
        link.ContactPriority = request.ContactPriority;
        link.LivesWithStudent = request.LivesWithStudent;

        // Exactly one primary contact per child — promoting this one demotes the rest, rather
        // than leaving two rows both claiming to be who you ring first.
        if (request.IsPrimaryContact && !link.IsPrimaryContact)
        {
            var siblings = await _context.StudentGuardians
                .Where(g => g.StudentId == studentId && g.Id != guardianLinkId && g.IsPrimaryContact)
                .ToListAsync();
            foreach (var s in siblings) s.IsPrimaryContact = false;
        }
        link.IsPrimaryContact = request.IsPrimaryContact;

        await _context.SaveChangesAsync();

        var confidential = await CanViewConfidentialAsync();
        return Ok(new StudentGuardianDto
        {
            Id = link.Id,
            VisitorProfileId = link.VisitorProfileId,
            FullName = link.VisitorProfile?.FullName ?? "",
            Phone = link.VisitorProfile?.Phone,
            Email = link.VisitorProfile?.Email,
            Relationship = link.Relationship,
            ContactRestriction = link.ContactRestriction,
            RestrictionReason = confidential ? link.RestrictionReason : null,
            HasLegalCustody = link.HasLegalCustody,
            IsPrimaryContact = link.IsPrimaryContact,
            ContactPriority = link.ContactPriority,
            LivesWithStudent = link.LivesWithStudent
        });
    }

    [HttpDelete("branches/{branchId:guid}/students/{studentId:guid}/guardians/{guardianLinkId:guid}")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveGuardian(Guid branchId, Guid studentId, Guid guardianLinkId)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
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

    /// <summary>Import history. <paramref name="kind"/> narrows to roster uploads or welfare-history backfills (both live in the same job table — see RosterImportKind); omitted = every kind.</summary>
    [HttpGet("branches/{branchId:guid}/students/import-jobs")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(List<RosterImportJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImportJobs(Guid branchId, [FromQuery] RosterImportKind? kind = null, [FromQuery] int limit = 50)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        // An import is a whole-roster operation. A class-scoped caller has no business reading the
        // per-row log of a bulk upload that touched every student in the school, so they get an
        // empty history rather than a filtered one — there is nothing here that is "theirs".
        if (!await _scope.IsUnscopedAsync()) return Ok(new List<RosterImportJobDto>());

        var query = _context.RosterImportJobs.Where(j => j.BranchId == branchId);
        if (kind.HasValue) query = query.Where(j => j.Kind == kind.Value);

        var jobs = await query
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

        // Same reasoning as the listing above, and a 404 rather than an empty result so a scoped
        // caller cannot probe for which job IDs exist.
        if (!await _scope.IsUnscopedAsync()) return NotFound();

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

        // The per-row log names every student the upload touched — the widest roster read in this
        // controller. Closed to a class-scoped caller entirely.
        if (!await _scope.IsUnscopedAsync()) return NotFound();

        var jobExists = await _context.RosterImportJobs.AnyAsync(j => j.Id == jobId && j.BranchId == branchId);
        if (!jobExists) return NotFound();

        var query = _context.RosterImportJobEntries.Where(e => e.RosterImportJobId == jobId);
        if (outcome.HasValue) query = query.Where(e => e.Outcome == outcome.Value);

        var entries = await query.OrderBy(e => e.RowNumber).Take(Math.Clamp(limit, 1, 5000)).ToListAsync();

        return Ok(entries.Select(MapToDto).ToList());
    }

    /// <summary>
    /// Shared with WelfareController's welfare-scoped copies of these three reads (see
    /// <see cref="WelfareController.GetImportJobs"/>) so the two never drift apart.
    /// </summary>
    internal static RosterImportJobEntryDto MapToDto(RosterImportJobEntry e) => new()
    {
        RowNumber = e.RowNumber,
        StudentCode = e.StudentCode,
        StudentName = e.StudentName,
        GuardianName = e.GuardianName,
        Outcome = e.Outcome,
        Message = e.Message,
        PreviousValue = e.PreviousValue,
        NewValue = e.NewValue
    };

    /// <summary>The clamp both controllers apply to an entries page.</summary>
    internal const int MaxImportEntriesPerPage = 5000;

    /// <summary>
    /// The ONE place that decides who sees what on a student. Three tiers, built on permissions
    /// that already exist rather than three new ones:
    ///
    ///   Open         (students.view)            name, class, house, residency, guardians, and
    ///                                           the FACT of a contact restriction.
    ///   Pastoral     (welfare.view)             health summary, family context, flags and their
    ///                                           notes. House parents, matrons, class teachers.
    ///   Confidential (welfare.confidential.view) restriction reasons and High-tier flag notes.
    ///   Restricted   (welfare.restricted.view)  the administrator-only student note, and any flag
    ///                                           marked Restricted (which is omitted entirely, not
    ///                                           blanked — its existence is the sensitive part).
    ///
    /// Fields above a caller's tier are BLANKED rather than the type being different per audience
    /// — one DTO shape, one decision point. A second mapper that "forgets" a field is precisely
    /// the drift this codebase keeps rediscovering.
    ///
    /// The one thing deliberately NOT hidden from the pastoral tier is
    /// <c>HasRestrictedNotes</c>: somebody handling this child needs to know an administrator
    /// holds information about them, or they cannot know to ask. What that information IS stays
    /// gated.
    /// </summary>
    internal static StudentDto MapToDto(Student s, bool pastoral = true, bool confidential = true, bool restricted = true) => new()
    {
        // --- Restricted tier: administrator only.
        RestrictedNotes = restricted ? s.RestrictedNotes : null,
        RestrictedNotesUpdatedAt = restricted ? s.RestrictedNotesUpdatedAt : null,
        HasRestrictedNotes = pastoral && !string.IsNullOrWhiteSpace(s.RestrictedNotes),

        Id = s.Id,
        BranchId = s.BranchId,
        FullName = s.FullName,
        StudentCode = s.StudentCode,
        ClassName = s.ClassName,
        IsActive = s.IsActive,
        DataConsentGivenAt = s.DataConsentGivenAt,
        DataConsentRecordedByUserId = s.DataConsentRecordedByUserId,
        DataConsentNotes = s.DataConsentNotes,

        // --- Open tier: placement. Everyone who can see the roster needs these to do their job.
        DateOfBirth = s.DateOfBirth,
        AgeYears = AgeFrom(s.DateOfBirth),
        Sex = s.Sex,
        AdmissionDate = s.AdmissionDate,
        Residency = s.Residency,
        House = s.House,
        DormitoryOrStream = s.DormitoryOrStream,
        PhotoUrl = QMgr.Infrastructure.Services.Storage.UploadLinks.Sign(s.PhotoUrl),

        // --- Pastoral tier: context and health.
        HomeCountry = pastoral ? s.HomeCountry : null,
        HomeDistrict = pastoral ? s.HomeDistrict : null,
        HomeAddress = pastoral ? s.HomeAddress : null,
        LivesWith = pastoral ? s.LivesWith : null,
        HomeLanguage = pastoral ? s.HomeLanguage : null,
        Religion = pastoral ? s.Religion : null,
        MedicalConditions = pastoral ? s.MedicalConditions : null,
        Allergies = pastoral ? s.Allergies : null,
        RegularMedication = pastoral ? s.RegularMedication : null,
        DisabilityOrLearningNeed = pastoral ? s.DisabilityOrLearningNeed : null,
        FeesStatus = pastoral ? s.FeesStatus : null,
        SponsorName = pastoral ? s.SponsorName : null,
        TransportMode = pastoral ? s.TransportMode : null,
        PreviousSchool = pastoral ? s.PreviousSchool : null,

        Flags = MapFlags(s.Flags, pastoral, confidential, restricted),

        HasGuardianRestriction = s.Guardians?.Any(g => g.IsActive && g.ContactRestriction != GuardianContactRestriction.None) ?? false,

        GuardianCount = s.Guardians?.Count(g => g.IsActive) ?? 0,
        Guardians = s.Guardians?.Where(g => g.IsActive)
            .OrderByDescending(g => g.IsPrimaryContact)
            .ThenBy(g => g.ContactPriority ?? int.MaxValue)
            .Select(g => new StudentGuardianDto
            {
                Id = g.Id,
                VisitorProfileId = g.VisitorProfileId,
                FullName = g.VisitorProfile?.FullName ?? "",
                Phone = g.VisitorProfile?.Phone,
                Email = g.VisitorProfile?.Email,
                Relationship = g.Relationship,

                // The restriction itself is Open tier on purpose: gate staff cannot enforce what
                // they cannot see. Only the REASON — which names a third party and a legal
                // circumstance — is held back.
                ContactRestriction = g.ContactRestriction,
                RestrictionReason = confidential ? g.RestrictionReason : null,
                HasLegalCustody = g.HasLegalCustody,
                IsPrimaryContact = g.IsPrimaryContact,
                ContactPriority = g.ContactPriority,
                LivesWithStudent = pastoral ? g.LivesWithStudent : null
            }).ToList() ?? new()
    };

    /// <summary>
    /// Live flags only, most severe first. Two independent gates, on two different axes:
    ///
    ///  - <b>Visibility</b> (Standard/Confidential/Restricted) decides whether the flag is returned
    ///    AT ALL. A flag above the caller's level is omitted from the list, not blanked — with a
    ///    flag, its existence is the sensitive part, and a redacted chip saying "something is
    ///    flagged here" would leak exactly what the level is protecting.
    ///  - <b>Tier</b> (severity) still decides whether the flag's NOTES come back, because a
    ///    High-tier flag's free text is where safeguarding detail tends to end up. The chip itself
    ///    survives, because staff need to know a flag exists to behave differently.
    /// </summary>
    internal static List<StudentFlagDto> MapFlags(IEnumerable<StudentFlag>? flags, bool pastoral, bool confidential, bool restricted = true)
    {
        if (flags == null || !pastoral) return new();
        var now = DateTime.UtcNow;

        return flags.Where(f => f.EndedAt == null)
            .Where(f => f.Visibility switch
            {
                WelfareVisibility.Standard => true,
                WelfareVisibility.Confidential => confidential,
                WelfareVisibility.Restricted => restricted,
                _ => false // an unrecognised level fails closed
            })
            .OrderByDescending(f => f.Tier)
            .ThenByDescending(f => f.RaisedAt)
            .Select(f => new StudentFlagDto
            {
                Id = f.Id,
                StudentId = f.StudentId,
                CategoryId = f.CategoryId,
                CategoryName = f.Category?.Name ?? "",
                CategoryColor = f.Category?.Color,
                Tier = f.Tier,
                Visibility = f.Visibility,
                Notes = (f.Tier == WelfareTier.High && !confidential) ? null : f.Notes,
                RaisedByUserId = f.RaisedByUserId,
                RaisedAt = f.RaisedAt,
                ReviewDueDate = f.ReviewDueDate,
                EndedAt = f.EndedAt,
                EndReason = f.EndReason,
                IsActive = true,
                IsReviewOverdue = f.ReviewDueDate.HasValue && f.ReviewDueDate.Value < now
            }).ToList();
    }

    /// <summary>
    /// The rules a <c>[MaxLength]</c> attribute cannot express. Runs on create AND update, and
    /// runs regardless of what the browser validated — the Blazor form shares these same limits,
    /// but a form is a courtesy to the user and this is the actual boundary.
    /// </summary>
    private static string? ValidateProfile(StudentProfileFields p)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (p.DateOfBirth is { } dob)
        {
            if (dob > today)
                return "Date of birth cannot be in the future";
            if (dob < today.AddYears(-130))
                return "Date of birth is not a plausible date";
        }

        if (p.AdmissionDate is { } adm)
        {
            // A year's grace: schools legitimately pre-register a cohort for next term.
            if (adm > today.AddYears(1))
                return "Admission date cannot be more than a year in the future";
            if (p.DateOfBirth is { } d && adm < d)
                return "Admission date cannot be before the date of birth";
        }

        // Guard the enums explicitly. A JSON body can carry any integer, and an out-of-range one
        // would otherwise be stored and then render as a blank chip forever.
        // Geography. The country must be one the picker actually offers, and a district is checked
        // against its country ONLY where Geography holds a complete list — everywhere else the
        // field is free text and any value is legitimate, which is what makes partial coverage
        // safe rather than a trap for a school in a country nobody has listed yet.
        if (!string.IsNullOrWhiteSpace(p.HomeCountry) && !Geography.IsKnownCountry(p.HomeCountry))
            return "Unrecognised country";

        if (!string.IsNullOrWhiteSpace(p.HomeDistrict) && !Geography.IsValidDistrict(p.HomeCountry, p.HomeDistrict))
            return $"'{p.HomeDistrict.Trim()}' is not a known {Geography.DistrictLabelFor(p.HomeCountry).Replace("Home ", "")} in {p.HomeCountry}";

        // A district on its own cannot be checked and cannot be read back reliably: Busia is a
        // Ugandan district and a Kenyan county, and the two are 200km and a border apart.
        if (!string.IsNullOrWhiteSpace(p.HomeDistrict) && string.IsNullOrWhiteSpace(p.HomeCountry))
            return "Choose the country before the district";

        if (p.Sex is { } sex && !Enum.IsDefined(sex)) return "Unrecognised value for sex";
        if (p.Residency is { } res && !Enum.IsDefined(res)) return "Unrecognised value for residency";
        if (p.LivesWith is { } lw && !Enum.IsDefined(lw)) return "Unrecognised value for lives-with";
        if (p.FeesStatus is { } fs && !Enum.IsDefined(fs)) return "Unrecognised value for fees status";
        if (p.TransportMode is { } tm && !Enum.IsDefined(tm)) return "Unrecognised value for transport mode";

        return null;
    }

    /// <summary>
    /// Copies the background fields onto the entity, trimming and collapsing blank strings to
    /// null so "" and null never both mean "unset" in the database.
    /// <paramref name="includePastoral"/> false leaves the pastoral-tier fields untouched — see
    /// the call in UpdateStudent for why writing them back would be destructive.
    /// </summary>
    private static void ApplyProfile(Student s, StudentProfileFields p, bool includePastoral = true)
    {
        s.DateOfBirth = p.DateOfBirth;
        s.Sex = p.Sex;
        s.AdmissionDate = p.AdmissionDate;
        s.Residency = p.Residency;
        s.House = Clean(p.House);
        s.DormitoryOrStream = Clean(p.DormitoryOrStream);
        s.PhotoUrl = QMgr.Infrastructure.Services.Storage.UploadLinks.Strip(Clean(p.PhotoUrl));

        if (!includePastoral) return;

        s.HomeCountry = Clean(p.HomeCountry);
        s.HomeDistrict = Clean(p.HomeDistrict);
        s.HomeAddress = Clean(p.HomeAddress);
        s.LivesWith = p.LivesWith;
        s.HomeLanguage = Clean(p.HomeLanguage);
        s.Religion = Clean(p.Religion);
        s.MedicalConditions = Clean(p.MedicalConditions);
        s.Allergies = Clean(p.Allergies);
        s.RegularMedication = Clean(p.RegularMedication);
        s.DisabilityOrLearningNeed = Clean(p.DisabilityOrLearningNeed);
        s.FeesStatus = p.FeesStatus;
        s.SponsorName = Clean(p.SponsorName);
        s.TransportMode = p.TransportMode;
        s.PreviousSchool = Clean(p.PreviousSchool);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Derived once, server-side, so no two screens can disagree about a child's age.</summary>
    internal static int? AgeFrom(DateOnly? dob)
    {
        if (dob is not { } d) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - d.Year;
        if (d > today.AddYears(-age)) age--;
        return age < 0 || age > 130 ? null : age;
    }

    // Internal so WelfareController's historical-import endpoint returns the identical DTO shape
    // for the job it creates in this same table, rather than a second mapper to keep in sync.
    internal static RosterImportJobDto MapToDto(RosterImportJob j) => new()
    {
        Id = j.Id,
        BranchId = j.BranchId,
        SourceFileName = j.SourceFileName,
        Source = j.Source,
        Kind = j.Kind,
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

    // =========================================================================================
    // Restricted note — administrator only.
    //
    // "This child's living situation is confidential", "do not discuss the father's case in front
    // of staff": current-state knowledge that is neither an incident nor a standing flag, and had
    // nowhere to live before this. Three nullable columns on the Student row rather than a fourth
    // welfare table, per the project's enhance-before-add rule.
    //
    // Gated on welfare.restricted.view for BOTH reading and writing. Everyone with the pastoral
    // tier still sees StudentDto.HasRestrictedNotes — somebody handling the child needs to know an
    // administrator holds something about them, or they cannot know to ask.
    // =========================================================================================

    /// <summary>
    /// Reads the note. A separate endpoint from the student read rather than just relying on the
    /// blanking in MapToDto, so the panel can fetch it deliberately and an access to genuinely
    /// restricted content is a distinct request in the logs rather than a side effect of opening
    /// the roster.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/{studentId:guid}/restricted-notes")]
    [RequirePermission(Permissions.WelfareRestrictedView)]
    [ProducesResponseType(typeof(StudentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRestrictedNotes(Guid branchId, Guid studentId)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        var student = await _context.Students
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        var dto = MapToDto(student, pastoral: true, confidential: true, restricted: true) with
        {
            RestrictedNotesUpdatedByName = student.RestrictedNotesUpdatedByUserId.HasValue
                ? (await ResolveUserNamesAsync(new[] { student.RestrictedNotesUpdatedByUserId.Value }))
                    .GetValueOrDefault(student.RestrictedNotesUpdatedByUserId.Value, "Unknown")
                : null
        };

        return Ok(dto);
    }

    /// <summary>
    /// Replaces the note. Empty or whitespace clears it along with both stamps — leaving a
    /// "last updated by" on an empty note would say somebody wrote something and then imply it was
    /// removed, which is a worse answer than nothing.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/students/{studentId:guid}/restricted-notes")]
    [RequirePermission(Permissions.WelfareRestrictedView)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRestrictedNotes(Guid branchId, Guid studentId, [FromBody] UpdateStudentRestrictedNotesRequest request)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (notes is { Length: > 4000 })
            return BadRequest(new ProblemDetails { Title = "Restricted notes cannot exceed 4000 characters", Status = StatusCodes.Status400BadRequest });

        var student = await _context.Students.FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        student.RestrictedNotes = notes;
        student.RestrictedNotesUpdatedAt = notes == null ? null : DateTime.UtcNow;
        student.RestrictedNotesUpdatedByUserId = notes == null ? null : CurrentUserId();
        student.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Restricted notes {Action} for student {StudentId} by user {UserId}",
            notes == null ? "cleared" : "updated", studentId, CurrentUserId());

        return NoContent();
    }

    // =========================================================================================
    // Student flags — standing vulnerability markers. The lens a chronology entry is read
    // through: a late arrival from a child with no flags is a late arrival; the same lateness
    // from a child flagged as a young carer is a signal.
    //
    // Zero new permissions: raising and ending a flag is students.manage (the same right that
    // edits the child's record), reading one is the pastoral tier, and a High-tier flag's notes
    // need welfare.confidential.view exactly as a safeguarding record does.
    // =========================================================================================

    [HttpGet("branches/{branchId:guid}/students/{studentId:guid}/flags")]
    [RequirePermission(Permissions.WelfareView)]
    [ProducesResponseType(typeof(List<StudentFlagDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStudentFlags(Guid branchId, Guid studentId, [FromQuery] bool includeEnded = false)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        var exists = await _context.Students.AnyAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (!exists) return NotFound();

        var query = _context.StudentFlags
            .Include(f => f.Category)
            .Where(f => f.StudentId == studentId && f.BranchId == branchId);
        if (!includeEnded) query = query.Where(f => f.EndedAt == null);

        // A flag above the caller's level is omitted entirely rather than blanked: with a flag its
        // existence is the sensitive part, and a redacted chip saying "something is flagged here"
        // would leak exactly what the level protects.
        var flagLevels = await VisibleLevelsAsync();
        query = query.Where(f => flagLevels.Contains(f.Visibility));

        var flags = await query.OrderByDescending(f => f.EndedAt == null)
            .ThenByDescending(f => f.Tier)
            .ThenByDescending(f => f.RaisedAt)
            .ToListAsync();

        var confidential = await CanViewConfidentialAsync();
        var names = await ResolveUserNamesAsync(
            flags.Select(f => f.RaisedByUserId)
                 .Concat(flags.Where(f => f.EndedByUserId.HasValue).Select(f => f.EndedByUserId!.Value)));

        var now = DateTime.UtcNow;
        return Ok(flags.Select(f => new StudentFlagDto
        {
            Id = f.Id,
            StudentId = f.StudentId,
            CategoryId = f.CategoryId,
            CategoryName = f.Category?.Name ?? "",
            CategoryColor = f.Category?.Color,
            Tier = f.Tier,
            Visibility = f.Visibility,
            Notes = (f.Tier == WelfareTier.High && !confidential) ? null : f.Notes,
            RaisedByUserId = f.RaisedByUserId,
            RaisedByName = names.GetValueOrDefault(f.RaisedByUserId, "Unknown"),
            RaisedAt = f.RaisedAt,
            ReviewDueDate = f.ReviewDueDate,
            EndedAt = f.EndedAt,
            EndedByName = f.EndedByUserId.HasValue ? names.GetValueOrDefault(f.EndedByUserId.Value, "Unknown") : null,
            EndReason = f.EndReason,
            IsActive = f.EndedAt == null,
            IsReviewOverdue = f.EndedAt == null && f.ReviewDueDate.HasValue && f.ReviewDueDate.Value < now
        }).ToList());
    }

    [HttpPost("branches/{branchId:guid}/students/{studentId:guid}/flags")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(StudentFlagDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> RaiseStudentFlag(Guid branchId, Guid studentId, [FromBody] CreateStudentFlagRequest request)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        var student = await _context.Students.FirstOrDefaultAsync(s => s.Id == studentId && s.BranchId == branchId);
        if (student == null) return NotFound();

        if (request.CategoryId == Guid.Empty)
            return BadRequest(new ProblemDetails { Title = "Choose what the flag is", Status = StatusCodes.Status400BadRequest });

        if (!Enum.IsDefined(request.Tier))
            return BadRequest(new ProblemDetails { Title = "Unrecognised tier", Status = StatusCodes.Status400BadRequest });

        // Raising a High-tier flag is a safeguarding act with the same audience as a confidential
        // record — someone who could not then read it back must not be able to create it.
        if (request.Tier == WelfareTier.High && !await CanViewConfidentialAsync())
            return Forbid();

        // The same rule on the visibility axis: you cannot file a flag into a level you could not
        // then read, which would hide it from everyone including yourself.
        if (!Enum.IsDefined(request.Visibility))
            return BadRequest(new ProblemDetails { Title = "Unrecognised visibility", Status = StatusCodes.Status400BadRequest });
        if (!await CanSeeLevelAsync(request.Visibility))
            return Forbid();

        var category = await _context.WelfareCategories
            .FirstOrDefaultAsync(c => c.Id == request.CategoryId && c.OrganizationId == student.OrganizationId && c.IsActive);
        if (category == null)
            return BadRequest(new ProblemDetails { Title = "That category does not exist on this organization", Status = StatusCodes.Status400BadRequest });

        if (request.ReviewDueDate is { } due && due.Date < DateTime.UtcNow.Date)
            return BadRequest(new ProblemDetails { Title = "A review date in the past would be overdue the moment it is set", Status = StatusCodes.Status400BadRequest });

        // One live flag per category per student. A second "young carer" flag adds no
        // information, it just splits the review history across two rows.
        var duplicate = await _context.StudentFlags
            .AnyAsync(f => f.StudentId == studentId && f.CategoryId == request.CategoryId && f.EndedAt == null);
        if (duplicate)
            return Conflict(new ProblemDetails { Title = category.Name + " is already raised for this student", Status = StatusCodes.Status409Conflict });

        var userId = CurrentUserId() ?? Guid.Empty;
        var flag = new StudentFlag
        {
            OrganizationId = student.OrganizationId,
            BranchId = branchId,
            StudentId = studentId,
            CategoryId = request.CategoryId,
            Tier = request.Tier,
            Visibility = request.Visibility,
            Notes = Clean(request.Notes),
            RaisedByUserId = userId,
            RaisedAt = DateTime.UtcNow,
            ReviewDueDate = request.ReviewDueDate
        };
        _context.StudentFlags.Add(flag);
        await _context.SaveChangesAsync();

        var names = await ResolveUserNamesAsync(new[] { userId });
        return CreatedAtAction(nameof(GetStudentFlags), new { branchId, studentId }, new StudentFlagDto
        {
            Id = flag.Id,
            StudentId = studentId,
            CategoryId = category.Id,
            CategoryName = category.Name,
            CategoryColor = category.Color,
            Tier = flag.Tier,
            Notes = flag.Notes,
            RaisedByUserId = userId,
            RaisedByName = names.GetValueOrDefault(userId, "Unknown"),
            RaisedAt = flag.RaisedAt,
            ReviewDueDate = flag.ReviewDueDate,
            IsActive = true,
            IsReviewOverdue = false
        });
    }

    /// <summary>
    /// Reviewing a flag IS this endpoint — the "review" half of assess-plan-do-review for the
    /// flag itself. Clearing ReminderSentAt is the point: a reviewed flag starts its nagging
    /// cycle afresh from the new date rather than staying silent because it was chased once.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/students/{studentId:guid}/flags/{flagId:guid}")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateStudentFlag(Guid branchId, Guid studentId, Guid flagId, [FromBody] UpdateStudentFlagRequest request)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        if (!Enum.IsDefined(request.Tier))
            return BadRequest(new ProblemDetails { Title = "Unrecognised tier", Status = StatusCodes.Status400BadRequest });

        var flag = await _context.StudentFlags
            .FirstOrDefaultAsync(f => f.Id == flagId && f.StudentId == studentId && f.BranchId == branchId);
        if (flag == null) return NotFound();

        if (flag.EndedAt != null)
            return BadRequest(new ProblemDetails { Title = "That flag has already been ended", Status = StatusCodes.Status400BadRequest });

        if ((flag.Tier == WelfareTier.High || request.Tier == WelfareTier.High) && !await CanViewConfidentialAsync())
            return Forbid();

        // Both directions on the visibility axis. The CURRENT level matters as much as the
        // requested one: without that check a caller who cannot see a Restricted flag could still
        // downgrade it to Standard by guessing its ID, which is the whole protection undone.
        if (!Enum.IsDefined(request.Visibility))
            return BadRequest(new ProblemDetails { Title = "Unrecognised visibility", Status = StatusCodes.Status400BadRequest });
        if (!await CanSeeLevelAsync(flag.Visibility) || !await CanSeeLevelAsync(request.Visibility))
            return Forbid();

        if (request.ReviewDueDate is { } due && due.Date < DateTime.UtcNow.Date)
            return BadRequest(new ProblemDetails { Title = "A review date in the past would be overdue the moment it is set", Status = StatusCodes.Status400BadRequest });

        flag.Tier = request.Tier;
        flag.Visibility = request.Visibility;
        flag.Notes = Clean(request.Notes);
        flag.ReviewDueDate = request.ReviewDueDate;
        flag.ReminderSentAt = null;

        await _context.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Ends a flag. Deliberately never deletes it: the fact of having been flagged, and for how
    /// long, is itself welfare information — and a flag that can be made to disappear is a flag
    /// nobody can rely on in a review.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/students/{studentId:guid}/flags/{flagId:guid}/end")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> EndStudentFlag(Guid branchId, Guid studentId, Guid flagId, [FromBody] EndStudentFlagRequest request)
    {
        var branchError = await VerifyStudentAccess(branchId, studentId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.EndReason))
            return BadRequest(new ProblemDetails { Title = "Say why the flag is being ended", Status = StatusCodes.Status400BadRequest });

        var flag = await _context.StudentFlags
            .FirstOrDefaultAsync(f => f.Id == flagId && f.StudentId == studentId && f.BranchId == branchId);
        if (flag == null) return NotFound();

        if (flag.EndedAt != null)
            return BadRequest(new ProblemDetails { Title = "That flag has already been ended", Status = StatusCodes.Status400BadRequest });

        if (flag.Tier == WelfareTier.High && !await CanViewConfidentialAsync())
            return Forbid();

        // You cannot end a flag you could not see. Without this, a caller could clear a Restricted
        // flag off a student by guessing its ID — the deletion path around the visibility gate.
        if (!await CanSeeLevelAsync(flag.Visibility))
            return Forbid();

        flag.EndedAt = DateTime.UtcNow;
        flag.EndedByUserId = CurrentUserId();
        flag.EndReason = request.EndReason.Trim();

        await _context.SaveChangesAsync();
        return NoContent();
    }

    private async Task<Dictionary<Guid, string>> ResolveUserNamesAsync(IEnumerable<Guid> userIds)
    {
        var ids = userIds.Where(i => i != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return new();

        return await _context.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.Name) ? "Unknown" : u.Name);
    }

    // =========================================================================================
    // Branch vocabularies — the small, user-configurable master data lists.
    //
    // Stored in Branch.Settings under one key rather than in tables, per the rule written on
    // BranchVocabulariesDto. Two things this has to get right that a table would have handled for
    // free, and both are handled explicitly below: a rename has to be propagated to the students
    // holding the old string, and a delete has to be refused while anyone still holds the value.
    // =========================================================================================

    internal const string VocabularySettingsKey = "Vocabularies";

    internal static BranchVocabulariesDto ReadVocabularies(string? branchSettingsJson)
    {
        if (string.IsNullOrEmpty(branchSettingsJson)) return new BranchVocabulariesDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(branchSettingsJson);
            if (root != null && root.TryGetValue(VocabularySettingsKey, out var element))
                return JsonSerializer.Deserialize<BranchVocabulariesDto>(element.GetRawText()) ?? new BranchVocabulariesDto();
        }
        catch (JsonException) { /* malformed settings blob — treat as not configured */ }
        return new BranchVocabulariesDto();
    }

    private static string WriteVocabularies(string? branchSettingsJson, BranchVocabulariesDto vocab)
    {
        var merged = string.IsNullOrEmpty(branchSettingsJson)
            ? new Dictionary<string, object>()
            : (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(branchSettingsJson) ?? new())
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);

        merged[VocabularySettingsKey] = vocab;

        // The legacy ClassColors map is now derived from the class list on every write, so the two
        // can never disagree. It is kept rather than dropped because the printed visiting-day pass
        // and any older client still read it — this is the compatibility shim, not a second store.
        merged[ClassColorSettingsKey] = new ClassColorSettingsDto
        {
            Colors = vocab.Classes
                .Where(c => !string.IsNullOrWhiteSpace(c.Color))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Color!)
        };

        return JsonSerializer.Serialize(merged);
    }

    /// <summary>
    /// The configured lists for a branch.
    ///
    /// On a branch that has never opened the editor, the class list is SEEDED from what is already
    /// in use — the class names students actually hold, plus any name the old colour map knew
    /// about. Starting empty would be worse than useless: it would present a school with hundreds
    /// of students as having no classes, and invite them to retype a list the system can already
    /// see. Nothing is persisted by this read; the seed is only offered until somebody saves.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/students/vocabularies")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(BranchVocabulariesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVocabularies(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var settingsJson = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var vocab = ReadVocabularies(settingsJson);

        if (vocab.Classes.Count == 0)
        {
            var legacyColors = ReadClassColorSettings(settingsJson).Colors;

            var inUse = await _context.Students
                .Where(s => s.BranchId == branchId && s.IsActive && s.ClassName != null && s.ClassName != "")
                .Select(s => s.ClassName!)
                .Distinct()
                .ToListAsync();

            vocab.Classes = inUse
                .Concat(legacyColors.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, NaturalOrder)
                .Select((name, i) => new VocabularyItemDto
                {
                    Name = name,
                    Color = legacyColors.TryGetValue(name, out var c) ? c : null,
                    SortOrder = i,
                    IsActive = true
                })
                .ToList();
        }

        if (vocab.Houses.Count == 0)
        {
            vocab.Houses = await SeedFromStudentsAsync(branchId, s => s.House);
        }

        if (vocab.Dormitories.Count == 0)
        {
            vocab.Dormitories = await SeedFromStudentsAsync(branchId, s => s.DormitoryOrStream);
        }

        return Ok(vocab);
    }

    private async Task<List<VocabularyItemDto>> SeedFromStudentsAsync(Guid branchId, Expression<Func<Student, string?>> selector)
    {
        var values = await _context.Students
            .Where(s => s.BranchId == branchId && s.IsActive)
            .Select(selector)
            .Where(v => v != null && v != "")
            .Distinct()
            .ToListAsync();

        return values
            .Select(v => v!)
            .OrderBy(v => v, NaturalOrder)
            .Select((name, i) => new VocabularyItemDto { Name = name, SortOrder = i, IsActive = true })
            .ToList();
    }

    /// <summary>
    /// Saves the lists, and propagates renames to the students holding the old value.
    ///
    /// Renames arrive as an explicit old→new map rather than being inferred by diffing the list
    /// against what was stored. A diff genuinely cannot tell a rename from a delete plus an add,
    /// and guessing wrong either strands every student on a class that no longer exists or
    /// rewrites the wrong one — the class name on a student is a copied string, which is the one
    /// real cost of storing this vocabulary outside a table.
    ///
    /// Deleting a value that students still hold is REFUSED rather than silently orphaning them,
    /// the same discipline the welfare-category editor already applies.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/students/vocabularies")]
    [RequirePermission(Permissions.StudentsManage)]
    [ProducesResponseType(typeof(UpdateBranchVocabulariesResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateVocabularies(Guid branchId, [FromBody] UpdateBranchVocabulariesRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId);
        if (branch == null) return NotFound();

        var vocab = request.Vocabularies ?? new BranchVocabulariesDto();

        var listError = ValidateList(vocab.Classes, "class")
                        ?? ValidateList(vocab.Houses, "house")
                        ?? ValidateList(vocab.Dormitories, "dormitory");
        if (listError != null)
            return BadRequest(new ProblemDetails { Title = listError, Status = StatusCodes.Status400BadRequest });

        var students = await _context.Students.Where(s => s.BranchId == branchId).ToListAsync();

        var renamed = 0;
        renamed += ApplyRenames(students, request.ClassRenames, s => s.ClassName, (s, v) => s.ClassName = v);
        renamed += ApplyRenames(students, request.HouseRenames, s => s.House, (s, v) => s.House = v);
        renamed += ApplyRenames(students, request.DormitoryRenames, s => s.DormitoryOrStream, (s, v) => s.DormitoryOrStream = v);

        // Class-teacher assignments reference a class BY NAME, so a rename has to move them too or
        // the teacher silently stops matching their own students — no error, no alert, just a class
        // that quietly stops reaching anybody. Same transaction as the student renames.
        var assignments = await _context.ClassTeacherAssignments
            .Where(a => a.BranchId == branchId && a.EndedAt == null)
            .ToListAsync();
        var assignmentsRenamed = ApplyAssignmentRenames(assignments, request.ClassRenames);

        // Guard AFTER renames: a value that was renamed is no longer held by anybody, so checking
        // first would refuse a perfectly ordinary rename-and-tidy in one save.
        var orphanError = FindOrphaned(students, s => s.ClassName, vocab.Classes, "class")
                          ?? FindOrphaned(students, s => s.House, vocab.Houses, "house")
                          ?? FindOrphaned(students, s => s.DormitoryOrStream, vocab.Dormitories, "dormitory");
        if (orphanError != null)
            return BadRequest(new ProblemDetails { Title = orphanError, Status = StatusCodes.Status400BadRequest });

        // A class with a live class teacher counts as in use. The editor already refuses to remove
        // a class students hold ("In-use entries cannot be removed — retire them"); removing one
        // that a teacher holds would leave an assignment pointing at a class that no longer exists.
        var vocabNames = vocab.Classes.Select(c => c.Name.Trim().ToLowerInvariant()).ToHashSet();
        var strandedClass = assignments
            .Select(a => a.ClassName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(n => !vocabNames.Contains(n.Trim().ToLowerInvariant()));
        if (strandedClass != null)
            return BadRequest(new ProblemDetails
            {
                Title = $"'{strandedClass}' still has a class teacher",
                Detail = "End that class-teacher assignment before removing or renaming the class away.",
                Status = StatusCodes.Status400BadRequest
            });

        vocab.HomeLanguages = CleanSuggestions(vocab.HomeLanguages);
        vocab.Religions = CleanSuggestions(vocab.Religions);
        vocab.GuardianRelationships = CleanSuggestions(vocab.GuardianRelationships);
        vocab.ActionsTaken = CleanSuggestions(vocab.ActionsTaken);

        branch.Settings = WriteVocabularies(branch.Settings, vocab);
        branch.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        if (renamed > 0)
            _logger.LogInformation("Branch {BranchId} vocabulary rename touched {Count} student row(s)", branchId, renamed);
        if (assignmentsRenamed > 0)
            _logger.LogInformation("Branch {BranchId} class rename moved {Count} class-teacher assignment(s)", branchId, assignmentsRenamed);

        return Ok(new UpdateBranchVocabulariesResultDto
        {
            Vocabularies = vocab,
            StudentsRenamed = renamed
        });
    }

    /// <summary>How many students hold each value — what the editor asks before offering to delete one.</summary>
    [HttpGet("branches/{branchId:guid}/students/vocabularies/usage")]
    [RequirePermission(Permissions.StudentsView)]
    [ProducesResponseType(typeof(Dictionary<string, List<VocabularyUsageDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVocabularyUsage(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var rows = await _context.Students
            .Where(s => s.BranchId == branchId && s.IsActive)
            .Select(s => new { s.ClassName, s.House, s.DormitoryOrStream })
            .ToListAsync();

        static List<VocabularyUsageDto> Count(IEnumerable<string?> values) =>
            values.Where(v => !string.IsNullOrWhiteSpace(v))
                  .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
                  .Select(g => new VocabularyUsageDto { Name = g.Key, StudentCount = g.Count() })
                  .OrderByDescending(u => u.StudentCount)
                  .ToList();

        return Ok(new Dictionary<string, List<VocabularyUsageDto>>
        {
            ["classes"] = Count(rows.Select(r => r.ClassName)),
            ["houses"] = Count(rows.Select(r => r.House)),
            ["dormitories"] = Count(rows.Select(r => r.DormitoryOrStream))
        });
    }

    private static string? ValidateList(List<VocabularyItemDto> items, string label)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                return $"A {label} needs a name";
            if (item.Name.Trim().Length > 100)
                return $"A {label} name cannot exceed 100 characters";
            if (!string.IsNullOrWhiteSpace(item.Color) && !IsHexColor(item.Color))
                return $"'{item.Color}' is not a valid colour";
            item.Name = item.Name.Trim();
        }

        var duplicate = items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return duplicate != null ? $"'{duplicate.Key}' is listed twice" : null;
    }

    private static bool IsHexColor(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

    /// <summary>
    /// The class-teacher half of a class rename. Deliberately its own method rather than a
    /// generic over both types: <see cref="ApplyRenames"/> is written against <c>Student</c>, and
    /// making it generic to save eight lines would obscure the one thing that matters here —
    /// that an assignment renamed out of step with its students stops matching anybody, silently.
    /// </summary>
    private static int ApplyAssignmentRenames(
        List<ClassTeacherAssignment> assignments,
        Dictionary<string, string>? renames)
    {
        if (renames == null || renames.Count == 0) return 0;

        var touched = 0;
        foreach (var (oldName, newName) in renames)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) continue;
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) continue;

            foreach (var a in assignments.Where(a => string.Equals(a.ClassName, oldName, StringComparison.OrdinalIgnoreCase)))
            {
                a.ClassName = newName.Trim();
                a.UpdatedAt = DateTime.UtcNow;
                touched++;
            }
        }

        return touched;
    }

    private static int ApplyRenames(
        List<Student> students,
        Dictionary<string, string>? renames,
        Func<Student, string?> read,
        Action<Student, string> write)
    {
        if (renames == null || renames.Count == 0) return 0;

        var touched = 0;
        foreach (var (oldName, newName) in renames)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) continue;
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) continue;

            foreach (var student in students.Where(s => string.Equals(read(s), oldName, StringComparison.OrdinalIgnoreCase)))
            {
                write(student, newName.Trim());
                touched++;
            }
        }
        return touched;
    }

    /// <summary>
    /// A value a student still holds that is no longer on the list. Inactive entries still count as
    /// present: retiring a class is how you stop it being offered on new records WITHOUT stranding
    /// the students already in it, which is the whole reason IsActive exists separately from delete.
    /// </summary>
    private static string? FindOrphaned(
        List<Student> students,
        Func<Student, string?> read,
        List<VocabularyItemDto> items,
        string label)
    {
        var known = items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphan = students
            .Where(s => s.IsActive && !string.IsNullOrWhiteSpace(read(s)) && !known.Contains(read(s)!))
            .GroupBy(s => read(s)!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (orphan == null) return null;

        var n = orphan.Count();
        return n == 1
            ? $"1 student is still in {label} '{orphan.Key}'. Rename it instead of removing it, or move that student first."
            : $"{n} students are still in {label} '{orphan.Key}'. Rename it instead of removing it, or move them first.";
    }

    private static List<string> CleanSuggestions(List<string>? values) =>
        (values ?? new())
            .Select(v => v?.Trim() ?? "")
            .Where(v => v.Length > 0 && v.Length <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Sorts "S.1, S.2, S.10" rather than "S.1, S.10, S.2". Class names are almost always a prefix
    /// plus a number, and plain alphabetical ordering of them is wrong in the one way a school
    /// notices immediately.
    /// </summary>
    private static readonly IComparer<string> NaturalOrder = Comparer<string>.Create((a, b) =>
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                var na = long.Parse(a[si..i]);
                var nb = long.Parse(b[sj..j]);
                if (na != nb) return na.CompareTo(nb);
            }
            else
            {
                var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    });
}
