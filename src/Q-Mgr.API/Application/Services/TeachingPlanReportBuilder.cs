using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// The planning figures (lesson plans plan §5.8), ONE builder for the Plans report page, its print and the Monday
/// digest, the <c>TeachingReportBuilder</c> rule.
///
/// <list type="bullet">
/// <item><b>Coverage</b> is lessons TAUGHT (the lesson flags say so) that had an approved plan. A lesson with no flag
/// is not counted either way — "unrecorded" is time passing, not a mark.</item>
/// <item><b>Review quality</b> is the share of decided plans whose trail carries a reviewer's comment or a decision note,
/// and the median hours from submission to the first decision. The Ugandan evidence is that plans are reviewed late and
/// rarely commented on (plan §2); these two numbers are that finding, measured.</item>
/// <item>Scoped: a caller sees authors inside their staff scope only (<paramref name="visible"/> null = everybody).</item>
/// </list>
/// </summary>
public static class TeachingPlanReportBuilder
{
    public static async Task<TeachingPlanReportDto> BuildAsync(QMgrDbContext db, Guid organizationId, Guid branchId, DateOnly from, DateOnly to,
        TimeZoneInfo zone, StaffPerformancePolicyDto policy, IStaffPerformancePolicyService policyService, HashSet<Guid>? visible, CancellationToken ct = default)
    {
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var nowUtc = DateTime.UtcNow;

        var lessons = await db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.IsActive && d.RecoversDutyId == null && d.ExpectedUserIds != null
                        && d.StartsAt >= startUtc && d.StartsAt < endUtc && d.StartsAt < nowUtc)
            .ToListAsync(ct);
        if (visible != null) lessons = lessons.Where(l => l.ExpectedUserIds!.Any(visible.Contains)).ToList();
        var items = await StaffLessons.ToItemsAsync(db, lessons, Guid.Empty, _ => false, policy, zone, nowUtc, ct);
        var taughtIds = items.Where(i => i.Status is LessonStatus.Taught or LessonStatus.TaughtSelfReported or LessonStatus.Recovered).Select(i => i.DutyId).ToHashSet();
        var ids = lessons.Select(l => l.Id).ToList();
        var plans = await db.TeachingPlans.AsNoTracking()
            .Where(p => p.DutyId != null && ids.Contains(p.DutyId.Value) && p.Status != TeachingPlanStatus.Withdrawn)
            .Select(p => new { DutyId = p.DutyId!.Value, p.Status }).ToListAsync(ct);
        var approved = plans.Where(p => p.Status == TeachingPlanStatus.Approved).Select(p => p.DutyId).ToHashSet();
        var any = plans.Where(p => p.Status is TeachingPlanStatus.Submitted or TeachingPlanStatus.Forwarded or TeachingPlanStatus.Approved).Select(p => p.DutyId).ToHashSet();

