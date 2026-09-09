using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// The single home for "which students may this caller see?".
///
/// Q-Mgr's RBAC gates ACTIONS — <c>welfare.view</c> means "may read welfare records", full stop.
/// It cannot express "may read welfare records FOR THESE STUDENTS", and every welfare and roster
/// query filtered on BranchId and nothing narrower. This is the missing second axis: a role whose
/// <see cref="RoleDataScope"/> is <see cref="RoleDataScope.AssignedClasses"/> sees only students
/// whose ClassName matches one of its live <see cref="ClassTeacherAssignment"/> rows.
///
/// ONE home, deliberately. This codebase's recurring failure is a second copy of a rule that then
/// drifts from the first (the DTO duplications, the guardian-restriction copy). Do not write a
/// second "which classes does this user teach" helper next to the code that needs one — call this.
///
/// Registered SCOPED and memoised per request. It is deliberately NOT put in the five-minute
/// IMemoryCache that PermissionAuthorizationHandler uses for permissions: a teacher removed from a
/// class would keep reading that class for up to five minutes, and class membership changes far
/// more often than a permission set does. Two indexed lookups per request is the right price.
/// </summary>
public interface IStudentScopeService
{
    /// <summary>
    /// True when the caller sees every row their permissions and tenant already allow — SuperAdmin,
    /// and any role whose DataScope is Organization (which is every role that existed before this
    /// feature). The overwhelmingly common case, so it short-circuits first everywhere.
    /// </summary>
    Task<bool> IsUnscopedAsync();

    /// <summary>
    /// The caller's live class names in this branch, as spelled in the branch vocabulary. Empty for
    /// an unscoped caller — check <see cref="IsUnscopedAsync"/> first; an empty list from a SCOPED
    /// caller means "sees nothing", which is not the same thing.
    /// </summary>
    Task<IReadOnlyList<string>> GetClassNamesAsync(Guid branchId);

    /// <summary>
    /// The only correct way to narrow a student query. FAILS CLOSED: a scoped caller with no
    /// assignments gets an empty queryable, never an unfiltered one.
    /// </summary>
    Task<IQueryable<Student>> ApplyAsync(IQueryable<Student> query, Guid branchId);

    /// <summary>
    /// Narrows any query that can reach a Student — welfare records, flags, guardians — by
    /// producing the set of student IDs the caller may see in this branch. Null means "unscoped,
    /// do not filter"; an empty set means "sees nothing".
    /// </summary>
    Task<HashSet<Guid>?> GetVisibleStudentIdsAsync(Guid branchId);

    /// <summary>
    /// Guard for the by-ID paths. Returns null when access is allowed, otherwise a NotFound result
    /// — never Forbid. An out-of-scope student must read identically to one that does not exist,
    /// the same 404-not-403 shape this app already uses for confidential records and other
    /// people's drafts; a 403 would confirm the student exists, which is itself a disclosure.
    /// </summary>
    Task<IActionResult?> VerifyStudentAccessAsync(Guid branchId, Guid studentId);

    /// <summary>
    /// True when the caller may see this student. The predicate behind
    /// <see cref="VerifyStudentAccessAsync"/>, exposed for call sites that need to make their own
    /// decision (the alert fan-out, a bulk validation loop).
    /// </summary>
    Task<bool> CanSeeStudentAsync(Guid branchId, Guid studentId);
}

