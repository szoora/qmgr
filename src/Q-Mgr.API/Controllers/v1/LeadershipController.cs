using System.Text.Json;
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
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The leadership posts and the access review (the RBAC review's R2, R4, R5 and R6, 2026-09-24).
///
/// <para><b>YOU MAY DELEGATE ONLY WHAT YOUR ROLE HOLDS.</b> Appointing somebody to a post grants them the post's
/// permissions, so the appointer's ROLE — never their posts — must already hold every one of them. That is the
/// RoleAssignmentGuard rule applied to posts: a post is temporary, and a deputy safeguarding lead must not be able to
/// appoint another. It makes the safeguarding lead and acting head the Administrator's or the Head Teacher's act.</para>
///
/// <para><b>Every write names the people on both sides to IStaffProfileChangeNotifier.PostChangedAsync</b>, because
/// the permission cache holds a set for five minutes and the person REMOVED is the one who would keep access.</para>
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public class LeadershipController : ControllerBase
{
    private readonly QMgrDbContext _db;
    private readonly ITenantContextAccessor _tenant;
    private readonly IStaffProfileChangeNotifier _accessChanged;
    private readonly INotificationService _notifications;
    private readonly IActivityLogger _activity;

    public LeadershipController(QMgrDbContext db, ITenantContextAccessor tenant, IStaffProfileChangeNotifier accessChanged,
        INotificationService notifications, IActivityLogger activity)
    {
        _db = db;
        _tenant = tenant;
        _accessChanged = accessChanged;
        _notifications = notifications;
        _activity = activity;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private Guid Me()
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private bool IsSuperAdmin => RoleCodes.IsSuperAdmin(_tenant.TenantContext?.UserRole);

    private Guid OrganizationId => _tenant.TenantContext is { IsResolved: true } t ? t.OrganizationId : Guid.Empty;

    private IActionResult NoOrganization() => BadRequest(new ProblemDetails
    {
        Title = "No organisation chosen",
        Detail = "Choose an organisation first.",
        Status = StatusCodes.Status400BadRequest
    });

    private static ObjectResult Problem400(string title, string detail) => new BadRequestObjectResult(new ProblemDetails
    {
        Title = title, Detail = detail, Status = StatusCodes.Status400BadRequest
    });

    /// <summary>The caller's ROLE permissions — deliberately not the effective set (see the class comment).</summary>
    private async Task<HashSet<string>> MyRolePermissionsAsync() =>
        await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == Me() && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions).Select(rp => rp.Permission.Code)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> MayDelegateAsync(IEnumerable<string> grant)
    {
        if (IsSuperAdmin) return true;
        var mine = await MyRolePermissionsAsync();
        return grant.All(mine.Contains);
    }

    private sealed record Person(Guid Id, string FullName, string SortName, string? RoleCode, string? RoleName);

    private async Task<List<Person>> PeopleAsync(Guid organizationId) =>
        (await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive && u.PendingApprovalAt == null && u.Role.Code != RoleCodes.SuperAdmin)
            .Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName, u.Username, RoleCode = u.Role.Code, RoleName = u.Role.Name })
            .ToListAsync())
        .Select(u => new Person(u.Id, PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName, u.Username),
            PersonNames.SortKey(u.OrganizationId, u.FirstName, u.LastName), u.RoleCode, u.RoleName))
        .OrderBy(p => p.SortName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static LeadershipPersonDto Dto(Person p) => new(p.Id, p.FullName, p.RoleName);

    private async Task TellAsync(Guid organizationId, IEnumerable<Guid> people, string title, string message)
    {
        foreach (var userId in people.Distinct())
        {
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = organizationId,
                    Title = title,
                    Message = message,
                    Type = NotificationType.SystemAlert,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.LeadershipPostChanged,
                    ActionUrl = "/portal",
                    IconClass = "shield-check"
                });
            }
            catch
            {
                // A notice that could not be sent must not undo an appointment that has been saved.
            }
        }
    }

    private async Task InvalidateAsync(IEnumerable<Guid> people, string via)
    {
        foreach (var id in people.Distinct()) await _accessChanged.PostChangedAsync(id, via);
    }

    // ---- R2 / R6: who holds the leadership posts ----------------------------------------------------

    /// <summary>Who holds each post. Readable by everybody in the school — every member of staff must know who the
    /// safeguarding lead is (KCSIE), and an acting head nobody has heard of cannot act.</summary>
    [HttpGet("leadership")]
    [ProducesResponseType(typeof(LeadershipViewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLeadership()
    {
        var org = OrganizationId;
        if (org == Guid.Empty) return NoOrganization();

        var posts = await LeadershipPosts.ReadAsync(_db, org);
        var people = await PeopleAsync(org);
        var byId = people.ToDictionary(p => p.Id);
        LeadershipPersonDto? Named(Guid? id) => id is { } i && byId.TryGetValue(i, out var p) ? Dto(p) : null;

        var canSafeguarding = await MayDelegateAsync(LeadershipPosts.SafeguardingLeadPost);
        var canActing = await MayDelegateAsync(LeadershipPosts.ActingHeadPost());
        var acting = posts.ActingHead;

        return Ok(new LeadershipViewDto
        {
            SafeguardingLead = Named(posts.SafeguardingLeadUserId),
            DeputySafeguardingLeads = posts.DeputySafeguardingLeadUserIds.Select(d => Named(d)).OfType<LeadershipPersonDto>().ToList(),
            ActingHead = Named(acting?.UserId),
            ActingHeadStartsOn = acting?.StartsOn,
            ActingHeadEndsOn = acting?.EndsOn,
            ActingHeadReason = canActing ? acting?.Reason : null,
            ActingHeadActive = acting?.IsActiveOn(LeadershipPosts.Today()) == true,
            CanAppointSafeguarding = canSafeguarding,
            CanAppointActingHead = canActing,
            SafeguardingCandidates = canSafeguarding
                ? people.Where(p => RoleCodes.IsIn(RoleCodes.SeniorLeadership, p.RoleCode)).Select(Dto).ToList() : new(),
            ActingHeadCandidates = canActing
                ? people.Where(p => RoleCodes.IsIn(RoleCodes.ActingHeadEligible, p.RoleCode)).Select(Dto).ToList() : new(),
        });
    }

    /// <summary>
    /// Appoint the designated safeguarding lead and up to three deputies. The holder must be on the senior leadership
    /// team (Administrator, Head Teacher, Deputy Head Teacher or Director of Studies): KCSIE requires the lead to have
    /// the status and authority to direct other staff. The appointer's role must hold every permission the post grants.
    /// </summary>
    [HttpPut("leadership/safeguarding")]
    public async Task<IActionResult> UpdateSafeguarding([FromBody] UpdateSafeguardingLeadsRequest request)
    {
        var org = OrganizationId;
        if (org == Guid.Empty) return NoOrganization();
        if (!await MayDelegateAsync(LeadershipPosts.SafeguardingLeadPost))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You cannot appoint the safeguarding lead",
                Detail = "The post grants the restricted rung of welfare records, and your own role does not hold it. The Administrator or the Head Teacher appoints the safeguarding lead.",
                Status = StatusCodes.Status403Forbidden
            });

        var deputies = request.DeputyUserIds.Where(d => d != Guid.Empty).Distinct().ToList();
        if (request.LeadUserId is { } lead) deputies.Remove(lead);
        if (deputies.Count > LeadershipPosts.MaxDeputySafeguardingLeads)
            return Problem400("Too many deputies", $"A school names at most {LeadershipPosts.MaxDeputySafeguardingLeads} deputy safeguarding leads.");
        if (request.LeadUserId == null && deputies.Count > 0)
            return Problem400("No lead named", "Name the safeguarding lead before naming deputies.");

        var people = (await PeopleAsync(org)).ToDictionary(p => p.Id);
        foreach (var id in deputies.Prepend(request.LeadUserId ?? Guid.Empty).Where(i => i != Guid.Empty))
        {
            if (!people.TryGetValue(id, out var p))
                return Problem400("Unknown person", "One of the people named is not an active member of this school.");
            if (!RoleCodes.IsIn(RoleCodes.SeniorLeadership, p.RoleCode))
                return Problem400("Not on the senior leadership team",
                    $"{p.FullName} is {p.RoleName}. The safeguarding lead and deputies must be the Administrator, the Head Teacher, a Deputy Head Teacher or the Director of Studies.");
        }

        LeadershipPostsDto before = new();
        var after = await LeadershipPosts.WriteAsync(_db, org, current =>
        {
            before = current;
            return current with { SafeguardingLeadUserId = request.LeadUserId, DeputySafeguardingLeadUserIds = deputies };
        });

        var oldSet = before.DeputySafeguardingLeadUserIds.Append(before.SafeguardingLeadUserId ?? Guid.Empty).Where(i => i != Guid.Empty).ToHashSet();
        var newSet = after.DeputySafeguardingLeadUserIds.Append(after.SafeguardingLeadUserId ?? Guid.Empty).Where(i => i != Guid.Empty).ToHashSet();
        var changed = oldSet.Union(newSet).ToList();
        await InvalidateAsync(changed, "safeguarding leads");

        var added = newSet.Except(oldSet).ToList();
        var removed = oldSet.Except(newSet).ToList();
        if (added.Count > 0)
            await TellAsync(org, added, "You are now a safeguarding lead",
                "You can read every child's welfare file, including restricted records. Staff will bring concerns to you.");
        if (removed.Count > 0)
            await TellAsync(org, removed, "You are no longer a safeguarding lead",
                "The access that came with the post has ended.");

        string Name(Guid? id) => id is { } i && people.TryGetValue(i, out var p) ? p.FullName : "nobody";
        await _activity.RecordAsync(ActivityActions.SafeguardingLeadsChanged, "Organization", org, null,
            $"Safeguarding lead: {Name(after.SafeguardingLeadUserId)}; deputies: {(deputies.Count == 0 ? "none" : string.Join(", ", deputies.Select(d => Name(d))))}",
            new { before.SafeguardingLeadUserId, before.DeputySafeguardingLeadUserIds, after = new { after.SafeguardingLeadUserId, after.DeputySafeguardingLeadUserIds } },
            organizationId: org, visibility: WelfareVisibility.Confidential);

        return await GetLeadership();
    }

    /// <summary>
    /// Name somebody acting head for a fixed period, or end it (UserId null). Only a Deputy Head Teacher or the Director
    /// of Studies may act; the period must end within 120 days, and the grant lapses on its own the day after.
    /// </summary>
    [HttpPut("leadership/acting-head")]
    public async Task<IActionResult> UpdateActingHead([FromBody] UpdateActingHeadRequest request)
    {
        var org = OrganizationId;
        if (org == Guid.Empty) return NoOrganization();
        if (!await MayDelegateAsync(LeadershipPosts.ActingHeadPost()))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You cannot appoint an acting head",
                Detail = "An acting head holds everything a Head Teacher holds, and your own role does not. The Administrator or the Head Teacher appoints one.",
                Status = StatusCodes.Status403Forbidden
            });

        var today = LeadershipPosts.Today();
        ActingHeadDto? next = null;
        Dictionary<Guid, Person> people = (await PeopleAsync(org)).ToDictionary(p => p.Id);

        if (request.UserId is { } userId)
        {
            if (!people.TryGetValue(userId, out var p))
                return Problem400("Unknown person", "That person is not an active member of this school.");
            if (userId == Me())
                return Problem400("Not yourself", "Nobody appoints themselves acting head.");
            if (!RoleCodes.IsIn(RoleCodes.ActingHeadEligible, p.RoleCode))
                return Problem400("Cannot act as head", $"{p.FullName} is {p.RoleName}. An acting head must be a Deputy Head Teacher or the Director of Studies.");

            var starts = request.StartsOn ?? today;
            if (request.EndsOn is not { } ends)
                return Problem400("End date required", "An acting-head period needs an end date, so the access ends without anybody having to remember.");
            if (starts < today) return Problem400("Start date passed", "The period cannot start in the past.");
            if (ends < starts) return Problem400("Dates the wrong way round", "The end date is before the start date.");
            if (ends > today.AddDays(LeadershipPosts.MaxActingHeadDays))
                return Problem400("Too long", $"An acting-head period ends within {LeadershipPosts.MaxActingHeadDays} days. A longer absence is a change of role.");
            var reason = (request.Reason ?? string.Empty).Trim();
            if (reason.Length < 10) return Problem400("Reason required", "Say why, in ten characters or more — for example, \"Head on study leave\".");

            next = new ActingHeadDto
            {
                UserId = userId, StartsOn = starts, EndsOn = ends, Reason = reason,
                AppointedByUserId = Me(), AppointedAt = DateTime.UtcNow
            };
        }

        LeadershipPostsDto before = new();
        await LeadershipPosts.WriteAsync(_db, org, current => { before = current; return current with { ActingHead = next }; });

        var changed = new[] { before.ActingHead?.UserId, next?.UserId }.OfType<Guid>().ToList();
        await InvalidateAsync(changed, "acting head");

        if (next != null && before.ActingHead?.UserId != next.UserId)
            await TellAsync(org, new[] { next.UserId }, "You will be acting head",
                $"From {QDates(next.StartsOn)} to {QDates(next.EndsOn)} you hold everything the Head Teacher holds. It ends on its own after that day.");
        if (before.ActingHead is { } old && old.UserId != next?.UserId)
            await TellAsync(org, new[] { old.UserId }, "You are no longer acting head", "The access that came with it has ended.");

        string Name(Guid? id) => id is { } i && people.TryGetValue(i, out var p) ? p.FullName : "nobody";
        await _activity.RecordAsync(ActivityActions.ActingHeadChanged, "Organization", org, next?.UserId,
            next == null ? $"Acting head ended ({Name(before.ActingHead?.UserId)})"
                         : $"{Name(next.UserId)} acting head {QDates(next.StartsOn)} to {QDates(next.EndsOn)}",
            new { before = before.ActingHead, after = next }, organizationId: org, visibility: WelfareVisibility.Confidential);

        return await GetLeadership();
    }

    private static string QDates(DateOnly d) => d.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    // ---- R4: house and dormitory posts ----------------------------------------------------------------

    private async Task<IActionResult?> VerifyBranchAsync(Guid branchId)
    {
        var org = OrganizationId;
        var ok = IsSuperAdmin
            ? await _db.Branches.AnyAsync(b => b.Id == branchId && (org == Guid.Empty || b.OrganizationId == org))
            : await _db.Branches.AnyAsync(b => b.Id == branchId && b.OrganizationId == org);
        return ok ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
    }

    private async Task<(List<string> Houses, List<string> Dormitories)> UnitsAsync(Guid branchId)
    {
        var json = await _db.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var vocab = StudentsController.ReadVocabularies(json);
        return (vocab.Houses.Where(h => h.IsActive).OrderBy(h => h.SortOrder).Select(h => h.Name).ToList(),
                vocab.Dormitories.Where(h => h.IsActive).OrderBy(h => h.SortOrder).Select(h => h.Name).ToList());
    }

    /// <summary>The branch's house and dormitory posts, and the houses and dormitories it has.</summary>
    [HttpGet("branches/{branchId:guid}/pastoral-posts")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [RequireModule(ModuleCodes.StudentWelfare)]
    public async Task<IActionResult> GetPastoralPosts(Guid branchId)
    {
        if (await VerifyBranchAsync(branchId) is { } err) return err;
        var org = await _db.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync();
        var posts = await LeadershipPosts.ReadAsync(_db, org);
        var people = (await PeopleAsync(org)).ToDictionary(p => p.Id);
        var (houses, dorms) = await UnitsAsync(branchId);

        return Ok(new PastoralUnitPostsViewDto
        {
            Posts = posts.PastoralUnitPosts.Where(p => p.BranchId == branchId)
                .Select(p => new PastoralUnitPostViewDto(p.Kind, p.Name, p.UserId, people.TryGetValue(p.UserId, out var x) ? x.FullName : "Former member of staff"))
                .OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Houses = houses,
            Dormitories = dorms,
        });
    }

    /// <summary>
    /// Replace this branch's house and dormitory posts. A holder gets the class teacher's pastoral permissions over the
    /// students of that house or dormitory, and nothing wider — the same grant, the same gate
    /// (<c>classes.teachers.manage</c>) as naming a class teacher.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/pastoral-posts")]
    [RequirePermission(Permissions.ClassTeachersManage)]
    [RequireModule(ModuleCodes.StudentWelfare)]
    public async Task<IActionResult> UpdatePastoralPosts(Guid branchId, [FromBody] UpdatePastoralUnitPostsRequest request)
    {
        if (await VerifyBranchAsync(branchId) is { } err) return err;
        var org = await _db.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync();
        if (request.Posts.Count > 200) return Problem400("Too many posts", "A branch holds at most 200 house and dormitory posts.");

        var (houses, dorms) = await UnitsAsync(branchId);
        var people = (await PeopleAsync(org)).ToDictionary(p => p.Id);
        var clean = new List<PastoralUnitPostDto>();
        foreach (var p in request.Posts)
        {
            var list = p.Kind == PastoralUnitKind.House ? houses : dorms;
            var name = list.FirstOrDefault(n => string.Equals(n.Trim(), (p.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            if (name == null)
                return Problem400("Unknown " + (p.Kind == PastoralUnitKind.House ? "house" : "dormitory"),
                    $"\"{p.Name}\" is not one of this branch's {(p.Kind == PastoralUnitKind.House ? "houses" : "dormitories")}. Add it under Students → Vocabularies first.");
            if (!people.ContainsKey(p.UserId))
                return Problem400("Unknown person", "One of the people named is not an active member of this school.");
            if (!clean.Any(c => c.Kind == p.Kind && c.Name == name && c.UserId == p.UserId))
                clean.Add(new PastoralUnitPostDto { BranchId = branchId, Kind = p.Kind, Name = name, UserId = p.UserId });
        }

        LeadershipPostsDto before = new();
        await LeadershipPosts.WriteAsync(_db, org, current =>
        {
            before = current;
            return current with { PastoralUnitPosts = current.PastoralUnitPosts.Where(x => x.BranchId != branchId).Concat(clean).ToList() };
        });

        static string Key(PastoralUnitPostDto p) => $"{p.Kind}|{p.Name}|{p.UserId}";
        var oldPosts = before.PastoralUnitPosts.Where(x => x.BranchId == branchId).ToList();
        var added = clean.Where(n => !oldPosts.Any(o => Key(o) == Key(n))).ToList();
        var removed = oldPosts.Where(o => !clean.Any(n => Key(n) == Key(o))).ToList();
        await InvalidateAsync(added.Concat(removed).Select(p => p.UserId), "house and dormitory posts");

        foreach (var g in added.GroupBy(a => a.UserId))
            await TellAsync(org, new[] { g.Key }, "You have a pastoral post",
                $"You now look after {string.Join(", ", g.Select(p => (p.Kind == PastoralUnitKind.House ? "house " : "dormitory ") + p.Name))}: you can read and log welfare records for those students.");
        foreach (var g in removed.GroupBy(a => a.UserId))
            await TellAsync(org, new[] { g.Key }, "A pastoral post ended",
                $"You no longer look after {string.Join(", ", g.Select(p => (p.Kind == PastoralUnitKind.House ? "house " : "dormitory ") + p.Name))}.");

        if (added.Count + removed.Count > 0)
            await _activity.RecordAsync(ActivityActions.PastoralPostsChanged, "Branch", branchId, null,
                $"House and dormitory posts: {added.Count} added, {removed.Count} ended",
                new { added, removed }, branchId: branchId, organizationId: org, visibility: WelfareVisibility.Confidential);

        return await GetPastoralPosts(branchId);
    }

    // ---- R5: the access review ---------------------------------------------------------------------------

    /// <summary>
    /// Who can do the things that matter most — read restricted and confidential records, manage accounts, design
    /// roles, pay — and which posts each person holds. NIST SP 800-53 AC-2 asks for accounts to be reviewed; this is
    /// the page a head does it on, once a term.
    /// </summary>
    [HttpGet("access-review")]
    [RequirePermission(Permissions.UsersEdit)]
    public async Task<IActionResult> GetAccessReview()
    {
        var org = OrganizationId;
        if (org == Guid.Empty) return NoOrganization();
        var effective = await PostPermissionService.EffectiveCodesAsync(_db, Me());
        if (!IsSuperAdmin && !effective.Contains(Permissions.RolesView))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Title = "You cannot read the access review", Status = StatusCodes.Status403Forbidden });

        var users = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == org && u.IsActive && u.PendingApprovalAt == null && u.Role.Code != RoleCodes.SuperAdmin)
            .Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName, u.Username, u.RoleId, RoleName = u.Role.Name })
            .ToListAsync();
        var roleIds = users.Select(u => u.RoleId).Distinct().ToList();
        var rolePerms = (await _db.RolePermissions.IgnoreQueryFilters().AsNoTracking()
                .Where(rp => roleIds.Contains(rp.RoleId)).Select(rp => new { rp.RoleId, rp.Permission.Code }).ToListAsync())
            .GroupBy(x => x.RoleId).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var userIds = users.Select(u => u.Id).ToList();
        var classes = (await _db.ClassTeacherAssignments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => userIds.Contains(a.UserId) && a.EndedAt == null
                    && (a.Role == ClassTeacherRole.ClassTeacher || a.Role == ClassTeacherRole.Assistant))
                .Select(a => new { a.UserId, a.ClassName, a.Role }).ToListAsync())
            .GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.ToList());
        var departments = await _db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.IsActive && d.OrganizationId == org && (d.HeadUserId != null || d.DeputyHeadUserId != null))
            .Select(d => new { d.Name, d.HeadUserId, d.DeputyHeadUserId }).ToListAsync();
        var leadership = await LeadershipPosts.ReadAsync(_db, org);
        var today = LeadershipPosts.Today();
        var actingSet = LeadershipPosts.ActingHeadPost();

        var rows = new List<AccessReviewRowDto>();
        foreach (var u in users)
        {
            var codes = new HashSet<string>(rolePerms.GetValueOrDefault(u.RoleId) ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            var posts = new List<string>();

            if (classes.TryGetValue(u.Id, out var cls))
            {
                foreach (var c in cls) posts.Add((c.Role == ClassTeacherRole.ClassTeacher ? "Class teacher of " : "Assistant class teacher of ") + c.ClassName);
                codes.UnionWith(PostPermissionService.PastoralClassPost);
            }
            foreach (var d in departments.Where(d => d.HeadUserId == u.Id || d.DeputyHeadUserId == u.Id))
            {
                posts.Add((d.HeadUserId == u.Id ? "Head of " : "Deputy head of ") + d.Name);
                codes.UnionWith(PostPermissionService.DepartmentHeadPost);
            }
            if (leadership.SafeguardingLeadUserId == u.Id) { posts.Add("Safeguarding lead"); codes.UnionWith(LeadershipPosts.SafeguardingLeadPost); }
            if (leadership.DeputySafeguardingLeadUserIds.Contains(u.Id)) { posts.Add("Deputy safeguarding lead"); codes.UnionWith(LeadershipPosts.SafeguardingLeadPost); }
            if (leadership.ActingHead is { } ah && ah.UserId == u.Id && ah.EndsOn >= today)
            {
                posts.Add(ah.IsActiveOn(today) ? $"Acting head to {QDates(ah.EndsOn)}" : $"Acting head from {QDates(ah.StartsOn)}");
                if (ah.IsActiveOn(today)) codes.UnionWith(actingSet);
            }
            foreach (var p in leadership.PastoralUnitPosts.Where(p => p.UserId == u.Id))
            {
                posts.Add((p.Kind == PastoralUnitKind.House ? "House " : "Dormitory ") + p.Name);
                codes.UnionWith(PostPermissionService.PastoralClassPost);
            }

            rows.Add(new AccessReviewRowDto
            {
                UserId = u.Id,
                FullName = PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName, u.Username),
                SortName = PersonNames.SortKey(u.OrganizationId, u.FirstName, u.LastName),
                RoleName = u.RoleName,
                RestrictedWelfare = codes.Contains(Permissions.WelfareRestrictedView),
                ConfidentialWelfare = codes.Contains(Permissions.WelfareConfidentialView),
                RestrictedStaff = codes.Contains(Permissions.StaffRestrictedView),
                ManagesAccounts = codes.Contains(Permissions.UsersEdit),
                DesignsRoles = codes.Contains(Permissions.RolesEdit),
                ManagesBilling = codes.Contains(Permissions.BillingManage),
                Posts = posts,
            });
        }

        var last = await _db.ActivityEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org && e.Action == ActivityActions.AccessReviewed)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.OccurredAt, e.ActorUserId, e.DetailJson })
            .FirstOrDefaultAsync();
        string? by = null, note = null;
        if (last != null)
        {
            by = last.ActorUserId is { } a ? (await PeopleAsync(org)).FirstOrDefault(p => p.Id == a)?.FullName : null;
            try { note = last.DetailJson == null ? null : JsonDocument.Parse(last.DetailJson).RootElement.GetProperty("note").GetString(); }
            catch { note = null; }
        }

        return Ok(new AccessReviewDto
        {
            Rows = rows.OrderBy(r => r.SortName, StringComparer.OrdinalIgnoreCase).ToList(),
            LastReviewedAt = last?.OccurredAt,
            LastReviewedBy = by,
            LastReviewNote = note,
            CanConfirm = IsSuperAdmin || (effective.Contains(Permissions.UsersEdit) && effective.Contains(Permissions.WelfareRestrictedView)),
        });
    }

    /// <summary>Record that the review was done, with a note. The Administrator, the Head Teacher or an acting head.</summary>
    [HttpPost("access-review/confirm")]
    [RequirePermission(Permissions.UsersEdit)]
    public async Task<IActionResult> ConfirmAccessReview([FromBody] ConfirmAccessReviewRequest request)
    {
        var org = OrganizationId;
        if (org == Guid.Empty) return NoOrganization();
        var effective = await PostPermissionService.EffectiveCodesAsync(_db, Me());
        if (!IsSuperAdmin && !effective.Contains(Permissions.WelfareRestrictedView))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You cannot record the review",
                Detail = "The review is recorded by the Administrator, the Head Teacher or an acting head.",
                Status = StatusCodes.Status403Forbidden
            });
        var noteText = (request.Note ?? string.Empty).Trim();
        if (noteText.Length < 10) return Problem400("Note required", "Say what was checked or changed, in ten characters or more.");
        if (noteText.Length > 1000) noteText = noteText[..1000];

        await _activity.RecordAsync(ActivityActions.AccessReviewed, "Organization", org, null,
            "Access review recorded", new { note = noteText }, organizationId: org, visibility: WelfareVisibility.Confidential);
        return await GetAccessReview();
    }
}
