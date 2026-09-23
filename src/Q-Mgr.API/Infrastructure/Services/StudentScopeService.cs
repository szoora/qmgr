using QMgr.API.Application.Services;
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
/// How much of a student's file this caller may see (duty rota plan §5.3). A <b>tier</b>, not a yes/no.
/// </summary>
public enum StudentAccessTier
{
    /// <summary>Out of scope: 404, never 403.</summary>
    None = 0,
    /// <summary>
    /// A live SubjectTeacher assignment for the student's class and no pastoral one: name, photo, class,
    /// the subjects this teacher teaches them, their own lesson registers, and learning-support notes when
    /// the tenant shares them — no guardians, welfare, discipline or pastoral tier.
    /// </summary>
    Teaching = 1,
    /// <summary>A live ClassTeacher or Assistant assignment for the student's class: everything the role's permissions allow.</summary>
    Pastoral = 2,
    /// <summary>A role whose DataScope is Organization (admin, DoS, deputy), or the platform administrator: today's behaviour.</summary>
    Unscoped = 3
}

/// <summary>
/// The single home for "which students may this caller see, and how much of them?".
///
/// Q-Mgr's RBAC gates ACTIONS — <c>welfare.view</c> means "may read welfare records", full stop.
/// It cannot express "may read welfare records FOR THESE STUDENTS". A role whose
/// <see cref="RoleDataScope"/> is <see cref="RoleDataScope.AssignedClasses"/> sees only students whose
/// ClassName matches one of its live <see cref="ClassTeacherAssignment"/> rows — and since the duty rota
/// plan (2026-09-17) that sight has two TIERS:
/// <list type="bullet">
/// <item><b>Pastoral</b> — ClassTeacher and Assistant assignments. Every existing method below that does
///   not name a tier (<see cref="ApplyAsync"/>, <see cref="GetVisibleStudentIdsAsync"/>,
///   <see cref="CanSeeStudentAsync"/>, <see cref="VerifyStudentAccessAsync"/>) means PASTORAL, so every
///   welfare, flag, guardian and picture path stays closed to a subject teacher without a line of change.</item>
/// <item><b>Teaching</b> — SubjectTeacher assignments. Reached only through the methods that say so
///   (<see cref="ApplyAnyTierAsync"/>, <see cref="GetTierAsync"/>, <see cref="GetTiersAsync"/>,
///   <see cref="VerifyAnyTierAccessAsync"/>), used by the few roster reads that blank fields by tier.</item>
/// </list>
/// The old tier-blind <c>GetClassNamesAsync</c> was deleted, not renamed, so every caller became a compile
/// error that had to choose <see cref="GetPastoralClassNamesAsync"/> or <see cref="GetTeachingClassNamesAsync"/>.
///
/// ONE home, deliberately. Do not write a second "which classes does this user teach" helper next to the
/// code that needs one — call this.
///
/// Registered SCOPED and memoised per request. It is deliberately NOT cached alongside permissions: a
/// teacher removed from a class must lose access on the very next request (ASVS 8.3.2).
/// </summary>
public interface IStudentScopeService
{
    /// <summary>
    /// True when the caller sees every row their permissions and tenant already allow — SuperAdmin, and any
    /// role whose DataScope is Organization. The common case, so it short-circuits first everywhere.
    /// </summary>
    Task<bool> IsUnscopedAsync();

    /// <summary>
    /// The caller's live PASTORAL class names (class teacher or assistant) in this branch, as spelled in the
    /// vocabulary. Empty for an unscoped caller — check <see cref="IsUnscopedAsync"/> first; an empty list
    /// from a SCOPED caller means "no pastoral classes", which is not the same thing.
    /// </summary>
    Task<IReadOnlyList<string>> GetPastoralClassNamesAsync(Guid branchId);

    /// <summary>The caller's live SUBJECT-TEACHER class names in this branch. Same empty-list caveat.</summary>
    Task<IReadOnlyList<string>> GetTeachingClassNamesAsync(Guid branchId);

    /// <summary>
    /// Narrows a student query to the caller's PASTORAL students. FAILS CLOSED: a scoped caller with no
    /// pastoral assignment gets an empty queryable, never an unfiltered one.
    /// </summary>
    Task<IQueryable<Student>> ApplyAsync(IQueryable<Student> query, Guid branchId);

    /// <summary>
    /// Narrows a student query to pastoral ∪ teaching students. Only for reads that then blank fields by
    /// <see cref="GetTiersAsync"/> — a caller that forgets to blank has leaked the pastoral tier.
    /// </summary>
    Task<IQueryable<Student>> ApplyAnyTierAsync(IQueryable<Student> query, Guid branchId);

    /// <summary>
    /// The PASTORAL student ids the caller may see in this branch. Null means "unscoped, do not filter"; an
    /// empty set means "sees nothing".
    /// </summary>
    Task<HashSet<Guid>?> GetVisibleStudentIdsAsync(Guid branchId);

