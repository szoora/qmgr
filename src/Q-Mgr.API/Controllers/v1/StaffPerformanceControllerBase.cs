using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The guards and helpers every Staff Performance controller shares: the branch-ownership check
/// (SuperAdmin bypass, tenant match, generic 404), the caller's id, a memoised permission lookup,
/// the three-rung visibility set, a one-query name lookup and the plain-English ProblemDetails
/// shapes. WelfareController carries these as private members; with six controllers on the staff
/// axis a sixth copy of a security helper is exactly the "second copy that drifts" this codebase
/// keeps finding, so they live once here. Nothing in this class knows about a specific route.
///
/// The visibility rungs are a SET, not a ladder: holding staff.restricted.view without
/// staff.confidential.view is a legitimate (if odd) configuration and must not grant the rung
/// below. Every rung check therefore goes through <see cref="CanSeeAsync"/> or
/// <see cref="VisibleLevelsAsync"/>, never a <c>&lt;= max</c> comparison.
/// </summary>
public abstract class StaffPerformanceControllerBase : ControllerBase
{
    protected readonly QMgrDbContext Db;
    protected readonly ITenantContextAccessor TenantAccessor;
    protected readonly IStaffScopeService StaffScope;
    protected readonly IActivityLogger Activity;

    private readonly Dictionary<string, bool> _permissionCache = new();

    protected StaffPerformanceControllerBase(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity)
    {
        Db = db;
        TenantAccessor = tenantAccessor;
        StaffScope = staffScope;
        Activity = activity;
    }

    // ---- Identity and tenancy -------------------------------------------------------------------

