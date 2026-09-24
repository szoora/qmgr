using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;


/// <summary>
/// The posts one person holds, and what they grant. <see cref="Permissions"/> is the derived set
/// alone — never unioned with the role's here, because the two scope rules below need to know which
/// of the two a permission came from.
/// </summary>
public sealed record PostGrants
{
    public static readonly PostGrants None = new();

    /// <summary>Permission codes granted by a post. Empty for somebody holding no post.</summary>
    public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The caller holds at least one live <see cref="ClassTeacherRole.ClassTeacher"/> or
    /// <see cref="ClassTeacherRole.Assistant"/> assignment. This is what narrows an
    /// organization-scoped role to its classes when the welfare grant is derived rather than held.
    /// </summary>
    public bool HasPastoralPost { get; init; }

    /// <summary>
    /// The caller is head or deputy head of at least one active department. This is what gives the
    /// derived staff permissions a reach: without it they open a Records page showing only the
    /// caller themselves.
    /// </summary>
    public bool HasDepartmentPost { get; init; }

    /// <summary>
    /// The caller holds a post whose reach is the WHOLE SCHOOL on the student axis — safeguarding lead, a deputy
    /// lead, or acting head while the period covers today (2026-09-24). Such a post makes the caller unscoped on
    /// students whatever their role's own scope: reaching every child is what those posts are for.
    /// </summary>
    public bool HasOrganizationWidePost { get; init; }

    /// <summary>House and dormitory posts the caller holds, in every branch. Each adds the students of that house or
    /// dormitory to the caller's pastoral reach, exactly as a class does.</summary>
    public IReadOnlyList<PastoralUnitPostDto> PastoralUnits { get; init; } = Array.Empty<PastoralUnitPostDto>();

    public bool Any => Permissions.Count > 0;
}

/// <summary>
/// What a person's POSTS grant them, on top of their role.
///
/// <para><b>The defect this exists to close.</b> Making somebody the class teacher of S4B gave them
/// the pastoral <i>scope</i> over S4B and not one pastoral <i>permission</i>: the assignment saved,
/// the page showed them as the class teacher, the coverage warning stopped naming that class — and
/// every welfare feature silently refused them, because
/// <c>PermissionAuthorizationHandler.GetUserPermissionsAsync</c> is one query,
/// <c>Users → Role → RolePermissions</c>, and nothing else contributed. Head of department had the
/// identical defect on the staff axis. See <c>docs/plans/DERIVED_POST_PERMISSIONS.md</c>.</para>
///
/// <para><b>A POST GRANTS PERMISSIONS AND SCOPE AS A PAIR, NEVER PERMISSIONS ALONE (plan §2.6).</b>
/// This was the audit's most serious finding and it is the reason this type returns
/// <see cref="PostGrants"/> rather than a bare list of codes. Three seeded roles carry
/// <c>DataScope: Organization</c> and two of them hold no welfare permission at all
/// (<c>support-staff</c>, <c>viewer</c>). Deriving welfare access onto one of those without also
/// deriving the scope would hand a matron who is the class teacher of S4B <b>every child in the
/// school</b>: <c>StudentScopeService.ApplyAsync</c> begins <c>if (await IsUnscopedAsync()) return
/// query;</c>, so an organization-scoped caller passes every one of the 29 guards in
/// <c>WelfareController</c> by construction. Today the only thing preventing that is the missing
/// permission — which is exactly what deriving removes.</para>
///
/// <para><b>The rule: the post's scope applies to the post's permissions; where the ROLE already
/// grants the permission, the ROLE's scope wins.</b> It reads as a narrowing on the student axis
/// (Organization → the classes held) and as a widening on the staff axis (SelfOnly → the
/// departments headed), because the two baselines differ — but it is one rule either way: a
/// permission that arrived with a post is exercised at that post's reach.</para>
///
/// <para><b>Subject teachers grant NOTHING here.</b> <see cref="ClassTeacherRole.SubjectTeacher"/>
/// already yields <see cref="StudentAccessTier.Teaching"/> through <c>IStudentScopeService</c>, and
/// the <c>teacher</c> role's own definition is explicit that it gets <c>students.view</c> only and
/// never a welfare permission. Deriving welfare access from a subject-teaching assignment would
/// hand every teacher in the school pastoral access to every class they teach — the opposite of the
/// duty rota plan's §5.3 decision.</para>
/// </summary>
// NOTE: deliberately a STATIC class and no interface. Nothing needs per-request state here (the two
// queries are cheap and each caller already memoises its own answer), and a DI registration nothing
// injects is the dead weight this codebase swept out on 2026-09-22.
public static class PostPermissionService
{
    /// <summary>
    /// What a live pastoral class assignment grants.
    ///
    /// <para><b>These are DECLARED CONSTANTS and must stay so.</b> An earlier draft read them from
    /// the seeded <c>class-teacher</c> role definition so the two could not drift — but that role is
    /// deleted (plan §5 decision 3), so there is nothing left to read from and this becomes the one
    /// home by construction rather than by discipline.</para>
    ///
    /// <para><b><c>welfare.reports.own</c> IS included, corrected 2026-09-22.</b> The plan's §5 decision 1
    /// left it out on the reasoning that running reports is a separate act — but the seeded
    /// <c>class-teacher</c> role HELD it, and that role was deleted in the same change, so excluding it
    /// removed a capability a class teacher had the day before. That is exactly what the note on
    /// <see cref="DepartmentHeadPost"/> below forbids for <c>timetable.lessons.flag</c>; the same principle
    /// simply was not applied here. Found by e2e sections 1, 3b, 8 and 9, which lost their own-class summary,
    /// record search and cohort report to a 403.</para>
    ///
    /// <para>It is the OWN code and never the branch-wide <c>welfare.reports.view</c> — a 2026-09-18 user
    /// decision, so that a school can withhold the page from a custom class-teacher role without touching what
    /// a manager reads — and <see cref="IStudentScopeService"/> narrows it to the classes held on top. It
    /// answers "how is my class doing" and nothing wider.</para>
    /// </summary>
    public static readonly string[] PastoralClassPost =
    {
        Permissions.StudentsView,
        Permissions.WelfareView,
        Permissions.WelfareCreate,
        Permissions.WelfareEdit,
        Permissions.WelfareNotify,
        Permissions.WelfareReportsOwn
    };

