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
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The summative appraisal (plan §7, Phase 5): Open → Self-assessment → Appraiser review →
/// Moderation → Signed, with Appealed reopening to Moderation. THE SCORE IS EVIDENCE, THE RATING
/// IS A DECISION: the live score is computed on every read while the appraisal is open and frozen
/// onto the row at Signed, so a later record correction cannot silently change a signed rating.
///
/// Appraisals are Confidential by nature. An appraisal is readable by its subject, its appraiser,
/// its moderator, any holder of staff.appraisals.approve, and a holder of staff.confidential.view
/// whose staff scope covers the subject; everyone else gets 404, never 403.
///
/// Who may do what at each stage is stated on the endpoint, and a stage transition from the wrong
/// stage is a 409 with the stage named, because "you cannot review this yet" is a workflow fact,
/// not a permission fact.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffAppraisalsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IStaffScopeService _scope;
    private readonly IStaffScoringService _scoring;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly INotificationService _notifications;
    private readonly IActivityLogger _activity;
    private readonly ILogger<StaffAppraisalsController> _logger;

    /// <summary>Days after the period end after which an appraiser may proceed without a self-assessment.</summary>
    private const int SelfAssessmentGraceDays = 7;

    public StaffAppraisalsController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService scope,
        IStaffScoringService scoring,
        IStaffPerformancePolicyService policy,
        INotificationService notifications,
        IActivityLogger activity,
        ILogger<StaffAppraisalsController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _scope = scope;
        _scoring = scoring;
        _policy = policy;
        _notifications = notifications;
        _activity = activity;
        _logger = logger;
    }

    // ---- Guards -------------------------------------------------------------------------------------

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
            ? await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync()
            : tenantContext.OrganizationId;
    }

    private Guid CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : Guid.Empty;
    }

    private readonly Dictionary<string, bool> _permissionCache = new();

    private async Task<bool> HasPermissionAsync(string code)
    {
        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole)) return true;
        if (_permissionCache.TryGetValue(code, out var cached)) return cached;

        // Role AND posts, through PostPermissionService.EffectiveCodesAsync — the one home for that union. It
        // read the role alone until 2026-09-22, which is why a department head could not set targets on an
        // appraisal they were the named appraiser of: staff.appraisals.conduct arrives with the post.
        var userId = CurrentUserId();
        if (userId == Guid.Empty) return _permissionCache[code] = false;
        _effectiveCodes ??= await PostPermissionService.EffectiveCodesAsync(_context, userId);
        return _permissionCache[code] = _effectiveCodes.Contains(code);
    }

    private HashSet<string>? _effectiveCodes;

    private Task<bool> CanApproveAsync() => HasPermissionAsync(Permissions.StaffAppraisalsApprove);
    private Task<bool> CanConductAsync() => HasPermissionAsync(Permissions.StaffAppraisalsConduct);

    private static IActionResult NotFoundAppraisal() => new NotFoundObjectResult(new ProblemDetails { Title = "Appraisal not found", Status = StatusCodes.Status404NotFound });

    private static IActionResult Problem400(string title, string? detail = null)
        => new BadRequestObjectResult(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status400BadRequest });

    private static IActionResult WrongStage(StaffAppraisal a, string wanted)
        => new ConflictObjectResult(new ProblemDetails
        {
            Title = $"This appraisal is at the {Stage(a.Stage)} stage",
            Detail = $"This step applies at the {wanted} stage.",
            Status = StatusCodes.Status409Conflict
        });

    private static string Stage(AppraisalStage s) => s switch
    {
        AppraisalStage.SelfAssessment => "Self-assessment",
        AppraisalStage.AppraiserReview => "Appraiser review",
        _ => s.ToString()
    };

    private IActionResult Forbidden(string detail)
        => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Title = "Not allowed", Detail = detail, Status = StatusCodes.Status403Forbidden });

    /// <summary>Loads an appraisal by id within the branch, or null.</summary>
    private Task<StaffAppraisal?> LoadAsync(Guid branchId, Guid organizationId, Guid appraisalId, bool track)
    {
        var q = track ? _context.StaffAppraisals : _context.StaffAppraisals.AsNoTracking();
        return q.Include(a => a.Subject).ThenInclude(s => s!.Role).Include(a => a.Appraiser)
            .FirstOrDefaultAsync(a => a.Id == appraisalId && a.BranchId == branchId && a.OrganizationId == organizationId && a.IsActive);
    }

    /// <summary>Subject, appraiser, moderator, an Approve holder, or a Confidential-rung holder whose scope covers the subject.</summary>
    private async Task<bool> MayReadAsync(StaffAppraisal a, Guid me)
    {
        if (a.SubjectUserId == me || a.AppraiserUserId == me || a.ModeratorUserId == me) return true;
        if (await CanApproveAsync()) return true;
        return await HasPermissionAsync(Permissions.StaffConfidentialView) && await _scope.CanSeeStaffAsync(a.BranchId, a.SubjectUserId);
    }

    private static PerformancePeriodDto PeriodOf(StaffAppraisal a, StaffPerformancePolicyDto policy, IStaffPerformancePolicyService svc)
        => svc.FindPeriod(policy, a.PeriodKey) ?? new PerformancePeriodDto { Key = a.PeriodKey, Name = a.PeriodKey, Start = a.PeriodStart, End = a.PeriodEnd };

    // ---- Board -------------------------------------------------------------------------------------

    /// <summary>
    /// The period's appraisals by stage. An Approve holder sees the branch; a Conduct holder sees
    /// the appraisals they appraise plus those whose subject their staff scope covers.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/staff/appraisals/board")]
    [ProducesResponseType(typeof(AppraisalBoardDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBoard(Guid branchId, [FromQuery] string? period = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var canApprove = await CanApproveAsync();
        if (!canApprove && !await CanConductAsync())
            return Forbidden("The appraisal board needs staff.appraisals.conduct or staff.appraisals.approve.");

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var p = _policy.FindPeriod(policy, period) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));
        var me = CurrentUserId();

        var items = await _context.StaffAppraisals.AsNoTracking()
            .Include(a => a.Subject).ThenInclude(s => s!.Role).Include(a => a.Appraiser)
            .Where(a => a.OrganizationId == organizationId && a.BranchId == branchId && a.PeriodKey == p.Key && a.IsActive)
            .OrderBy(a => a.Stage).ThenBy(a => a.Subject!.LastName)
            .ToListAsync();

        if (!canApprove)
        {
            var visible = await _scope.GetVisibleUserIdsAsync(branchId);
            if (visible != null)
                items = items.Where(a => a.AppraiserUserId == me || visible.Contains(a.SubjectUserId)).ToList();
        }

        var dtos = await MapManyAsync(items, organizationId, policy, me, canApprove, await CanConductAsync(), liveScores: false);
        var closure = _policy.ClosureOf(policy, p.Key);
        if (closure != null)
        {
            var closerNames = await StaffLookups.LoadNamesAsync(_context, new Guid?[] { closure.ClosedByUserId });
            closure = closure with { ClosedByName = closerNames[closure.ClosedByUserId] };
        }
        return Ok(new AppraisalBoardDto
        {
            Period = p,
            PeriodStatus = new PeriodStatusDto
            {
                Period = p,
                IsClosed = closure != null,
                Closure = closure,
                AppraisalCount = items.Count,
                UnsignedAppraisals = items.Count(a => a.Stage != AppraisalStage.Signed)
            },
            ByStage = Enum.GetValues<AppraisalStage>().ToDictionary(s => s.ToString(), s => items.Count(a => a.Stage == s)),
            Items = dtos,
            ScopedToDepartments = canApprove ? new List<string>() : (await _scope.GetScopedDepartmentNamesAsync()).ToList()
        });
    }

    /// <summary>
    /// Opens the period's appraisals: one per active staff member of the branch (or the given
    /// subjects) who does not already have one, appraiser = the override, else their line manager,
    /// else the head of their first department, else the caller.
    ///
    /// ANNUAL ROLL-UP: when <c>PeriodKey</c> is a bare year ("2026"), the appraisal's AppraiserRating
    /// is pre-filled with the rounded average of that year's SIGNED termly FinalRatings — the MoES
    /// reading that the termly reports "cumulatively constitute the annual performance appraisal
    /// report". The appraiser may still change it at review.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/staff/appraisals/open")]
    [RequirePermission(Permissions.StaffAppraisalsApprove)]
    [ProducesResponseType(typeof(List<StaffAppraisalDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Open(Guid branchId, [FromBody] OpenAppraisalsRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var p = _policy.FindPeriod(policy, request.PeriodKey) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));
        var me = CurrentUserId();
        var isAnnual = IsAnnualKey(p.Key);

        if (_policy.ClosureOf(policy, p.Key) != null)
            return new ConflictObjectResult(new ProblemDetails
            {
                Title = $"{p.Name} is closed",
                Detail = "No new appraisals open in a closed period. An approver can reopen the period first.",
                Status = StatusCodes.Status409Conflict
            });

        var staffQuery = StaffLookups.BranchStaff(_context, organizationId, branchId);
        if (request.SubjectUserIds is { Count: > 0 } wanted)
        {
            var ids = wanted.Distinct().ToList();
            staffQuery = staffQuery.Where(u => ids.Contains(u.Id));
        }
        var staff = await staffQuery.ToListAsync();
        if (request.SubjectUserIds is { Count: > 0 } && staff.Count != request.SubjectUserIds.Distinct().Count())
            return Problem400("One or more subjects are not active staff of this branch");

        if (request.AppraiserUserIdOverride is { } overrideId)
        {
            var ok = await _context.Users.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(u => u.Id == overrideId && u.OrganizationId == organizationId && u.IsActive);
            if (!ok) return Problem400("The appraiser override is not an active member of this organization");
        }

        var existing = (await _context.StaffAppraisals.AsNoTracking()
                .Where(a => a.OrganizationId == organizationId && a.BranchId == branchId && a.PeriodKey == p.Key && a.IsActive)
                .Select(a => a.SubjectUserId).ToListAsync())
            .ToHashSet();

        var departmentHeads = await _context.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.IsActive && d.HeadUserId != null)
            .ToDictionaryAsync(d => d.Id, d => d.HeadUserId!.Value);

        Dictionary<Guid, List<int>> termly = new();
        if (isAnnual)
        {
            var year = int.Parse(p.Key);
            var termKeys = _policy.PeriodsForYear(policy, year).Select(t => t.Key).Where(k => k != p.Key).ToList();
            var signed = await _context.StaffAppraisals.AsNoTracking()
                .Where(a => a.OrganizationId == organizationId && a.BranchId == branchId && a.IsActive
                            && a.Stage == AppraisalStage.Signed && a.FinalRating != null && termKeys.Contains(a.PeriodKey))
                .Select(a => new { a.SubjectUserId, Rating = a.FinalRating!.Value })
                .ToListAsync();
            termly = signed.GroupBy(s => s.SubjectUserId).ToDictionary(g => g.Key, g => g.Select(s => s.Rating).ToList());
        }

        var created = new List<StaffAppraisal>();
        foreach (var subject in staff.Where(s => !existing.Contains(s.Id)))
        {
            var appraiser = request.AppraiserUserIdOverride
                            ?? subject.LineManagerUserId
                            ?? subject.DepartmentIds?.Select(id => departmentHeads.TryGetValue(id, out var h) ? (Guid?)h : null).FirstOrDefault(h => h.HasValue && h.Value != subject.Id)
                            ?? me;
            // A person does not appraise themselves; fall back to the caller in that corner case.
            if (appraiser == subject.Id) appraiser = me;

            var a = new StaffAppraisal
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                SubjectUserId = subject.Id,
                PeriodKey = p.Key,
                PeriodStart = p.Start,
                PeriodEnd = p.End,
                AppraiserUserId = appraiser,
                Stage = AppraisalStage.Open,
                CreatedBy = me
            };
            if (isAnnual && termly.TryGetValue(subject.Id, out var ratings) && ratings.Count > 0)
                a.AppraiserRating = (int)Math.Round(ratings.Average(), MidpointRounding.AwayFromZero);

            _context.StaffAppraisals.Add(a);
            created.Add(a);
        }

        if (created.Count == 0)
            return Ok(new List<StaffAppraisalDto>());

        await _context.SaveChangesAsync();

        var names = await StaffLookups.LoadNamesAsync(_context, created.Select(a => (Guid?)a.SubjectUserId).Concat(created.Select(a => (Guid?)a.AppraiserUserId)));
        foreach (var a in created)
        {
            await _activity.RecordAsync(ActivityActions.AppraisalOpened, nameof(StaffAppraisal), a.Id, a.SubjectUserId,
                $"{p.Name} appraisal opened for {names[a.SubjectUserId]}, appraiser {names[a.AppraiserUserId]}",
                new { a.PeriodKey, a.AppraiserUserId, Annual = isAnnual, a.AppraiserRating }, branchId, organizationId, visibility: WelfareVisibility.Confidential);

            await NotifyAsync(a.SubjectUserId, organizationId, branchId, $"Your {p.Name} appraisal is open",
                $"Targets for the period can now be agreed with {names[a.AppraiserUserId]}. Complete your self-assessment when you are ready.", "/portal");
            if (a.AppraiserUserId != me)
                await NotifyAsync(a.AppraiserUserId, organizationId, branchId, $"You are appraising {names[a.SubjectUserId]} for {p.Name}",
                    "Agree targets with them at the start of the period; their self-assessment will come to you for review.", "/admin/staff/appraisals");
        }

        _logger.LogInformation("Opened {Count} {Period} appraisal(s) in branch {BranchId}", created.Count, p.Key, branchId);

        var canConduct = await CanConductAsync();
        return Ok(await MapManyAsync(created, organizationId, policy, me, true, canConduct, liveScores: false));
    }

    // ---- One appraisal --------------------------------------------------------------------------------

    [HttpGet("branches/{branchId:guid}/staff/appraisals/{appraisalId:guid}")]
    [ProducesResponseType(typeof(StaffAppraisalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid branchId, Guid appraisalId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var a = await LoadAsync(branchId, organizationId, appraisalId, track: false);
        var me = CurrentUserId();
        if (a == null || !await MayReadAsync(a, me)) return NotFoundAppraisal();

        var policy = await _policy.GetAsync(organizationId);
        return Ok(await MapOneAsync(a, organizationId, policy, me, includeRank: a.SubjectUserId == me || await CanApproveAsync()));
    }

    /// <summary>The performance plan for the period. Appraiser (with conduct) or Approve; at Open or Self-assessment.</summary>
    [HttpPut("branches/{branchId:guid}/staff/appraisals/{appraisalId:guid}/targets")]
    [ProducesResponseType(typeof(StaffAppraisalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetTargets(Guid branchId, Guid appraisalId, [FromBody] SetAppraisalTargetsRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var a = await LoadAsync(branchId, organizationId, appraisalId, track: true);
        var me = CurrentUserId();
        if (a == null || !await MayReadAsync(a, me)) return NotFoundAppraisal();
        if (!await IsAppraiserOrApproverAsync(a, me)) return Forbidden("Only the appraiser or an approver sets targets.");
        if (a.Stage is not (AppraisalStage.Open or AppraisalStage.SelfAssessment)) return WrongStage(a, "Open or Self-assessment");

        var targets = (request.Targets ?? new()).Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
        foreach (var t in targets) t.Text = t.Text.Trim();
        a.TargetsJson = StaffPerformanceMapping.SerializeList(targets);
        if (a.Stage == AppraisalStage.Open && targets.Count > 0) a.Stage = AppraisalStage.SelfAssessment;
        Touch(a, me);
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.AppraisalTargetsSet, nameof(StaffAppraisal), a.Id, a.SubjectUserId,
            $"{targets.Count} target(s) set on {SubjectName(a)}'s {a.PeriodKey} appraisal", new { Count = targets.Count }, branchId, organizationId, visibility: WelfareVisibility.Confidential);

        if (a.SubjectUserId != me)
            await NotifyAsync(a.SubjectUserId, organizationId, branchId, $"Targets set for your {a.PeriodKey} appraisal",
                $"{targets.Count} target(s) have been agreed. Your self-assessment is open.", "/portal");

        var policy = await _policy.GetAsync(organizationId);
        return Ok(await MapOneAsync(a, organizationId, policy, me, includeRank: false));
    }

    /// <summary>The subject's own self-assessment. Subject only; from Open or Self-assessment; moves to Appraiser review.</summary>
    [HttpPost("branches/{branchId:guid}/staff/appraisals/{appraisalId:guid}/self")]
    [ProducesResponseType(typeof(StaffAppraisalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitSelf(Guid branchId, Guid appraisalId, [FromBody] SubmitSelfAssessmentRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var a = await LoadAsync(branchId, organizationId, appraisalId, track: true);
        var me = CurrentUserId();
        // Subject only. Anyone else — appraiser included — gets the same 404 an unknown id gets.
        if (a == null || a.SubjectUserId != me) return NotFoundAppraisal();
        if (a.Stage is not (AppraisalStage.Open or AppraisalStage.SelfAssessment)) return WrongStage(a, "Open or Self-assessment");
        if (request.SelfRating is < 1 or > 5) return Problem400("Rate yourself from 1 to 5");
        // Each parameter on the same 1–5 scale; 0 means "not rated" and is dropped. Before 2026-09-17 only
        // the overall rating was bounded, so a per-parameter 9 was stored and shown to the appraiser.
        if (request.Ratings?.Any(r => r.Rating is < 0 or > 5) == true)
            return Problem400("Rate each parameter from 1 to 5", "Leave a parameter unrated rather than rating it outside the scale.");

        a.SelfRatingsJson = StaffPerformanceMapping.SerializeList(request.Ratings?.Where(r => r.Rating is >= 1 and <= 5).GroupBy(r => r.ParameterId).Select(g => g.Last()).ToList());
        a.SelfRating = request.SelfRating;
        a.SelfComments = string.IsNullOrWhiteSpace(request.Comments) ? null : request.Comments.Trim();
        a.SelfSubmittedAt = DateTime.UtcNow;
        a.Stage = AppraisalStage.AppraiserReview;
        a.ReminderSentAt = null;
        Touch(a, me);
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.AppraisalSelfSubmitted, nameof(StaffAppraisal), a.Id, a.SubjectUserId,
            $"{SubjectName(a)} submitted their {a.PeriodKey} self-assessment", new { a.SelfRating }, branchId, organizationId, visibility: WelfareVisibility.Confidential);

        await NotifyAsync(a.AppraiserUserId, organizationId, branchId, $"{SubjectName(a)}'s self-assessment is ready",
            $"Their {a.PeriodKey} appraisal is now with you for review.", "/admin/staff/appraisals");

        var policy = await _policy.GetAsync(organizationId);
        return Ok(await MapOneAsync(a, organizationId, policy, me, includeRank: true));
    }

    /// <summary>
    /// The appraiser's review: rating, comments, strengths, development areas, support plan, next
    /// targets. From Appraiser review; or from Open / Self-assessment once the period has ended plus
    /// seven days with no self-assessment, so one silent subject cannot stall a school's cycle.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/staff/appraisals/{appraisalId:guid}/review")]
    [ProducesResponseType(typeof(StaffAppraisalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitReview(Guid branchId, Guid appraisalId, [FromBody] SubmitAppraiserReviewRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var a = await LoadAsync(branchId, organizationId, appraisalId, track: true);
        var me = CurrentUserId();
        if (a == null || !await MayReadAsync(a, me)) return NotFoundAppraisal();
        if (!await IsAppraiserOrApproverAsync(a, me)) return Forbidden("Only the appraiser (with staff.appraisals.conduct) or an approver reviews.");

        if (a.Stage is AppraisalStage.Open or AppraisalStage.SelfAssessment)
        {
            var graceEnd = a.PeriodEnd.AddDays(SelfAssessmentGraceDays).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            if (DateTime.UtcNow < graceEnd)
                return new ConflictObjectResult(new ProblemDetails
                {
                    Title = "Waiting for the self-assessment",
                    Detail = string.Create(CultureInfo.InvariantCulture, $"{SubjectName(a)} has not submitted a self-assessment. You may proceed without one from {graceEnd:dd MMM yyyy}."),
                    Status = StatusCodes.Status409Conflict
                });
        }
        else if (a.Stage != AppraisalStage.AppraiserReview) return WrongStage(a, "Appraiser review");

        if (request.Rating is < 1 or > 5) return Problem400("Rate from 1 to 5");

        a.AppraiserRating = request.Rating;
        a.AppraiserComments = Clean(request.Comments);
        a.Strengths = Clean(request.Strengths);
        a.DevelopmentAreas = Clean(request.DevelopmentAreas);
        a.SupportPlanJson = StaffPerformanceMapping.SerializeList(request.SupportPlan?.Where(s => !string.IsNullOrWhiteSpace(s.Gap)).ToList());
        a.NextTargetsJson = StaffPerformanceMapping.SerializeList(request.NextTargets?.Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList());
        if (request.Targets is { Count: > 0 }) a.TargetsJson = StaffPerformanceMapping.SerializeList(request.Targets.Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList());
        a.AppraiserSubmittedAt = DateTime.UtcNow;
        a.Stage = AppraisalStage.Moderation;
        a.ReminderSentAt = null;
        Touch(a, me);
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.AppraisalReviewed, nameof(StaffAppraisal), a.Id, a.SubjectUserId,
            $"{SubjectName(a)}'s {a.PeriodKey} appraisal reviewed by the appraiser", new { a.AppraiserRating }, branchId, organizationId, visibility: WelfareVisibility.Confidential);

        foreach (var moderator in await StaffLookups.UsersWithPermissionAsync(_context, organizationId, Permissions.StaffAppraisalsApprove))
            if (moderator != me)
                await NotifyAsync(moderator, organizationId, branchId, $"{SubjectName(a)}'s appraisal awaits moderation",
                    $"{a.PeriodKey}: the appraiser rated {a.AppraiserRating}/5. Moderate and sign.", "/admin/staff/appraisals");
        await NotifyAsync(a.SubjectUserId, organizationId, branchId, $"Your {a.PeriodKey} appraisal has been reviewed",
            "Your appraiser has completed their review. It now goes to moderation before it is signed.", "/portal");

        var policy = await _policy.GetAsync(organizationId);
        return Ok(await MapOneAsync(a, organizationId, policy, me, includeRank: false));
    }

    /// <summary>Moderation: sets the FinalRating (a reason is required when it differs from the appraiser's) and, by default, signs. From Moderation or Appealed.</summary>
    [HttpPost("branches/{branchId:guid}/staff/appraisals/{appraisalId:guid}/moderate")]
    [RequirePermission(Permissions.StaffAppraisalsApprove)]
    [ProducesResponseType(typeof(StaffAppraisalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Moderate(Guid branchId, Guid appraisalId, [FromBody] ModerateAppraisalRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var a = await LoadAsync(branchId, organizationId, appraisalId, track: true);
        var me = CurrentUserId();
        if (a == null) return NotFoundAppraisal();
        if (a.Stage is not (AppraisalStage.Moderation or AppraisalStage.Appealed)) return WrongStage(a, "Moderation or Appealed");
        if (request.FinalRating is < 1 or > 5) return Problem400("The final rating is 1 to 5");
        if (a.AppraiserRating.HasValue && request.FinalRating != a.AppraiserRating.Value && string.IsNullOrWhiteSpace(request.Reason))
            return Problem400("A reason is required to change the appraiser's rating", $"The appraiser rated {a.AppraiserRating}; you are recording {request.FinalRating}. Say why, so the record carries it.");
        if (request.SignNow)
        {
            var termlyProblem = await AnnualSignProblemAsync(a, organizationId);
            if (termlyProblem != null) return termlyProblem;
        }

        var before = a.FinalRating;
        a.FinalRating = request.FinalRating;
        a.ModerationReason = Clean(request.Reason);
        a.ModeratedAt = DateTime.UtcNow;
        a.ModeratorUserId = me;
        a.ReminderSentAt = null;
        Touch(a, me);

        var policy = await _policy.GetAsync(organizationId);
        var bandName = policy.Bands.FirstOrDefault(b => b.Rating == a.FinalRating)?.Name;

        if (request.SignNow)
            await FreezeAndSignAsync(a, organizationId, branchId, policy, me);
        else
            a.Stage = AppraisalStage.Moderation;

        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.AppraisalModerated, nameof(StaffAppraisal), a.Id, a.SubjectUserId,
            $"{SubjectName(a)}'s {a.PeriodKey} appraisal moderated{(a.AppraiserRating != a.FinalRating ? ", rating changed with a recorded reason" : "")}",
            new { Before = before, a.AppraiserRating, a.FinalRating, Reason = a.ModerationReason, Signed = request.SignNow }, branchId, organizationId, visibility: WelfareVisibility.Confidential);

        if (request.SignNow)
            await AfterSignedAsync(a, organizationId, branchId, bandName);
        else
            await NotifyAsync(a.SubjectUserId, organizationId, branchId, $"Your {a.PeriodKey} appraisal has been moderated",
                $"A moderator has recorded a rating of {a.FinalRating}/5 ({bandName}). It will be signed shortly.", "/portal");

        return Ok(await MapOneAsync(a, organizationId, policy, me, includeRank: false));
    }

    /// <summary>Signs: FinalRating defaults to the appraiser's, the score and breakdown are FROZEN from the scorer, and the stage becomes Signed. From Moderation.</summary>
    [HttpPost("branches/{branchId:guid}/staff/appraisals/{appraisalId:guid}/sign")]
    [RequirePermission(Permissions.StaffAppraisalsApprove)]
    [ProducesResponseType(typeof(StaffAppraisalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Sign(Guid branchId, Guid appraisalId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var a = await LoadAsync(branchId, organizationId, appraisalId, track: true);
        var me = CurrentUserId();
        if (a == null) return NotFoundAppraisal();
        if (a.Stage != AppraisalStage.Moderation) return WrongStage(a, "Moderation");
        if (a.AppraiserRating == null && a.FinalRating == null)
            return Problem400("Nothing to sign yet", "The appraiser has not recorded a rating.");
        var termlyProblem = await AnnualSignProblemAsync(a, organizationId);
        if (termlyProblem != null) return termlyProblem;

        var policy = await _policy.GetAsync(organizationId);
        await FreezeAndSignAsync(a, organizationId, branchId, policy, me);
        Touch(a, me);
        await _context.SaveChangesAsync();

        var bandName = policy.Bands.FirstOrDefault(b => b.Rating == a.FinalRating)?.Name;
        await AfterSignedAsync(a, organizationId, branchId, bandName);

        return Ok(await MapOneAsync(a, organizationId, policy, me, includeRank: false));
    }

    /// <summary>The subject appeals a signed appraisal with a note; it reopens to Moderation for the approvers.</summary>
    [HttpPost("branches/{branchId:guid}/staff/appraisals/{appraisalId:guid}/appeal")]
    [ProducesResponseType(typeof(StaffAppraisalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Appeal(Guid branchId, Guid appraisalId, [FromBody] AppealAppraisalRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var a = await LoadAsync(branchId, organizationId, appraisalId, track: true);
        var me = CurrentUserId();
        if (a == null || a.SubjectUserId != me) return NotFoundAppraisal();
        if (a.Stage != AppraisalStage.Signed) return WrongStage(a, "Signed");
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Trim().Length < 10) return Problem400("Say why you are appealing, in at least ten characters");

        a.AppealNote = request.Note.Trim();
        a.Stage = AppraisalStage.Appealed;
        a.ReminderSentAt = null;
        Touch(a, me);
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.AppraisalAppealed, nameof(StaffAppraisal), a.Id, a.SubjectUserId,
            $"{SubjectName(a)} appealed their {a.PeriodKey} appraisal", null, branchId, organizationId, visibility: WelfareVisibility.Confidential);

        foreach (var moderator in await StaffLookups.UsersWithPermissionAsync(_context, organizationId, Permissions.StaffAppraisalsApprove))
            await NotifyAsync(moderator, organizationId, branchId, $"{SubjectName(a)} has appealed their {a.PeriodKey} appraisal",
                StaffNoticeFanOut.Summary(a.AppealNote, 160), "/admin/staff/appraisals");

        var policy = await _policy.GetAsync(organizationId);
        return Ok(await MapOneAsync(a, organizationId, policy, me, includeRank: true));
    }

    /// <summary>Attaches the signed report published to the Library (a MediaContent of the organization). Appraiser or approver.</summary>
    [HttpPut("branches/{branchId:guid}/staff/appraisals/{appraisalId:guid}/report")]
    [ProducesResponseType(typeof(StaffAppraisalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AttachReport(Guid branchId, Guid appraisalId, [FromBody] AttachAppraisalReportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var a = await LoadAsync(branchId, organizationId, appraisalId, track: true);
        var me = CurrentUserId();
        if (a == null || !await MayReadAsync(a, me)) return NotFoundAppraisal();
        if (!await IsAppraiserOrApproverAsync(a, me)) return Forbidden("Only the appraiser or an approver attaches the report.");

        var mediaName = await _context.MediaContents.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == request.MediaContentId && m.OrganizationId == organizationId && m.IsActive)
            .Select(m => m.Name).FirstOrDefaultAsync();
        if (mediaName == null) return Problem400("Library document not found", "Publish the appraisal report to the Library first, then attach it here.");

        a.ReportMediaContentId = request.MediaContentId;
        Touch(a, me);
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.AppraisalReportPublished, nameof(StaffAppraisal), a.Id, a.SubjectUserId,
            $"Report \"{mediaName}\" attached to {SubjectName(a)}'s {a.PeriodKey} appraisal", new { request.MediaContentId }, branchId, organizationId, visibility: WelfareVisibility.Confidential);

        var policy = await _policy.GetAsync(organizationId);
        return Ok(await MapOneAsync(a, organizationId, policy, me, includeRank: false));
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private async Task<bool> IsAppraiserOrApproverAsync(StaffAppraisal a, Guid me)
        => (a.AppraiserUserId == me && await CanConductAsync()) || await CanApproveAsync();

    /// <summary>FinalRating ??= AppraiserRating; freeze the score and breakdown; Signed. Does not save.</summary>
    private async Task FreezeAndSignAsync(StaffAppraisal a, Guid organizationId, Guid branchId, StaffPerformancePolicyDto policy, Guid me)
    {
        a.FinalRating ??= a.AppraiserRating;
        var period = PeriodOf(a, policy, _policy);
        var score = await _scoring.ComputeAsync(organizationId, branchId, a.SubjectUserId, period, includeRank: false);
        a.ComputedScore = score.Composite;
        a.ComputedBreakdownJson = StaffPerformanceMapping.SerializeList(score.Breakdown);
        a.SignedAt = DateTime.UtcNow;
        a.SignedByUserId = me;
        a.Stage = AppraisalStage.Signed;
        a.ReminderSentAt = null;
    }

    private async Task AfterSignedAsync(StaffAppraisal a, Guid organizationId, Guid branchId, string? bandName)
    {
        await _activity.RecordAsync(ActivityActions.AppraisalSigned, nameof(StaffAppraisal), a.Id, a.SubjectUserId,
            $"{SubjectName(a)}'s {a.PeriodKey} appraisal signed; score frozen",
            new { a.FinalRating, a.ComputedScore }, branchId, organizationId, visibility: WelfareVisibility.Confidential);

        await NotifyAsync(a.SubjectUserId, organizationId, branchId, $"Your {a.PeriodKey} appraisal has been signed",
            $"Final rating {a.FinalRating}/5 ({bandName}). Open your portal to read it; you may appeal with a note if you disagree.", "/portal");
        if (a.AppraiserUserId != a.SignedByUserId)
            await NotifyAsync(a.AppraiserUserId, organizationId, branchId, $"{SubjectName(a)}'s {a.PeriodKey} appraisal has been signed",
                $"Final rating {a.FinalRating}/5 ({bandName}).", "/admin/staff/appraisals");
    }

    private async Task NotifyAsync(Guid userId, Guid organizationId, Guid branchId, string title, string message, string url)
    {
        try
        {
            await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = userId,
                OrganizationId = organizationId,
                BranchId = branchId,
                Title = title,
                Message = message,
                Type = NotificationType.StaffPerformance,
                Priority = NotificationPriority.Normal,
                Channels = NotificationChannel.InApp | NotificationChannel.Email,
                EventKey = NotificationEventKeys.StaffAppraisalStage,
                ActionUrl = url,
                IconClass = "clipboard-check"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Appraisal notification to {UserId} failed: {Title}", userId, title);
        }
    }

    private static void Touch(StaffAppraisal a, Guid me)
    {
        a.UpdatedAt = DateTime.UtcNow;
        a.UpdatedBy = me;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string SubjectName(StaffAppraisal a) => a.Subject != null ? StaffPerformanceMapping.FullName(a.Subject) : "the subject";

    private static bool IsAnnualKey(string key) => key.Length == 4 && int.TryParse(key, out _);

    /// <summary>
    /// The annual appraisal is what the termly ones "cumulatively constitute" (MoES), so it is not signed
    /// while one of that year's termly appraisals for the same person is still unsigned — signing the
    /// annual first would freeze an average of ratings that can still change. A person with no termly
    /// appraisals at all (joined late, or a school that appraises annually) is not held up.
    /// </summary>
    private async Task<IActionResult?> AnnualSignProblemAsync(StaffAppraisal a, Guid organizationId)
    {
        if (!IsAnnualKey(a.PeriodKey)) return null;
        var policy = await _policy.GetAsync(organizationId);
        var termKeys = _policy.PeriodsForYear(policy, int.Parse(a.PeriodKey)).Select(t => t.Key).Where(k => k != a.PeriodKey).ToList();
        var open = await _context.StaffAppraisals.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.SubjectUserId == a.SubjectUserId && t.IsActive
                        && termKeys.Contains(t.PeriodKey) && t.Stage != AppraisalStage.Signed)
            .Select(t => new { t.PeriodKey, t.Stage })
            .ToListAsync();
        if (open.Count == 0) return null;
        return new ConflictObjectResult(new ProblemDetails
        {
            Title = "The termly appraisals are not all signed",
            Detail = $"{string.Join(", ", open.Select(o => $"{o.PeriodKey} is at {Stage(o.Stage)}"))}. Sign those first; the annual rating is their average.",
            Status = StatusCodes.Status409Conflict
        });
    }

    private async Task<StaffAppraisalDto> MapOneAsync(StaffAppraisal a, Guid organizationId, StaffPerformancePolicyDto policy, Guid me, bool includeRank)
    {
        var period = PeriodOf(a, policy, _policy);
        var live = await _scoring.ComputeAsync(organizationId, a.BranchId, a.SubjectUserId, period, includeRank);
        var list = await MapManyAsync(new List<StaffAppraisal> { a }, organizationId, policy, me, await CanApproveAsync(), await CanConductAsync(), liveScores: false, live);
        var dto = list[0] with { IsAnnual = IsAnnualKey(a.PeriodKey), PeriodClosed = _policy.ClosureOf(policy, a.PeriodKey) != null };

        // The moderator's view of the appraiser (plan §7): this appraiser's ratings this period against
        // every appraiser's in the branch. Only for someone who may moderate — it names a colleague's habits.
        if (await CanApproveAsync())
        {
            var ratings = await _context.StaffAppraisals.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.BranchId == a.BranchId && x.PeriodKey == a.PeriodKey && x.IsActive && x.AppraiserRating != null)
                .Select(x => new { x.AppraiserUserId, Rating = x.AppraiserRating!.Value })
                .ToListAsync();
            var mine = ratings.Where(r => r.AppraiserUserId == a.AppraiserUserId).Select(r => r.Rating).ToList();
            var all = ratings.Select(r => r.Rating).ToList();
            dto = dto with
            {
                AppraiserRatingCounts = Enumerable.Range(1, 5).Select(n => mine.Count(r => r == n)).ToList(),
                SchoolRatingCounts = Enumerable.Range(1, 5).Select(n => all.Count(r => r == n)).ToList(),
                AppraiserMeanRating = mine.Count > 0 ? Math.Round((decimal)mine.Average(), 2) : null,
                SchoolMeanRating = all.Count > 0 ? Math.Round((decimal)all.Average(), 2) : null
            };
        }

        // The annual roll-up, live: the year's termly appraisals for this person and their signed average.
        if (dto.IsAnnual)
        {
            var terms = _policy.PeriodsForYear(policy, int.Parse(a.PeriodKey)).Where(t => t.Key != a.PeriodKey).ToList();
            var termKeys = terms.Select(t => t.Key).ToList();
            var termly = await _context.StaffAppraisals.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.SubjectUserId == a.SubjectUserId && x.IsActive && termKeys.Contains(x.PeriodKey))
                .Select(x => new { x.Id, x.PeriodKey, x.Stage, x.FinalRating })
                .ToListAsync();
            var rows = termly
                .OrderBy(x => termKeys.IndexOf(x.PeriodKey))
                .Select(x => new TermRatingDto
                {
                    AppraisalId = x.Id,
                    PeriodKey = x.PeriodKey,
                    PeriodName = terms.First(t => t.Key == x.PeriodKey).Name,
                    Stage = x.Stage,
                    FinalRating = x.Stage == AppraisalStage.Signed ? x.FinalRating : null
                })
                .ToList();
            var signed = rows.Where(r => r.FinalRating.HasValue).Select(r => r.FinalRating!.Value).ToList();
            dto = dto with { TermlyRollup = rows, TermlyAverage = signed.Count > 0 ? Math.Round((decimal)signed.Average(), 2) : null };
        }

        return dto;
    }

    private async Task<List<StaffAppraisalDto>> MapManyAsync(List<StaffAppraisal> items, Guid organizationId, StaffPerformancePolicyDto policy, Guid me, bool canApprove, bool canConduct, bool liveScores, StaffScoreDto? singleLive = null)
    {
        if (items.Count == 0) return new List<StaffAppraisalDto>();

        var names = await StaffLookups.LoadNamesAsync(_context,
            items.Select(a => (Guid?)a.SubjectUserId).Concat(items.Select(a => (Guid?)a.AppraiserUserId))
                .Concat(items.Select(a => a.ModeratorUserId)).Concat(items.Select(a => a.SignedByUserId)));
        var departmentNames = await StaffLookups.LoadDepartmentNamesAsync(_context, organizationId);

        var result = new List<StaffAppraisalDto>(items.Count);
        foreach (var a in items)
        {
            var bandName = a.FinalRating.HasValue ? policy.Bands.FirstOrDefault(b => b.Rating == a.FinalRating.Value)?.Name : null;
            StaffScoreDto? live = singleLive;
            if (live == null && liveScores)
                live = await _scoring.ComputeAsync(organizationId, a.BranchId, a.SubjectUserId, PeriodOf(a, policy, _policy), includeRank: false);

            result.Add(StaffPerformanceMapping.ToDto(a, names, StaffLookups.DepartmentNames(a.Subject?.DepartmentIds, departmentNames), bandName, live, me, canApprove, canConduct));
        }
        return result;
    }
}
