using QMgr.API.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// The single home for "which OTHER members of staff may this caller see?" — the staff-axis twin
/// of <see cref="IStudentScopeService"/>, mirrored rather than forked, including the things that
/// are easy to break:
///
///  - <see cref="StaffDataScope.Organization"/> sees everyone the tenant and permissions allow.
///  - <see cref="StaffDataScope.AssignedDepartments"/> sees members of the departments the caller
///    heads or deputises. FAILS CLOSED: a head with no department sees nobody.
///  - <see cref="StaffDataScope.DirectReports"/> sees staff whose LineManagerUserId is the caller.
///  - <see cref="StaffDataScope.SelfOnly"/> sees nobody else.
///
/// SELF IS ALWAYS VISIBLE AND IS NOT SCOPE. Every caller reads their own file through the portal
/// regardless of role; this service governs reading OTHER PEOPLE. For convenience the visible set
/// includes the caller, so a scoped search still returns their own rows.
///
/// Registered SCOPED and memoised per request, never in the five-minute permission cache: a head
/// removed from a department must lose their staff on the very next request. Out of scope is 404,
/// never 403 — a 403 confirms the person exists.
/// </summary>
public interface IStaffScopeService
{
    /// <summary>The caller's user id, or Guid.Empty for an API-key or unresolvable request.</summary>
    Guid CurrentUserId { get; }

    /// <summary>True for SuperAdmin, API-key callers and any role whose StaffScope is Organization.</summary>
    Task<bool> IsUnscopedAsync();

    Task<StaffDataScope> GetScopeAsync();

    /// <summary>
    /// The user ids the caller may see in this branch. Null = unscoped, do not filter; an empty set
    /// = sees nobody (the caller themselves is always included for a scoped caller).
    /// </summary>
    Task<HashSet<Guid>?> GetVisibleUserIdsAsync(Guid branchId);

    /// <summary>The departments a scoped caller's view covers, for "these figures cover Maths and Physics only". Empty for an unscoped caller.</summary>
    Task<IReadOnlyList<string>> GetScopedDepartmentNamesAsync();

    /// <summary>The department ids the caller heads or deputises. Empty for anyone else.</summary>
    Task<IReadOnlyList<Guid>> GetHeadedDepartmentIdsAsync();

    /// <summary>Narrows a record query. FAILS CLOSED on an empty allow-list.</summary>
    Task<IQueryable<StaffPerformanceRecord>> ApplyAsync(IQueryable<StaffPerformanceRecord> query, Guid branchId);

    /// <summary>Narrows a staff (User) query the same way.</summary>
    Task<IQueryable<User>> ApplyToStaffAsync(IQueryable<User> query, Guid branchId);

    Task<bool> CanSeeStaffAsync(Guid branchId, Guid userId);

    /// <summary>Null when allowed, otherwise NotFound. Never Forbid.</summary>
    Task<IActionResult?> VerifyStaffAccessAsync(Guid branchId, Guid userId);
}