    /// <summary>
    /// What heading (or deputy-heading) a department grants. Head and deputy alike — plan §5
    /// decision 2: a deputy exists to act when the head cannot, and a deputy who cannot see the
    /// department's records is not a deputy.
    ///
    /// <para><c>timetable.lessons.flag</c> is included because the seeded
    /// <c>head-of-department</c> role held it, and that role is being deleted: excluding it would
    /// make the deletion remove a capability somebody has today.</para>
    /// </summary>
    public static readonly string[] DepartmentHeadPost =
    {
        Permissions.StaffRecordsView,
        Permissions.StaffRecordsCreate,
        Permissions.StaffRecordsEdit,
        Permissions.StaffDutiesManage,
        Permissions.StaffAppraisalsConduct,
        Permissions.StaffReportsView,
        Permissions.StaffDutyReportsView,
        Permissions.TimetableLessonsFlag
    };

    /// <summary>
    /// The permissions whose presence on the ROLE means the role already reaches students
    /// organization-wide, so a pastoral post must NOT narrow it. A Tenant Admin or Manager who is
    /// also the class teacher of S4B keeps the school; taking it away because they picked up a
    /// class would be a worse bug than the one this plan fixes.
    ///
    /// <para><b>SPELLED OUT RATHER THAN aliased to <see cref="PastoralClassPost"/>, since 2026-09-22.</b>
    /// The two lists answer different questions and it was only a coincidence that they matched. This one asks
    /// "does the role already read STUDENTS org-wide?", so it must not gain <c>welfare.reports.own</c> when the
    /// grant list does: reports are aggregates, and a role holding nothing but the own-class report code does
    /// not thereby reach every child in the school. Aliasing them would have widened this test silently.</para>
    /// </summary>
    private static readonly string[] RoleHeldStudentAccess =
    {
        Permissions.StudentsView,
        Permissions.WelfareView,
        Permissions.WelfareCreate,
        Permissions.WelfareEdit,
        Permissions.WelfareNotify
    };

