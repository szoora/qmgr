using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// Lesson plans and schemes of work: the chain, the access rule and the stored shapes (plan
/// LESSON_PLANS_AND_SCHEMES_OF_WORK §4). The controller, the upload authorizer, the reminder ladder and the reports all
/// call THIS, so a plan and its file can never disagree about who may read them.
///
/// <list type="bullet">
/// <item><b>Stage 1 is the head of the subject's department</b>, or its deputy when the head wrote the plan. With nobody
/// left, the stage is skipped and the reason recorded — it is never handed to the author.</item>
/// <item><b>Stage 2 is a holder of <c>teaching.plans.approve</c></b> with the author in their staff scope — never the author,
/// never whoever forwarded it (<see cref="DutySeparation"/>). With nobody left, the plan waits and says so.</item>
/// <item>Out of reach is 404, never 403: a 403 confirms the plan exists.</item>
/// </list>
/// </summary>
public static class TeachingPlans
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Stored shapes ---------------------------------------------------------------------------------

    public static LessonPlanContentDto ContentOf(TeachingPlan p)
    {
        try { return JsonSerializer.Deserialize<LessonPlanContentDto>(p.SectionsJson, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static List<SchemeRowDto> RowsOf(TeachingPlan p)
    {
        try { return JsonSerializer.Deserialize<List<SchemeRowDto>>(p.RowsJson, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static List<PlanTrailEntryDto> TrailOf(TeachingPlan p)
    {
        try { return JsonSerializer.Deserialize<List<PlanTrailEntryDto>>(p.TrailJson, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Appends to the trail. The trail is append-only: nothing in the product removes an entry.</summary>
    public static void AddTrail(TeachingPlan p, PlanTrailKind kind, Guid? by, string? body = null, string? sectionKey = null, string? snapshot = null)
    {
        var trail = TrailOf(p);
        trail.Add(new PlanTrailEntryDto { Kind = kind, ByUserId = by, At = DateTime.UtcNow, Body = body, SectionKey = sectionKey, SnapshotJson = snapshot });
        p.TrailJson = Serialize(trail);
    }

    /// <summary>Cleans submitted content: trims, caps lengths, drops empty procedure rows and unknown keys.</summary>
    public static LessonPlanContentDto Clean(LessonPlanContentDto? content, IReadOnlyList<PlanSectionDto> sections)
    {
        content ??= new();
        var keys = sections.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        return new LessonPlanContentDto
        {
            Header = content.Header ?? new(),
            Answers = (content.Answers ?? new())
                .Where(kv => keys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => Cap(kv.Value.Trim(), 4000)),
            Procedure = (content.Procedure ?? new())
                .Where(p => !string.IsNullOrWhiteSpace(p.Teacher) || !string.IsNullOrWhiteSpace(p.Learner) || !string.IsNullOrWhiteSpace(p.Phase))
                .Take(20)
                .Select(p => new ProcedureStepDto
                {
                    Phase = Cap((p.Phase ?? string.Empty).Trim(), 60),
                    Minutes = p.Minutes is > 0 and <= 600 ? p.Minutes : null,
                    Teacher = p.Teacher == null ? null : Cap(p.Teacher.Trim(), 2000),
                    Learner = p.Learner == null ? null : Cap(p.Learner.Trim(), 2000)
                }).ToList()
        };
    }

    public static List<SchemeRowDto> Clean(List<SchemeRowDto>? rows, IReadOnlyList<PlanColumnDto> columns)
    {
        var keys = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        return (rows ?? new()).Take(60).Select(r => new SchemeRowDto
        {
            Key = string.IsNullOrWhiteSpace(r.Key) ? Guid.NewGuid().ToString("N")[..12] : Cap(r.Key.Trim(), 40),
            Week = Math.Clamp(r.Week, 1, 52),
            Periods = r.Periods is > 0 and <= 60 ? r.Periods : null,
            Cells = (r.Cells ?? new()).Where(kv => keys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => Cap(kv.Value.Trim(), 2000))
        }).OrderBy(r => r.Week).ToList();
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    // ---- The chain -------------------------------------------------------------------------------------

    /// <summary>The subject's department and who leads it, read when the plan is acted on — a new head takes the queue.</summary>
    public sealed record Chain(Guid? DepartmentId, string? DepartmentName, Guid? HeadUserId, Guid? DeputyUserId, string SubjectName)
    {
        /// <summary>Who takes stage 1 for this author: the head, and the deputy — never the author.</summary>
        public IReadOnlyList<Guid> Stage1For(Guid authorId)
            => new[] { HeadUserId, DeputyUserId }.Where(id => id.HasValue && id.Value != authorId).Select(id => id!.Value).Distinct().ToList();

        public bool IsStage1(Guid userId, Guid authorId) => Stage1For(authorId).Contains(userId);
    }

    public static async Task<Chain> ChainAsync(QMgrDbContext db, Guid organizationId, Guid subjectId, CancellationToken ct = default)
    {
        var subject = await db.Subjects.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == subjectId && s.OrganizationId == organizationId)
            .Select(s => new { s.Name, s.DepartmentId }).FirstOrDefaultAsync(ct);
        if (subject?.DepartmentId is not { } departmentId) return new Chain(null, null, null, null, subject?.Name ?? "Subject");

        var department = await db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Id == departmentId && d.OrganizationId == organizationId)
            .Select(d => new { d.Name, d.HeadUserId, d.DeputyHeadUserId }).FirstOrDefaultAsync(ct);
        if (department == null) return new Chain(null, null, null, null, subject.Name);

        // A head or deputy who has left, or been switched off, takes nothing.
        var ids = new[] { department.HeadUserId, department.DeputyHeadUserId }.Where(i => i.HasValue).Select(i => i!.Value).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var active = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.IsActive && (u.EmploymentEndDate == null || u.EmploymentEndDate >= today))
            .Select(u => u.Id).ToListAsync(ct);
        Guid? Keep(Guid? id) => id.HasValue && active.Contains(id.Value) ? id : null;
        return new Chain(departmentId, department.Name, Keep(department.HeadUserId), Keep(department.DeputyHeadUserId), subject.Name);
    }

    /// <summary>Stage 2 for this plan: approvers with the author in scope, less the author and whoever forwarded it.</summary>
    public static async Task<List<Guid>> ApproversAsync(QMgrDbContext db, TeachingPlan plan, Func<Guid, Guid, Task<bool>> seesStaff, CancellationToken ct = default)
    {
        var holders = await StaffLookups.UsersWithPermissionAsync(db, plan.OrganizationId, Permissions.TeachingPlansApprove, ct, plan.BranchId);
        var result = new List<Guid>();
        foreach (var id in holders.Distinct())
            if (DutySeparation.Allows(id, plan.AuthorUserId, plan.ForwardedByUserId) && await seesStaff(id, plan.AuthorUserId))
                result.Add(id);
        return result;
    }

    /// <summary>How many stages a plan takes (decision L4): a scheme always two; a lesson plan the school's setting.</summary>
    public static int StagesFor(TeachingPlanKind kind, TeachingPlanSettingsDto settings)
        => kind == TeachingPlanKind.SchemeOfWork ? 2 : settings.LessonPlanStages;

    // ---- Access ----------------------------------------------------------------------------------------

    public sealed record Access(bool CanRead, bool IsAuthor, bool IsStage1, bool IsApprover, bool CanEdit, bool CanSubmit, bool CanWithdraw,
        bool CanForward, bool CanApprove, bool CanReturn, bool CanComment, bool CanReflect, bool CanRevise, string? WhyNot)
    {
        public static readonly Access None = new(false, false, false, false, false, false, false, false, false, false, false, false, false, null);
    }

    /// <summary>
    /// The one read-and-act rule. <paramref name="hasPermission"/> is the CALLER's effective permission set (role plus
    /// posts); <paramref name="seesAuthor"/> is the caller's staff scope over the author; <paramref name="isDepartmentMember"/>
    /// answers the department-sharing switch (decision L12).
    /// </summary>
    public static async Task<Access> AccessForAsync(Guid callerId, TeachingPlan plan, Chain chain, TeachingPlanSettingsDto settings,
        Func<string, Task<bool>> hasPermission, Func<Task<bool>> seesAuthor, Func<Task<bool>> isDepartmentMember)
    {
        if (callerId == Guid.Empty) return Access.None;
        var isAuthor = plan.AuthorUserId == callerId;
        var s = plan.Status;
        var inFlight = s is TeachingPlanStatus.Submitted or TeachingPlanStatus.Forwarded;

        if (isAuthor)
        {
            var editable = s is TeachingPlanStatus.Draft or TeachingPlanStatus.Returned;
            return new Access(true, true, false, false,
                CanEdit: editable, CanSubmit: editable, CanWithdraw: inFlight,
                CanForward: false, CanApprove: false, CanReturn: false,
                CanComment: s != TeachingPlanStatus.Draft && s != TeachingPlanStatus.Withdrawn,
                CanReflect: plan.Kind == TeachingPlanKind.LessonPlan && s != TeachingPlanStatus.Withdrawn,
                CanRevise: s == TeachingPlanStatus.Approved && plan.IsCurrent,
                WhyNot: null);
        }

        // Nobody but the author reads a draft or a discarded plan.
        if (s is TeachingPlanStatus.Draft or TeachingPlanStatus.Withdrawn) return Access.None;

        var isStage1 = chain.IsStage1(callerId, plan.AuthorUserId) && await hasPermission(Permissions.TeachingPlansReview);
        var mayApprove = await hasPermission(Permissions.TeachingPlansApprove) && await seesAuthor();
        var mayView = s == TeachingPlanStatus.Approved
                      && ((await hasPermission(Permissions.TeachingPlansView) && await seesAuthor())
                          || (settings.ShareApprovedWithDepartment && await isDepartmentMember()));
        var actedBefore = plan.ForwardedByUserId == callerId || plan.ApprovedByUserId == callerId || plan.ReturnedByUserId == callerId;

        if (!isStage1 && !mayApprove && !mayView && !actedBefore) return Access.None;

        string? whyNot = null;
        var forward = false; var approve = false;
        if (s == TeachingPlanStatus.Submitted && isStage1)
        {
            // One-stage plans are approved AT stage 1; two-stage plans are forwarded.
            if (plan.Stages == 1) approve = true; else forward = true;
        }
        if (s == TeachingPlanStatus.Forwarded && mayApprove)
        {
            whyNot = DutySeparation.Refusal(callerId, plan.AuthorUserId, "approve this plan", plan.ForwardedByUserId);
            approve = whyNot == null;
        }

        return new Access(true, false, isStage1, mayApprove,
            CanEdit: false, CanSubmit: false, CanWithdraw: false,
            CanForward: forward, CanApprove: approve, CanReturn: forward || approve,
            CanComment: s != TeachingPlanStatus.Approved ? (isStage1 || mayApprove) : (isStage1 || mayApprove || actedBefore),
            CanReflect: false, CanRevise: false, WhyNot: whyNot);
    }

    // ---- Authoring rules -----------------------------------------------------------------------------------

    /// <summary>
    /// The classes of <paramref name="classNames"/> the author teaches in <paramref name="subjectId"/>, through live
    /// subject-teacher assignments. FAIL CLOSED: nobody plans for a class they do not teach. A lesson duty's own teacher
    /// is also allowed for that lesson's class (a cover), which the caller checks separately.
    /// </summary>
    public static async Task<List<string>> TaughtClassesAsync(QMgrDbContext db, Guid organizationId, Guid branchId, Guid userId, Guid subjectId, CancellationToken ct = default)
    {
        return await db.ClassTeacherAssignments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.BranchId == branchId && a.UserId == userId && a.EndedAt == null
                        && a.Role == QMgr.Domain.Enums.ClassTeacherRole.SubjectTeacher && a.SubjectId == subjectId)
            .Select(a => a.ClassName).ToListAsync(ct);
    }

    public static bool Covers(IEnumerable<string> taught, IEnumerable<string> wanted)
    {
        var keys = taught.Select(ClassName.Key).ToHashSet();
        return wanted.All(w => keys.Contains(ClassName.Key(w)));
    }

    /// <summary>"With Grace Nansubuga (head of Sciences)" — who has the plan now, in words.</summary>
    public static string? WaitingOn(TeachingPlan p, Chain chain, StaffPerformanceMapping.NameLookup names, int approverCount)
    {
        switch (p.Status)
        {
            case TeachingPlanStatus.Submitted:
                var who = chain.Stage1For(p.AuthorUserId);
                return who.Count == 0 ? "With the head of department" : $"With {string.Join(" or ", who.Select(id => names[id]).Where(n => n.Length > 0))}{(chain.DepartmentName is { } d ? $" ({d})" : "")}";
            case TeachingPlanStatus.Forwarded:
                return approverCount == 0
                    ? "Waiting: nobody else holds the approval permission. An administrator can give it to another person."
                    : "With the Director of Studies";
            case TeachingPlanStatus.Returned: return "With the author, returned for changes";
            case TeachingPlanStatus.Draft: return "Draft";
            default: return null;
        }
    }
}