public class StudentScopeService : IStudentScopeService
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<StudentScopeService> _logger;

    // Per-request memoisation. This service is scoped, so these live exactly as long as one HTTP
    // request — long enough that a controller action calling ApplyAsync and then
    // VerifyStudentAccessAsync twice does not pay for the lookup three times, and short enough that
    // an assignment ended a second ago is honoured by the very next request.
    private bool? _isUnscoped;
    private readonly Dictionary<Guid, IReadOnlyList<string>> _classNamesByBranch = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _visibleStudentIdsByBranch = new();

    public StudentScopeService(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IHttpContextAccessor httpContextAccessor,
        ILogger<StudentScopeService> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private Guid CurrentUserId()
    {
        var raw = _httpContextAccessor.HttpContext?.User?.Claims
            .FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : Guid.Empty;
    }

    public async Task<bool> IsUnscopedAsync()
    {
        if (_isUnscoped.HasValue) return _isUnscoped.Value;

        // SuperAdmin bypasses, exactly as it does in PermissionAuthorizationHandler.
        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole))
            return (_isUnscoped = true).Value;

        var userId = CurrentUserId();
        if (userId == Guid.Empty)
        {
            // No user on the request. This is API-key authentication (auth_method=api_key), which
            // has no user row and therefore no role to carry a scope — it is already constrained by
            // its own scope claims. Treating it as scoped would break every integration; treating
            // it as unscoped is what it has always been.
            var isApiKey = _httpContextAccessor.HttpContext?.User?.FindFirst("auth_method")?.Value == "api_key";
            if (!isApiKey)
                _logger.LogWarning("Student scope could not resolve a user ID on an authenticated request; treating the caller as scoped-to-nothing");
            return (_isUnscoped = isApiKey).Value;
        }

        var scope = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => (RoleDataScope?)u.Role.DataScope)
            .FirstOrDefaultAsync();

        // A user row that cannot be found or is inactive resolves to null. FAIL CLOSED: scoped, and
        // with no assignments that means nothing at all.
        _isUnscoped = scope == RoleDataScope.Organization;
        return _isUnscoped.Value;
    }

    public async Task<IReadOnlyList<string>> GetClassNamesAsync(Guid branchId)
    {
        if (_classNamesByBranch.TryGetValue(branchId, out var cached)) return cached;

        if (await IsUnscopedAsync())
            return _classNamesByBranch[branchId] = Array.Empty<string>();

        var userId = CurrentUserId();
        if (userId == Guid.Empty)
            return _classNamesByBranch[branchId] = Array.Empty<string>();

        var names = await _context.ClassTeacherAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.BranchId == branchId && a.EndedAt == null)
            .Select(a => a.ClassName)
            .Distinct()
            .ToListAsync();

        return _classNamesByBranch[branchId] = names;
    }

    public async Task<IQueryable<Student>> ApplyAsync(IQueryable<Student> query, Guid branchId)
    {
        if (await IsUnscopedAsync()) return query;

        var names = await GetClassNamesAsync(branchId);

        // FAIL CLOSED. The natural bug here is letting an empty allow-list collapse into a no-op
        // WHERE — `names.Contains(x)` over an empty list does produce a false predicate in EF, but
        // relying on that is relying on a translation detail. An explicit short-circuit means a
        // brand-new class teacher with no assignment yet can never be handed the whole school roll.
        if (names.Count == 0) return query.Where(_ => false);

        var normalized = names.Select(n => n.Trim().ToLower()).ToList();

        // Trim().ToLower() on both sides: Student.ClassName is free text typed by whoever built the
        // roster, and the vocabulary is typed by an administrator. "S4B" and "s4b " are one class.
        return query.Where(s => s.ClassName != null && normalized.Contains(s.ClassName.Trim().ToLower()));
    }

    public async Task<HashSet<Guid>?> GetVisibleStudentIdsAsync(Guid branchId)
    {
        if (await IsUnscopedAsync()) return null;

        if (_visibleStudentIdsByBranch.TryGetValue(branchId, out var cached)) return cached;

        var names = await GetClassNamesAsync(branchId);
        if (names.Count == 0)
            return _visibleStudentIdsByBranch[branchId] = new HashSet<Guid>();

        var normalized = names.Select(n => n.Trim().ToLower()).ToList();

        // INACTIVE STUDENTS INCLUDED on purpose. A child who has left still has a ledger, and the
        // welfare timeline deliberately serves records for retired roster rows — filtering to
        // active here would make a class teacher's view of their own class silently differ from
        // everyone else's for exactly the students most likely to matter.
        var ids = await _context.Students
            .AsNoTracking()
            .Where(s => s.BranchId == branchId && s.ClassName != null && normalized.Contains(s.ClassName.Trim().ToLower()))
            .Select(s => s.Id)
            .ToListAsync();

        return _visibleStudentIdsByBranch[branchId] = ids.ToHashSet();
    }

    public async Task<bool> CanSeeStudentAsync(Guid branchId, Guid studentId)
    {
        var visible = await GetVisibleStudentIdsAsync(branchId);
        return visible == null || visible.Contains(studentId);
    }

    public async Task<IActionResult?> VerifyStudentAccessAsync(Guid branchId, Guid studentId)
    {
        if (await CanSeeStudentAsync(branchId, studentId)) return null;

        return new NotFoundObjectResult(new ProblemDetails
        {
            Title = "Student not found",
            Status = StatusCodes.Status404NotFound
        });
    }
}