    /// <summary>
    /// The static core, so a background job or a singleton authorization handler resolves the same
    /// answer without an HTTP context. There is one rule, not two — the same shape as
    /// <c>StaffScopeService.VisibleUserIdsForAsync</c>.
    ///
    /// <para><b>FAILS CLOSED.</b> An unknown or inactive user holds no post.</para>
    /// </summary>
    /// <summary>
    /// THE EFFECTIVE PERMISSION SET: what the person's ROLE grants, plus what their POSTS grant on top.
    /// The ONE home for that union, and it exists because there were SIX readers of "what may this person do"
    /// and only one of them knew about posts (found 2026-09-22).
    ///
    /// <para>The other five all wrote <c>SelectMany(u => u.Role.RolePermissions)</c> by hand:
    /// <c>StaffPerformanceControllerBase.HasPermissionAsync</c> (73 call sites), <c>BatchController</c>,
    /// <c>ClassTeachersController</c>, <c>ContentController</c> and <c>DocumentSharesController</c>. So a
    /// derived permission passed a <c>[RequirePermission]</c> attribute and then FAILED the in-code check
    /// inside the same endpoint — a class teacher's welfare access worked or did not depending on which style
    /// the endpoint happened to use. The teaching reports refused a department head outright.</para>
    ///
    /// <para><b>This is not cached here.</b> The authorization handler caches the result for five minutes and
    /// invalidates on every post write (<c>IStaffProfileChangeNotifier.PostChangedAsync</c>); a controller
    /// memoises it for the life of one request. Caching in two places with two lifetimes is how the two would
    /// come to disagree.</para>
    /// </summary>
    public static async Task<HashSet<string>> EffectiveCodesAsync(QMgrDbContext db, Guid userId, CancellationToken ct = default)
    {
        var codes = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        var posts = await GrantsForAsync(db, userId, ct);
        foreach (var code in posts.Permissions) codes.Add(code);
        return codes;
    }

    public static async Task<PostGrants> GrantsForAsync(QMgrDbContext db, Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return PostGrants.None;

        // IgnoreQueryFilters: this runs from the authorization handler, before a tenant context
        // exists on the request, and from background jobs that have none at all.
        var pastoral = await db.ClassTeacherAssignments.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(a => a.UserId == userId
                        && a.EndedAt == null
                        && (a.Role == ClassTeacherRole.ClassTeacher || a.Role == ClassTeacherRole.Assistant), ct);

        var department = await db.Departments.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => d.IsActive && (d.HeadUserId == userId || d.DeputyHeadUserId == userId), ct);

        // THE LEADERSHIP POSTS (2026-09-24): safeguarding lead and deputies, acting head, house and dormitory.
        // One read of the organization's settings; LeadershipPosts is the only parser of that key.
        var organizationId = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive).Select(u => (Guid?)u.OrganizationId).FirstOrDefaultAsync(ct);
        var leadership = organizationId is { } org ? await LeadershipPosts.ReadAsync(db, org, ct) : new LeadershipPostsDto();

        var safeguarding = leadership.SafeguardingLeadUserId == userId || leadership.DeputySafeguardingLeadUserIds.Contains(userId);
        var actingHead = leadership.ActingHead is { } ah && ah.UserId == userId && ah.IsActiveOn(LeadershipPosts.Today());
        var units = leadership.PastoralUnitPosts.Where(p => p.UserId == userId).ToList();

        if (!pastoral && !department && !safeguarding && !actingHead && units.Count == 0) return PostGrants.None;

        var codes = new HashSet<string>(StringComparer.Ordinal);
        if (pastoral || units.Count > 0) foreach (var c in PastoralClassPost) codes.Add(c);
        if (department) foreach (var c in DepartmentHeadPost) codes.Add(c);
        if (safeguarding) foreach (var c in LeadershipPosts.SafeguardingLeadPost) codes.Add(c);
        if (actingHead) foreach (var c in LeadershipPosts.ActingHeadPost()) codes.Add(c);

        return new PostGrants
        {
            Permissions = codes,
            HasPastoralPost = pastoral || units.Count > 0,
            HasDepartmentPost = department,
            HasOrganizationWidePost = safeguarding || actingHead,
            PastoralUnits = units,
        };
    }

    /// <summary>
    /// The student-axis half of plan §2.6, and the one home for it.
    ///
    /// <para>Answers: <i>is this caller unscoped on the student axis?</i> — which used to be a bare
    /// read of <c>Role.DataScope</c>. A caller is unscoped only when their ROLE is organization-wide
    /// AND the role itself grants student access. A role that is organization-wide but holds no
    /// student permission of its own, whose holder is a class teacher, is scoped to that post's
    /// classes: the access arrived with the post, so it runs at the post's reach.</para>
    ///
    /// <para>Note the third case is unchanged and must stay so: an organization-scoped role with no
    /// student permission and no post stays "unscoped" here, because it can reach nothing anyway and
    /// narrowing it would change nothing but make the answer harder to read.</para>
    /// </summary>
    public static bool IsUnscopedOnStudents(RoleDataScope roleScope, IReadOnlyCollection<string> rolePermissions, PostGrants posts)
    {
        // A whole-school post reaches every child, whatever the role's own scope — that is the post.
        if (posts.HasOrganizationWidePost) return true;
        if (roleScope != RoleDataScope.Organization) return false;
        if (!posts.HasPastoralPost) return true;
        return rolePermissions.Any(p => RoleHeldStudentAccess.Contains(p, StringComparer.OrdinalIgnoreCase));
    }
}