    /// <summary>The tier this caller holds for one student.</summary>
    Task<StudentAccessTier> GetTierAsync(Guid branchId, Guid studentId);

    /// <summary>
    /// The tier for every student the caller can see at all in this branch. Null means unscoped (every
    /// student at <see cref="StudentAccessTier.Unscoped"/>); a student missing from the map is None.
    /// </summary>
    Task<Dictionary<Guid, StudentAccessTier>?> GetTiersAsync(Guid branchId);

    /// <summary>PASTORAL guard for the by-ID paths. Null when allowed, otherwise 404 — never Forbid.</summary>
    Task<IActionResult?> VerifyStudentAccessAsync(Guid branchId, Guid studentId);

    /// <summary>Any-tier guard for the few by-ID reads that blank by tier. Null when allowed, otherwise 404.</summary>
    Task<IActionResult?> VerifyAnyTierAccessAsync(Guid branchId, Guid studentId);

    /// <summary>True when the caller has PASTORAL sight of this student (or is unscoped).</summary>
    Task<bool> CanSeeStudentAsync(Guid branchId, Guid studentId);
}

public class StudentScopeService : IStudentScopeService
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<StudentScopeService> _logger;

    // Per-request memoisation: long enough that one action calling several guards does not pay for the
    // lookup repeatedly, short enough that an assignment ended a second ago is honoured next request.
    private bool? _isUnscoped;
    private readonly Dictionary<Guid, IReadOnlyList<string>> _pastoralNamesByBranch = new();
    private readonly Dictionary<Guid, IReadOnlyList<string>> _teachingNamesByBranch = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _pastoralIdsByBranch = new();
    private readonly Dictionary<Guid, Dictionary<Guid, StudentAccessTier>> _tiersByBranch = new();

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
            // No user on the request: API-key authentication (auth_method=api_key), which has no user row
            // and therefore no role to carry a scope — it is constrained by its own scope claims.
            var isApiKey = _httpContextAccessor.HttpContext?.User?.FindFirst("auth_method")?.Value == "api_key";
            if (!isApiKey)
                _logger.LogWarning("Student scope could not resolve a user ID on an authenticated request; treating the caller as scoped-to-nothing");
            return (_isUnscoped = isApiKey).Value;
        }

        var role = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new
            {
                Scope = (RoleDataScope?)u.Role.DataScope,
                Permissions = u.Role.RolePermissions.Select(rp => rp.Permission.Code).ToList()
            })
            .FirstOrDefaultAsync();

        // A user row that cannot be found or is inactive resolves to null. FAIL CLOSED.
        if (role?.Scope == null) return (_isUnscoped = false).Value;

        // A POST GRANTS PERMISSIONS AND SCOPE AS A PAIR (plan §2.6, and the audit's most serious
        // finding). This used to be a bare `scope == Organization`, which was correct only while a
        // pastoral post granted no permission: three seeded roles are organization-scoped and two of
        // them — support-staff and viewer — hold no welfare permission at all, so the MISSING
        // PERMISSION was the only thing keeping a matron who is the class teacher of S4B from
        // reading every child in the school. Deriving the permission removes exactly that lock, and
        // no controller can catch it: ApplyAsync below returns the query whole for an unscoped
        // caller, so all 29 guards in WelfareController are passed by construction.
        //
        // So: organization-wide only if the ROLE ITSELF reaches students. Where the welfare grant
        // arrived with the post, it runs at the post's reach — the classes held. A Tenant Admin or
        // Manager who also teaches a class keeps the school, because their role already granted it.
        var posts = await PostPermissionService.GrantsForAsync(_context, userId);
        _isUnscoped = PostPermissionService.IsUnscopedOnStudents(role.Scope.Value, role.Permissions, posts);
        return _isUnscoped.Value;
    }

    public Task<IReadOnlyList<string>> GetPastoralClassNamesAsync(Guid branchId)
        => ClassNamesAsync(branchId, pastoral: true);

    public Task<IReadOnlyList<string>> GetTeachingClassNamesAsync(Guid branchId)
        => ClassNamesAsync(branchId, pastoral: false);

    private async Task<IReadOnlyList<string>> ClassNamesAsync(Guid branchId, bool pastoral)
    {
        var cache = pastoral ? _pastoralNamesByBranch : _teachingNamesByBranch;
        if (cache.TryGetValue(branchId, out var cached)) return cached;

        if (await IsUnscopedAsync()) return cache[branchId] = Array.Empty<string>();

        var userId = CurrentUserId();
        if (userId == Guid.Empty) return cache[branchId] = Array.Empty<string>();

        // The tier is the assignment's Role, and it is chosen HERE and nowhere else.
        var query = _context.ClassTeacherAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.BranchId == branchId && a.EndedAt == null);
        query = pastoral
            ? query.Where(a => a.Role == ClassTeacherRole.ClassTeacher || a.Role == ClassTeacherRole.Assistant)
            : query.Where(a => a.Role == ClassTeacherRole.SubjectTeacher);

        var names = await query.Select(a => a.ClassName).Distinct().ToListAsync();
        return cache[branchId] = names;
    }

    private static IQueryable<Student> FilterByClasses(IQueryable<Student> query, IReadOnlyCollection<string> names)
    {
        // FAIL CLOSED: an empty allow-list is an explicit empty result, never a no-op WHERE.
        if (names.Count == 0) return query.Where(_ => false);
        var normalized = names.Select(n => n.Trim().ToLower()).Distinct().ToList();
        // Trim().ToLower() on both sides: Student.ClassName is free text, the vocabulary is admin-typed.
        return query.Where(s => s.ClassName != null && normalized.Contains(s.ClassName.Trim().ToLower()));
    }

    public async Task<IQueryable<Student>> ApplyAsync(IQueryable<Student> query, Guid branchId)
    {
        if (await IsUnscopedAsync()) return query;
        return FilterByClasses(query, await GetPastoralClassNamesAsync(branchId));
    }

    public async Task<IQueryable<Student>> ApplyAnyTierAsync(IQueryable<Student> query, Guid branchId)
    {
        if (await IsUnscopedAsync()) return query;
        var names = (await GetPastoralClassNamesAsync(branchId)).Concat(await GetTeachingClassNamesAsync(branchId)).ToList();
        return FilterByClasses(query, names);
    }

    public async Task<HashSet<Guid>?> GetVisibleStudentIdsAsync(Guid branchId)
    {
        if (await IsUnscopedAsync()) return null;
        if (_pastoralIdsByBranch.TryGetValue(branchId, out var cached)) return cached;

        var tiers = await GetTiersAsync(branchId);
        var ids = tiers!.Where(t => t.Value == StudentAccessTier.Pastoral).Select(t => t.Key).ToHashSet();
        return _pastoralIdsByBranch[branchId] = ids;
    }

    public async Task<Dictionary<Guid, StudentAccessTier>?> GetTiersAsync(Guid branchId)
    {
        if (await IsUnscopedAsync()) return null;
        if (_tiersByBranch.TryGetValue(branchId, out var cached)) return cached;

        var pastoral = (await GetPastoralClassNamesAsync(branchId)).Select(n => n.Trim().ToLower()).ToHashSet();
        var teaching = (await GetTeachingClassNamesAsync(branchId)).Select(n => n.Trim().ToLower()).ToHashSet();
        var any = pastoral.Concat(teaching).ToList();
        if (any.Count == 0) return _tiersByBranch[branchId] = new Dictionary<Guid, StudentAccessTier>();

        // INACTIVE STUDENTS INCLUDED on purpose: a child who has left still has a ledger.
        var students = await _context.Students
            .AsNoTracking()
            .Where(s => s.BranchId == branchId && s.ClassName != null && any.Contains(s.ClassName.Trim().ToLower()))
            .Select(s => new { s.Id, s.ClassName })
            .ToListAsync();

        // One user, both tiers: pastoral wins for the classes they are pastoral for (plan §5.3 — tiers are
        // per assignment, never per role).
        var map = students.ToDictionary(
            s => s.Id,
            s => pastoral.Contains(s.ClassName!.Trim().ToLower()) ? StudentAccessTier.Pastoral : StudentAccessTier.Teaching);
        return _tiersByBranch[branchId] = map;
    }

    public async Task<StudentAccessTier> GetTierAsync(Guid branchId, Guid studentId)
    {
        var tiers = await GetTiersAsync(branchId);
        if (tiers == null) return StudentAccessTier.Unscoped;
        return tiers.TryGetValue(studentId, out var tier) ? tier : StudentAccessTier.None;
    }

    public async Task<bool> CanSeeStudentAsync(Guid branchId, Guid studentId)
    {
        var tier = await GetTierAsync(branchId, studentId);
        return tier is StudentAccessTier.Pastoral or StudentAccessTier.Unscoped;
    }

    public async Task<IActionResult?> VerifyStudentAccessAsync(Guid branchId, Guid studentId)
        => await CanSeeStudentAsync(branchId, studentId) ? null : NotFoundStudent();

    public async Task<IActionResult?> VerifyAnyTierAccessAsync(Guid branchId, Guid studentId)
        => await GetTierAsync(branchId, studentId) != StudentAccessTier.None ? null : NotFoundStudent();

    private static IActionResult NotFoundStudent() => new NotFoundObjectResult(new ProblemDetails
    {
        Title = "Student not found",
        Status = StatusCodes.Status404NotFound
    });
}