        var subjects = await db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == organizationId)
            .Select(s => new { s.Id, s.Name, s.DepartmentId }).ToListAsync(ct);
        var departments = await StaffLookups.LoadDepartmentNamesAsync(db, organizationId, ct);
        string SubjectName(Guid? id) => subjects.FirstOrDefault(s => s.Id == id)?.Name ?? "No subject";
        string DepartmentName(Guid? subjectId)
            => subjects.FirstOrDefault(s => s.Id == subjectId)?.DepartmentId is { } d && departments.TryGetValue(d, out var n) ? n : "No department";

        List<PlanCoverageRowDto> Group(Func<StaffDuty, string> key) => lessons
            .Where(l => taughtIds.Contains(l.Id))
            .GroupBy(key)
            .Select(g => new PlanCoverageRowDto
            {
                Group = g.Key,
                LessonsTaught = g.Count(),
                WithApprovedPlan = g.Count(l => approved.Contains(l.Id)),
                WithAnyPlan = g.Count(l => any.Contains(l.Id)),
                CoveragePercent = g.Any() ? Math.Round(100.0 * g.Count(l => approved.Contains(l.Id)) / g.Count(), 1) : null
            })
            .OrderBy(r => r.Group, Comparer<string>.Create((a, b) => QMgr.Application.NaturalOrder.Compare(a, b))).ToList();

        var report = new TeachingPlanReportDto
        {
            From = from,
            To = to,
            ByDepartment = Group(l => DepartmentName(l.SubjectId)),
            BySubject = Group(l => SubjectName(l.SubjectId)),
        };

        // Review quality: plans first submitted in the window.
        var submitted = await db.TeachingPlans.AsNoTracking()
            .Where(p => p.BranchId == branchId && p.SubmittedAt != null && p.SubmittedAt >= startUtc && p.SubmittedAt < endUtc && p.Status != TeachingPlanStatus.Withdrawn)
            .ToListAsync(ct);
        if (visible != null) submitted = submitted.Where(p => visible.Contains(p.AuthorUserId)).ToList();
        var hours = new List<double>();
        foreach (var p in submitted)
        {
            var trail = TeachingPlans.TrailOf(p);
            var decision = trail.Where(t => t.Kind is PlanTrailKind.Forwarded or PlanTrailKind.Approved or PlanTrailKind.Returned && t.ByUserId != p.AuthorUserId)
                .OrderBy(t => t.At).FirstOrDefault();
            if (decision == null)
            {
                if (p.Status is TeachingPlanStatus.Submitted or TeachingPlanStatus.Forwarded)
                {
                    report.Review.Waiting++;
                    if (p.WaitingSince is { } since && nowUtc - since > TimeSpan.FromDays(2)) report.Review.WaitingOverTwoDays++;
                }
                continue;
            }
            report.Review.Reviewed++;
            if (trail.Any(t => t.ByUserId != p.AuthorUserId && !string.IsNullOrWhiteSpace(t.Body)
                               && t.Kind is PlanTrailKind.Comment or PlanTrailKind.Forwarded or PlanTrailKind.Approved or PlanTrailKind.Returned))
                report.Review.ReviewedWithComment++;
            if (trail.Any(t => t.Kind == PlanTrailKind.Returned)) report.Review.Returned++;
            var firstSubmit = trail.Where(t => t.Kind == PlanTrailKind.Submitted).Select(t => (DateTime?)t.At).Min() ?? p.SubmittedAt!.Value;
            hours.Add(Math.Max(0, (decision.At - firstSubmit).TotalHours));
        }
        if (hours.Count > 0)
        {
            hours.Sort();
            var mid = hours.Count / 2;
            report.Review.MedianHoursToDecision = Math.Round(hours.Count % 2 == 1 ? hours[mid] : (hours[mid - 1] + hours[mid]) / 2, 1);
        }

        // ---- Schemes of work, teacher by teacher (2026-09-26) ----
        // One scheme is expected per teacher per subject they are assigned to teach this term. Each row says where that
        // scheme stands, so the Director of Studies can see who has submitted, who is pending and who is approved — not a
        // total. A teacher's CURRENT scheme counts; while a revision of an approved one is open, the approved one does.
        var period = policyService.PeriodFor(policy, to);
        var settings = policyService.PlanSettings(policy);
        var dueBy = period.Start.AddDays(7 * settings.SchemeDueWeek - 1);
        var assignments = await db.ClassTeacherAssignments.AsNoTracking()
            .Where(a => a.BranchId == branchId && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher && a.SubjectId != null)
            .Select(a => new { a.UserId, SubjectId = a.SubjectId!.Value, a.ClassName }).ToListAsync(ct);
        if (visible != null) assignments = assignments.Where(a => visible.Contains(a.UserId)).ToList();
        var schemes = await db.TeachingPlans.AsNoTracking()
            .Where(p => p.BranchId == branchId && p.Kind == TeachingPlanKind.SchemeOfWork && p.PeriodKey == period.Key && p.IsCurrent && p.Status != TeachingPlanStatus.Withdrawn)
            .Select(p => new { p.Id, p.AuthorUserId, p.SubjectId, p.Status, p.SubmittedAt, p.ApprovedAt, p.ApprovedByUserId, p.TrailJson }).ToListAsync(ct);
        if (visible != null) schemes = schemes.Where(s => visible.Contains(s.AuthorUserId)).ToList();

        var expected = assignments.GroupBy(a => (a.UserId, a.SubjectId)).ToList();
        var peopleIds = expected.Select(g => (Guid?)g.Key.UserId).Concat(schemes.Select(s => s.ApprovedByUserId)).Distinct();
        var names = await StaffLookups.LoadNamesAsync(db, peopleIds, ct);
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var dueUtc = TimeZoneInfo.ConvertTimeToUtc(dueBy.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var summary = new SchemeSummaryDto { PeriodName = period.Name, DueBy = dueBy, Expected = expected.Count };
        foreach (var g in expected)
        {
            var s = schemes.Where(x => x.AuthorUserId == g.Key.UserId && x.SubjectId == g.Key.SubjectId)
                .OrderByDescending(x => x.Status == TeachingPlanStatus.Approved).ThenByDescending(x => x.SubmittedAt).FirstOrDefault();
            var progress = s == null ? SchemeProgress.NotStarted : s.Status switch
            {
                TeachingPlanStatus.Approved => SchemeProgress.Approved,
                TeachingPlanStatus.Forwarded => SchemeProgress.WithApprover,
                TeachingPlanStatus.Submitted => SchemeProgress.WithHeadOfDepartment,
                TeachingPlanStatus.Returned => SchemeProgress.Returned,
                _ => SchemeProgress.Draft
            };
            // First submission, from the trail: a return and resubmission does not make an on-time scheme late.
            DateTime? firstSubmitted = null;
            if (s != null)
            {
                var trail = TeachingPlans.TrailOf(new TeachingPlan { TrailJson = s.TrailJson });
                firstSubmitted = trail.Where(t => t.Kind == PlanTrailKind.Submitted).Select(t => (DateTime?)t.At).Min() ?? s.SubmittedAt;
            }
            var late = firstSubmitted is { } fs ? fs > dueUtc : todayLocal > dueBy;
            summary.Rows.Add(new SchemeStatusRowDto
            {
                TeacherUserId = g.Key.UserId,
                TeacherName = names[g.Key.UserId] is { Length: > 0 } n ? n : "A former member of staff",
                SubjectId = g.Key.SubjectId,
                SubjectName = SubjectName(g.Key.SubjectId),
                DepartmentName = DepartmentName(g.Key.SubjectId),
                ClassNames = g.Select(a => a.ClassName).Distinct().OrderBy(c => c, Comparer<string>.Create((a, b) => QMgr.Application.NaturalOrder.Compare(a, b))).ToList(),
                Progress = progress,
                PlanId = s?.Id,
                SubmittedAt = firstSubmitted,
                ApprovedAt = s?.ApprovedAt,
                ApprovedByName = s?.ApprovedByUserId is { } ab ? names[ab] : null,
                Late = late
            });
        }
        summary.Rows = summary.Rows.OrderBy(r => r.Progress == SchemeProgress.Approved).ThenBy(r => r.Progress).ThenBy(r => r.DepartmentName).ThenBy(r => r.TeacherName).ToList();
        summary.NotStarted = summary.Rows.Count(r => r.Progress == SchemeProgress.NotStarted);
        summary.Draft = summary.Rows.Count(r => r.Progress == SchemeProgress.Draft);
        summary.WithHeadOfDepartment = summary.Rows.Count(r => r.Progress == SchemeProgress.WithHeadOfDepartment);
        summary.WithApprover = summary.Rows.Count(r => r.Progress == SchemeProgress.WithApprover);
        summary.Returned = summary.Rows.Count(r => r.Progress == SchemeProgress.Returned);
        summary.Approved = summary.Rows.Count(r => r.Progress == SchemeProgress.Approved);
        summary.Late = summary.Rows.Count(r => r.Late);
        report.Schemes = summary;
        report.SchemesExpected = summary.Expected;
        report.SchemesSubmitted = summary.WithHeadOfDepartment + summary.WithApprover + summary.Approved;
        report.SchemesApproved = summary.Approved;

        // ---- Lesson plans, teacher by teacher, over the window ----
        var planByDuty = await db.TeachingPlans.AsNoTracking()
            .Where(p => p.DutyId != null && ids.Contains(p.DutyId.Value) && p.Status != TeachingPlanStatus.Withdrawn)
            .Select(p => new { DutyId = p.DutyId!.Value, p.Status }).ToListAsync(ct);
        var teacherNames = await StaffLookups.LoadNamesAsync(db, lessons.Select(l => l.ExpectedUserIds!.FirstOrDefault()).Select(x => (Guid?)x), ct);
        report.ByTeacher = lessons.Where(l => taughtIds.Contains(l.Id))
            .GroupBy(l => l.ExpectedUserIds!.FirstOrDefault())
            .Where(g => g.Key != Guid.Empty)
            .Select(g =>
            {
                var statuses = g.Select(l => planByDuty.Where(p => p.DutyId == l.Id).Select(p => (TeachingPlanStatus?)p.Status).FirstOrDefault()).ToList();
                var withApproved = statuses.Count(s => s == TeachingPlanStatus.Approved);
                return new TeacherPlanRowDto
                {
                    TeacherUserId = g.Key,
                    TeacherName = teacherNames[g.Key] is { Length: > 0 } n ? n : "A former member of staff",
                    DepartmentName = g.Select(l => DepartmentName(l.SubjectId)).GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key,
                    LessonsTaught = g.Count(),
                    WithApprovedPlan = withApproved,
                    Submitted = statuses.Count(s => s is TeachingPlanStatus.Submitted or TeachingPlanStatus.Forwarded),
                    Returned = statuses.Count(s => s == TeachingPlanStatus.Returned),
                    NoPlan = statuses.Count(s => s == null || s == TeachingPlanStatus.Draft),
                    CoveragePercent = Math.Round(100.0 * withApproved / g.Count(), 1)
                };
            })
            .OrderBy(r => r.CoveragePercent).ThenBy(r => r.TeacherName).ToList();
        return report;
    }
}