public class StaffScopeService : IStaffScopeService
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<StaffScopeService> _logger;

    private StaffDataScope? _scope;
    private bool? _isUnscoped;
    private IReadOnlyList<Guid>? _headedDepartments;
    private IReadOnlyList<string>? _scopedDepartmentNames;
    private readonly Dictionary<Guid, HashSet<Guid>> _visibleByBranch = new();

    public StaffScopeService(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IHttpContextAccessor httpContextAccessor,
        ILogger<StaffScopeService> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public Guid CurrentUserId
    {
        get
        {
            var raw = _httpContextAccessor.HttpContext?.User?.Claims
                .FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(raw, out var uid) ? uid : Guid.Empty;
        }
    }

    public async Task<StaffDataScope> GetScopeAsync()
    {
        if (_scope.HasValue) return _scope.Value;

        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole))
            return (_scope = StaffDataScope.Organization).Value;

        var userId = CurrentUserId;
        if (userId == Guid.Empty)
        {
            var isApiKey = _httpContextAccessor.HttpContext?.User?.FindFirst("auth_method")?.Value == "api_key";
            if (!isApiKey)
                _logger.LogWarning("Staff scope could not resolve a user ID on an authenticated request; treating the caller as SelfOnly");
            return (_scope = isApiKey ? StaffDataScope.Organization : StaffDataScope.SelfOnly).Value;
        }

        var scope = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => (StaffDataScope?)u.Role.StaffScope)
            .FirstOrDefaultAsync();

        // A missing or inactive user FAILS CLOSED to SelfOnly.
        return (_scope = scope ?? StaffDataScope.SelfOnly).Value;
    }

    public async Task<bool> IsUnscopedAsync()
    {
        if (_isUnscoped.HasValue) return _isUnscoped.Value;
        return (_isUnscoped = await GetScopeAsync() == StaffDataScope.Organization).Value;
    }

    public async Task<IReadOnlyList<Guid>> GetHeadedDepartmentIdsAsync()
    {
        if (_headedDepartments != null) return _headedDepartments;
        var userId = CurrentUserId;
        if (userId == Guid.Empty) return _headedDepartments = Array.Empty<Guid>();

        return _headedDepartments = await _context.Departments
            .AsNoTracking()
            .Where(d => d.IsActive && (d.HeadUserId == userId || d.DeputyHeadUserId == userId))
            .Select(d => d.Id)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<string>> GetScopedDepartmentNamesAsync()
    {
        if (_scopedDepartmentNames != null) return _scopedDepartmentNames;
        // An ORGANIZATION-scoped caller is not scoped to departments at all, so there is nothing to
        // name. Everybody else who heads a department is scoped to it — see ComputeVisibleAsync:
        // this no longer requires the ROLE to say AssignedDepartments, because since 2026-09-22 the
        // POST is what grants the reach and the seeded head-of-department role is gone.
        if (await GetScopeAsync() == StaffDataScope.Organization)
            return _scopedDepartmentNames = Array.Empty<string>();

        var ids = await GetHeadedDepartmentIdsAsync();
        if (ids.Count == 0) return _scopedDepartmentNames = Array.Empty<string>();

        return _scopedDepartmentNames = await _context.Departments
            .AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => d.Name)
            .ToListAsync();
    }

    public async Task<HashSet<Guid>?> GetVisibleUserIdsAsync(Guid branchId)
    {
        var scope = await GetScopeAsync();
        if (scope == StaffDataScope.Organization) return null;

        if (_visibleByBranch.TryGetValue(branchId, out var cached)) return cached;

        var visible = await ComputeVisibleAsync(_context, CurrentUserId, scope, await GetHeadedDepartmentIdsAsync());
        return _visibleByBranch[branchId] = visible!;
    }

    /// <summary>
    /// The visible set for a user who is NOT the current request's caller — a background job's recipient (the weekly lesson
    /// analysis). The same rule as <see cref="GetVisibleUserIdsAsync"/>, computed without an HTTP context: there is one
    /// rule, not two. Null = unscoped. Fails closed: an inactive or unknown user sees nobody.
    /// </summary>
    public static async Task<HashSet<Guid>?> VisibleUserIdsForAsync(QMgrDbContext db, Guid userId, CancellationToken ct = default)
    {
        var scope = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => (StaffDataScope?)u.Role.StaffScope)
            .FirstOrDefaultAsync(ct);
        if (scope == null) return new HashSet<Guid>();
        if (scope == StaffDataScope.Organization) return null;
        var headed = await db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.IsActive && (d.HeadUserId == userId || d.DeputyHeadUserId == userId))
            .Select(d => d.Id).ToListAsync(ct);
        return await ComputeVisibleAsync(db, userId, scope.Value, headed, ct);
    }

    private static async Task<HashSet<Guid>?> ComputeVisibleAsync(QMgrDbContext db, Guid me, StaffDataScope scope, IReadOnlyList<Guid> headedDepartments, CancellationToken ct = default)
    {
        if (scope == StaffDataScope.Organization) return null;
        var visible = new HashSet<Guid>();
        if (me != Guid.Empty) visible.Add(me);

        // THE DEPARTMENT POST GRANTS ITS OWN REACH, WHATEVER THE ROLE SAYS (plan §2.6).
        // This used to sit inside `case AssignedDepartments:`, which read the ROLE — and only the
        // seeded head-of-department role carried that value. Deleting that role (plan §5 decision 3)
        // drops its holders onto `teacher` or `staff`, both SelfOnly, so a derived
        // `staff.records.view` would have opened a Records page showing the caller and nobody else:
        // a permission with no reach is a dud, and the mirror of the student-axis leak.
        //
        // It is unioned rather than switched on, so a DirectReports line manager who also heads a
        // department sees both. FAIL CLOSED is unchanged: an empty list adds nobody.
        if (headedDepartments.Count > 0 && scope != StaffDataScope.Organization)
        {
            var headed = headedDepartments.ToArray();
            // Inactive staff INCLUDED, as inactive students are on the student axis: someone who has
            // left still has a file, and a head's view of it must not silently differ.
            var members = await db.Users
                .AsNoTracking()
                .Where(u => u.DepartmentIds != null && u.DepartmentIds.Any(id => headed.Contains(id)))
                .Select(u => u.Id)
                .ToListAsync(ct);
            foreach (var id in members) visible.Add(id);
        }

        switch (scope)
        {
            case StaffDataScope.AssignedDepartments:
                // Handled by the department-post union above, which covers this and every other
                // non-Organization scope. A second copy of that query here is exactly the drift this
                // codebase keeps paying for, so the case is left declaring the intent and nothing
                // more: for this scope the union IS the whole answer, and it still fails closed —
                // a head with no department adds nobody and keeps only themselves.
                break;
            case StaffDataScope.DirectReports:
            {
                if (me == Guid.Empty) break;
                var ids = await db.Users
                    .AsNoTracking()
                    .Where(u => u.LineManagerUserId == me)
                    .Select(u => u.Id)
                    .ToListAsync(ct);
                foreach (var id in ids) visible.Add(id);
                break;
            }
            case StaffDataScope.SelfOnly:
            default:
                break;
        }
        return visible;
    }

    public async Task<IQueryable<StaffPerformanceRecord>> ApplyAsync(IQueryable<StaffPerformanceRecord> query, Guid branchId)
    {
        var visible = await GetVisibleUserIdsAsync(branchId);
        if (visible == null) return query;
        // FAIL CLOSED. An explicit short-circuit, not a reliance on EF translating Contains over an
        // empty list into a false predicate.
        if (visible.Count == 0) return query.Where(_ => false);
        var list = visible.ToList();
        return query.Where(r => list.Contains(r.SubjectUserId));
    }

    public async Task<IQueryable<User>> ApplyToStaffAsync(IQueryable<User> query, Guid branchId)
    {
        var visible = await GetVisibleUserIdsAsync(branchId);
        if (visible == null) return query;
        if (visible.Count == 0) return query.Where(_ => false);
        var list = visible.ToList();
        return query.Where(u => list.Contains(u.Id));
    }

    public async Task<bool> CanSeeStaffAsync(Guid branchId, Guid userId)
    {
        if (userId != Guid.Empty && userId == CurrentUserId) return true;
        var visible = await GetVisibleUserIdsAsync(branchId);
        return visible == null || visible.Contains(userId);
    }

    public async Task<IActionResult?> VerifyStaffAccessAsync(Guid branchId, Guid userId)
    {
        if (await CanSeeStaffAsync(branchId, userId)) return null;

        // The same wording an unknown id gets. A distinct "not your department" message would
        // confirm the person is on the staff list.
        return new NotFoundObjectResult(new ProblemDetails
        {
            Title = "Staff member not found",
            Status = StatusCodes.Status404NotFound
        });
    }
}
