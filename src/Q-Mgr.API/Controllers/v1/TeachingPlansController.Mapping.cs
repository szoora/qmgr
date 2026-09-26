using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers.v1;

public partial class TeachingPlansController
{
    // ---- Mapping --------------------------------------------------------------------------------------------

    private async Task<TeachingPlanDto> MapAsync(PlanContext ctx)
    {
        var p = ctx.Plan;
        var trail = TeachingPlans.TrailOf(p);
        var names = await StaffLookups.LoadNamesAsync(Db,
            new Guid?[] { p.AuthorUserId, p.ForwardedByUserId, p.ApprovedByUserId, p.ReturnedByUserId }
                .Concat(trail.Select(t => t.ByUserId)).Concat(ctx.Chain.Stage1For(p.AuthorUserId).Select(g => (Guid?)g)));
        var policy = await _policy.GetAsync(p.OrganizationId);
        var approvers = p.Status == TeachingPlanStatus.Forwarded ? (await TeachingPlans.ApproversAsync(Db, p, SeesStaffAsync)).Count : 0;
        foreach (var t in trail) t.ByName = t.ByUserId is { } by ? names[by] : null;
        // The returned snapshot is the author's own earlier text: the reviewers and the author may read it, nobody else reaches here.

        return new TeachingPlanDto
        {
            Id = p.Id,
            BranchId = p.BranchId,
            Kind = p.Kind,
            AuthorUserId = p.AuthorUserId,
            AuthorName = names[p.AuthorUserId],
            SubjectId = p.SubjectId,
            SubjectName = ctx.Chain.SubjectName,
            DepartmentId = ctx.Chain.DepartmentId,
            DepartmentName = ctx.Chain.DepartmentName,
            ClassNames = p.ClassNames.ToList(),
            PeriodKey = p.PeriodKey,
            PeriodName = _policy.FindPeriod(policy, p.PeriodKey)?.Name,
            LessonDate = p.LessonDate,
            DutyId = p.DutyId,
            SchemeId = p.SchemeId,
            SchemeRowKey = p.SchemeRowKey,
            Title = p.Title,
            Content = p.Kind == TeachingPlanKind.LessonPlan ? TeachingPlans.ContentOf(p) : null,
            Rows = p.Kind == TeachingPlanKind.SchemeOfWork ? TeachingPlans.RowsOf(p) : null,
            Status = p.Status,
            Stages = p.Stages,
            SubmittedAt = p.SubmittedAt,
            ForwardedByName = p.ForwardedByUserId is { } f ? names[f] : null,
            ForwardedAt = p.ForwardedAt,
            StageSkippedReason = p.StageSkippedReason,
            ApprovedByName = p.ApprovedByUserId is { } a ? names[a] : null,
            ApprovedAt = p.ApprovedAt,
            ReturnReason = p.ReturnReason,
            Reflection = p.Reflection,
            Trail = trail,
            FileUrl = p.FileUrl == null ? null : UploadLinks.Sign(p.FileUrl),
            FileName = p.FileName,
            FileSizeBytes = p.FileSizeBytes,
            OriginalSizeBytes = p.OriginalSizeBytes,
            FilePages = p.FilePages,
            Version = p.Version,
            SupersedesId = p.SupersedesId,
            IsCurrent = p.IsCurrent,
            WaitingOn = TeachingPlans.WaitingOn(p, ctx.Chain, names, approvers),
            RowVersion = p.RowVersion,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            CanIEdit = ctx.Access.CanEdit,
            CanISubmit = ctx.Access.CanSubmit,
            CanIWithdraw = ctx.Access.CanWithdraw,
            CanIForward = ctx.Access.CanForward,
            CanIApprove = ctx.Access.CanApprove,
            CanIReturn = ctx.Access.CanReturn,
            CanIComment = ctx.Access.CanComment,
            CanIReflect = ctx.Access.CanReflect,
            CanIRevise = ctx.Access.CanRevise,
            WhyNot = ctx.Access.WhyNot,
            Sections = TeachingPlanDefaults.SectionsOf(ctx.Settings).ToList(),
            Phases = TeachingPlanDefaults.PhasesOf(ctx.Settings).ToList(),
            Columns = TeachingPlanDefaults.ColumnsOf(ctx.Settings).ToList(),
            UploadsEnabled = ctx.Settings.UploadsEnabled,
            FileTargetKb = p.Kind == TeachingPlanKind.SchemeOfWork ? ctx.Settings.SchemeTargetKb : ctx.Settings.LessonPlanTargetKb,
            FileCapKb = p.Kind == TeachingPlanKind.SchemeOfWork ? ctx.Settings.SchemeCapKb : ctx.Settings.LessonPlanCapKb,
            FileMaxPages = p.Kind == TeachingPlanKind.SchemeOfWork ? ctx.Settings.SchemeMaxPages : ctx.Settings.LessonPlanMaxPages
        };
    }