    protected Guid CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : Guid.Empty;
    }

    protected bool IsSuperAdmin => RoleCodes.IsSuperAdmin(TenantAccessor.TenantContext?.UserRole);

    /// <summary>
    /// Copied in shape from WelfareController.VerifyBranchOwnership: a SuperAdmin may reach any
    /// branch that exists; everyone else only a branch of their own organization; the failure is a
    /// generic 404 so a probe cannot learn that a branch id belongs to another tenant.
    /// </summary>
    protected async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = TenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails { Title = "Organization not resolved", Status = StatusCodes.Status401Unauthorized });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var superAdminBranchExists = await Db.Branches.AnyAsync(b => b.Id == branchId);
            return superAdminBranchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
        }

        var branchExists = await Db.Branches.AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        return branchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
    }

    /// <summary>The organization a branch belongs to — the branch's own row for a SuperAdmin, the tenant for everyone else.</summary>
    protected async Task<Guid> ResolveOrganizationIdAsync(Guid branchId)
    {
        var tenantContext = TenantAccessor.TenantContext!;
        return RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? await Db.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync()
            : tenantContext.OrganizationId;
    }

    /// <summary>
    /// The organization for a route with no branch in it (parameters, policy, portal). The tenant
    /// context carries it — a SuperAdmin acting for a tenant sends X-Tenant-Id, which the tenant
    /// middleware resolves into the same place — so there is one answer for both.
    /// </summary>
    protected Guid? CurrentOrganizationId()
    {
        var tenant = TenantAccessor.TenantContext;
        if (tenant == null || !tenant.IsResolved || tenant.OrganizationId == Guid.Empty) return null;
        return tenant.OrganizationId;
    }

    protected IActionResult TenantNotResolved()
        => Unauthorized(new ProblemDetails { Title = "Organization not resolved", Status = StatusCodes.Status401Unauthorized });

    // ---- Permissions and visibility ------------------------------------------------------------

    /// <summary>
    /// Permissions are not JWT claims in this app (see PermissionAuthorizationHandler); they are
    /// resolved by role lookup. Memoised per request because the visibility ceiling is read on
    /// essentially every action.
    /// </summary>
    protected async Task<bool> HasPermissionAsync(string code)
    {
        if (IsSuperAdmin) return true;
        if (_permissionCache.TryGetValue(code, out var cached)) return cached;

        var userId = CurrentUserId();
        var has = userId != Guid.Empty && await Db.Users
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions)
            .AnyAsync(rp => rp.Permission.Code == code);
        return _permissionCache[code] = has;
    }

    protected Task<bool> CanViewConfidentialAsync() => HasPermissionAsync(Permissions.StaffConfidentialView);
    protected Task<bool> CanViewRestrictedAsync() => HasPermissionAsync(Permissions.StaffRestrictedView);

    /// <summary>The set of rungs this caller may read on OTHER people's records. Standard is always in it.</summary>
    protected async Task<List<WelfareVisibility>> VisibleLevelsAsync()
        => StaffPerformanceMapping.VisibleLevels(await CanViewConfidentialAsync(), await CanViewRestrictedAsync()).ToList();

    protected async Task<bool> CanSeeAsync(WelfareVisibility visibility) => visibility switch
    {
        WelfareVisibility.Standard => true,
        WelfareVisibility.Confidential => await CanViewConfidentialAsync(),
        WelfareVisibility.Restricted => await CanViewRestrictedAsync(),
        _ => false // an unrecognised rung fails closed
    };

    /// <summary>The highest rung the caller holds. For "can this caller raise a record to X": X must be a rung they can read.</summary>
    protected async Task<WelfareVisibility> MaxVisibilityAsync()
    {
        if (await CanViewRestrictedAsync()) return WelfareVisibility.Restricted;
        if (await CanViewConfidentialAsync()) return WelfareVisibility.Confidential;
        return WelfareVisibility.Standard;
    }

    /// <summary>
    /// May this caller read this record? The whole rule, once:
    ///  - the SUBJECT reads their own Standard and Confidential records, never Restricted, and never
    ///    somebody else's draft;
    ///  - the AUTHOR reads what they wrote at Standard or Confidential (a forced-Confidential
    ///    observation must not vanish from the person who filed it), and their own drafts;
    ///  - anyone else needs staff.records.view, the rung, and the subject in scope, and never sees
    ///    a draft.
    /// The <see cref="StaffScope"/> service treats self as visible, so the third clause is what
    /// governs other people.
    /// </summary>
    protected async Task<bool> CanReadRecordAsync(StaffPerformanceRecord record, Guid branchId)
    {
        var me = CurrentUserId();
        var isDraft = record.Status == StaffRecordStatus.Draft;

        if (record.LoggedByUserId == me && me != Guid.Empty)
            return record.Visibility != WelfareVisibility.Restricted || await CanViewRestrictedAsync();

        if (isDraft) return false;

        if (record.SubjectUserId == me && me != Guid.Empty)
            return record.Visibility != WelfareVisibility.Restricted;

        return await HasPermissionAsync(Permissions.StaffRecordsView)
               && await CanSeeAsync(record.Visibility)
               && await StaffScope.CanSeeStaffAsync(branchId, record.SubjectUserId);
    }

    // ---- Names -------------------------------------------------------------------------------

    /// <summary>
    /// One Users query for every name a response needs. IgnoreQueryFilters on purpose: the ids were
    /// already produced by a scoped, tenant-filtered query, and a name for a known id (a line manager
    /// who has since left, a SuperAdmin acting on another tenant's branch) is not a leak.
    /// </summary>
    protected Task<StaffPerformanceMapping.NameLookup> BuildNamesAsync(IEnumerable<Guid?> ids)
        => StaffLookups.LoadNamesAsync(Db, ids);

    /// <summary>Every department of the organization by id, including retired ones so an old membership still reads as a name.</summary>
    protected Task<Dictionary<Guid, string>> DepartmentNamesAsync(Guid organizationId)
        => StaffLookups.LoadDepartmentNamesAsync(Db, organizationId);

    // ---- Recognition budget (the records controller and the portal show the same tile) ------------

    protected async Task<RecognitionBudgetDto> BuildRecognitionBudgetAsync(IStaffPerformancePolicyService policyService, Guid organizationId, Guid userId)
    {
        var policy = await policyService.GetAsync(organizationId);
        var parameters = await Db.PerformanceParameters.AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.IsActive && p.Kind == ParameterKind.Recognition)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync();
        return new RecognitionBudgetDto
        {
            MonthlyBudget = policy.RecognitionMonthlyBudget,
            UsedThisMonth = await RecognitionsUsedThisMonthAsync(organizationId, userId),
            Parameters = parameters.Select(StaffPerformanceMapping.ToDto).ToList()
        };
    }

    /// <summary>Recognitions the caller has given this calendar month (UTC). Annulled ones are handed back to the budget.</summary>
    protected async Task<int> RecognitionsUsedThisMonthAsync(Guid organizationId, Guid userId)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await Db.StaffPerformanceRecords
            .CountAsync(r => r.OrganizationId == organizationId && r.LoggedByUserId == userId
                             && r.Source == RecordSource.Recognition && r.Status != StaffRecordStatus.Annulled
                             && r.CreatedAt >= monthStart);
    }

    // ---- Closed periods ------------------------------------------------------------------------

    /// <summary>
    /// A 409 when <paramref name="whenUtc"/> falls in a closed period, or null. One wording for every write
    /// a closure stops, naming the period and the way out, so a register, a recognition and a correction
    /// all say the same thing.
    /// </summary>
    protected IActionResult? ClosedPeriodProblem(IStaffPerformancePolicyService policyService, StaffPerformancePolicyDto policy, DateTime whenUtc, string what)
    {
        var closure = policyService.ClosureFor(policy, whenUtc);
        if (closure == null) return null;
        var period = policyService.FindPeriod(policy, closure.Key);
        return ConflictProblem($"{period?.Name ?? closure.Key} is closed",
            $"{what} dated in a closed period would change figures that have been signed off. An approver can reopen the period first.");
    }

    // ---- Problem shapes ------------------------------------------------------------------------

    protected IActionResult BadRequestProblem(string title, string? detail = null)
        => BadRequest(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status400BadRequest });

    protected IActionResult NotFoundProblem(string title)
        => NotFound(new ProblemDetails { Title = title, Status = StatusCodes.Status404NotFound });

    protected IActionResult ConflictProblem(string title, string? detail = null)
        => Conflict(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status409Conflict });

    /// <summary>The wording an unknown id gets, reused for out-of-scope so the refusal does not confirm the person exists.</summary>
    protected IActionResult StaffMemberNotFound() => NotFoundProblem("Staff member not found");
    protected IActionResult RecordNotFound() => NotFoundProblem("Record not found");

    // ---- Summaries for the activity log --------------------------------------------------------

    /// <summary>
    /// What a record looks like in the activity log, written at the ACTOR's visibility. A Standard
    /// record names the parameter and the outcome; a Confidential one names the parameter only; a
    /// Restricted one says only that a restricted record exists. Never the description.
    /// </summary>
    protected static string RecordSummary(string verb, StaffPerformanceRecord record, string? parameterName, string subjectName)
    {
        return record.Visibility switch
        {
            WelfareVisibility.Restricted => $"Restricted record {verb} for {subjectName}",
            WelfareVisibility.Confidential => $"Confidential {parameterName ?? "record"} record {verb} for {subjectName}",
            _ => $"{parameterName ?? "Record"} {verb} for {subjectName}{OutcomeSuffix(record)}"
        };
    }

    private static string OutcomeSuffix(StaffPerformanceRecord r)
    {
        var parts = new List<string>();
        if (r.Outcome != DutyOutcome.NotApplicable) parts.Add(r.Outcome.ToString());
        if (r.Points is { } p && p != 0) parts.Add($"{(p > 0 ? "+" : "")}{p} pts");
        if (r.Rating is { } rating) parts.Add($"rated {rating}");
        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }

    protected static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