    // ---- Pre-filling a new plan ----------------------------------------------------------------------------

    /// <summary>"2026-T3" → 3. A school's own period keys may carry no term number, and then none is assumed.</summary>
    internal static int? TermOf(string periodKey)
        => Regex.Match(periodKey ?? string.Empty, @"T(\d)\b") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    private async Task<LessonPlanContentDto> PrefillLessonAsync(TeachingPlan plan, StaffDuty? duty, PerformancePeriodDto period,
        TeachingPlanSettingsDto settings, TimeZoneInfo zone, CreateTeachingPlanRequest request)
    {
        var me = CurrentUserId();
        var chain = await TeachingPlans.ChainAsync(Db, plan.OrganizationId, plan.SubjectId);
        var names = await StaffLookups.LoadNamesAsync(Db, new Guid?[] { me });
        var school = await Db.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == plan.OrganizationId).Select(o => o.Name).FirstOrDefaultAsync();
        var date = plan.LessonDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var week = Math.Max(1, (date.DayNumber - period.Start.DayNumber) / 7 + 1);

        var header = new PlanHeaderDto
        {
            SchoolName = school,
            TeacherName = names[me],
            SubjectName = chain.SubjectName,
            ClassText = string.Join(", ", plan.ClassNames),
            TermName = period.Name,
            Week = week,
            Learners = await LearnersAsync(plan.BranchId, plan.ClassNames)
        };
        if (duty != null)
        {
            var start = TimeZoneInfo.ConvertTimeFromUtc(duty.StartsAt, zone);
            var end = TimeZoneInfo.ConvertTimeFromUtc(duty.EndsAt, zone);
            header = header with
            {
                Time = string.Create(CultureInfo.InvariantCulture, $"{start:HH:mm}–{end:HH:mm}"),
                DurationMinutes = (int)Math.Round((duty.EndsAt - duty.StartsAt).TotalMinutes),
                Room = duty.Room ?? duty.Location
            };
        }

        var content = new LessonPlanContentDto { Header = header };

        // Copy forward: the author's last plan for these classes, or one they chose and may read.
        TeachingPlan? source = null;
        if (request.CopyFromId is { } fromId)
        {
            var candidate = await Db.TeachingPlans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fromId && x.OrganizationId == plan.OrganizationId && x.Kind == TeachingPlanKind.LessonPlan);
            if (candidate != null)
            {
                var chainOf = await TeachingPlans.ChainAsync(Db, candidate.OrganizationId, candidate.SubjectId);
                var settingsOf = settings;
                if ((await TeachingPlans.AccessForAsync(me, candidate, chainOf, settingsOf, HasPermissionAsync,
                        () => StaffScope.CanSeeStaffAsync(candidate.BranchId, candidate.AuthorUserId), () => Task.FromResult(false))).CanRead)
                    source = candidate;
            }
        }
        else if (request.CopyFromLast)
        {
            source = await Db.TeachingPlans.AsNoTracking()
                .Where(x => x.AuthorUserId == me && x.Kind == TeachingPlanKind.LessonPlan && x.SubjectId == plan.SubjectId && x.ClassKey == plan.ClassKey
                            && x.Status != TeachingPlanStatus.Withdrawn)
                .OrderByDescending(x => x.LessonDate).ThenByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        }
        if (source != null)
        {
            var copied = TeachingPlans.ContentOf(source);
            content.Answers = copied.Answers.Where(kv => kv.Key != TeachingPlanDefaults.ReflectionKey).ToDictionary(kv => kv.Key, kv => kv.Value);
            content.Procedure = copied.Procedure;
        }

        // This week's line of the scheme: the approved one first, else the author's own current scheme in any state.
        var scheme = await SchemeForAsync(plan);
        if (scheme != null)
        {
            var rows = TeachingPlans.RowsOf(scheme);
            var row = request.SchemeRowKey is { } key ? rows.FirstOrDefault(r => r.Key == key)
                : rows.Where(r => r.Week <= week).OrderByDescending(r => r.Week).FirstOrDefault() ?? rows.FirstOrDefault();
            if (row != null)
            {
                plan.SchemeId = scheme.Id;
                plan.SchemeRowKey = row.Key;
                foreach (var k in new[] { TeachingPlanDefaults.TopicKey, TeachingPlanDefaults.SubTopicKey, TeachingPlanDefaults.CompetencyKey, TeachingPlanDefaults.OutcomesKey })
                    if (row.Cells.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                        content.Answers[k] = v;
            }
        }

        if (content.Procedure.Count == 0)
            content.Procedure = TeachingPlanDefaults.PhasesOf(settings).Select(ph => new ProcedureStepDto { Phase = ph }).ToList();
        return content;
    }

    private async Task<TeachingPlan?> SchemeForAsync(TeachingPlan plan)
    {
        var keys = plan.ClassNames.Select(ClassName.Key).ToList();
        var candidates = await Db.TeachingPlans.AsNoTracking()
            .Where(s => s.BranchId == plan.BranchId && s.Kind == TeachingPlanKind.SchemeOfWork && s.SubjectId == plan.SubjectId && s.PeriodKey == plan.PeriodKey
                        && s.IsCurrent && s.Status != TeachingPlanStatus.Withdrawn && (s.Status == TeachingPlanStatus.Approved || s.AuthorUserId == plan.AuthorUserId))
            .ToListAsync();
        return candidates
            .Where(s => s.ClassKey.Split('|').Intersect(keys).Any())
            .OrderByDescending(s => s.Status == TeachingPlanStatus.Approved).ThenByDescending(s => s.AuthorUserId == plan.AuthorUserId).ThenByDescending(s => s.UpdatedAt ?? s.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>A new scheme's weeks, from the curriculum list: one line per topic, weeks allotted by its periods and the
    /// teacher's periods a week for the class. Empty without a curriculum list — the teacher adds weeks.</summary>
    private async Task<List<SchemeRowDto>> PrefillSchemeAsync(TeachingPlan plan, PerformancePeriodDto period, TeachingPlanSettingsDto settings)
    {
        var curriculum = await CurriculumStore.ReadAsync(Db, plan.OrganizationId);
        var topics = CurriculumStore.TopicsFor(curriculum, plan.SubjectId, plan.ClassNames, TermOf(period.Key));
        var perWeek = await Db.ClassTeacherAssignments.AsNoTracking()
            .Where(a => a.BranchId == plan.BranchId && a.UserId == plan.AuthorUserId && a.SubjectId == plan.SubjectId && a.EndedAt == null)
            .Select(a => a.PeriodsPerWeek).MaxAsync(x => (int?)x) ?? 4;
        if (perWeek <= 0) perWeek = 4;
        var weeks = Math.Max(1, (period.End.DayNumber - period.Start.DayNumber + 1) / 7);

        var rows = new List<SchemeRowDto>();
        var week = 1;
        foreach (var t in topics)
        {
            if (week > weeks) break;
            var cells = new Dictionary<string, string>();
            void Put(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) cells[key] = value.Trim(); }
            Put(TeachingPlanDefaults.TopicKey, t.Topic);
            Put(TeachingPlanDefaults.SubTopicKey, t.SubTopics);
            Put(TeachingPlanDefaults.CompetencyKey, t.Competency);
            Put(TeachingPlanDefaults.OutcomesKey, t.Outcomes);
            Put("methods", t.Activities);
            Put("assessment", t.Assessment);
            rows.Add(new SchemeRowDto { Key = Guid.NewGuid().ToString("N")[..12], Week = week, Periods = t.Periods, Cells = cells });
            week += Math.Max(1, (int)Math.Ceiling((t.Periods ?? perWeek) / (double)perWeek));
        }
        return rows;
    }
}
