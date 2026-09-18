using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// "My Portal": the caller's own file and nothing else. Follows ProfileController — [Authorize]
/// plus the module gate, NO permission code, the user id from the JWT — because a teacher or a
/// front-office employee with no staff.* permission at all must still see what has been recorded
/// about them, respond to it, and take their file with them. That is the s.24 subject-access right
/// the plan is built around, and it is why every seeded role reaches these routes.
///
/// The rung rule for self is fixed: Standard and Confidential, never Restricted, never somebody
/// else's draft. The branch is the caller's assigned branch, or the organization's first active
/// branch for a person assigned to none.
/// </summary>
[ApiController]
[Route("api/v1/staff/portal")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffPortalController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly IStaffScoringService _scoring;
    private readonly ILogger<StaffPortalController> _logger;
    private const int MaxPageSize = 200;

    public StaffPortalController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        IStaffScoringService scoring,
        IStaffOnboardingPolicyService onboarding,
        ILogger<StaffPortalController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _logger = logger;
        _policy = policy;
        _scoring = scoring;
        _onboarding = onboarding;
    }

    private readonly IStaffOnboardingPolicyService _onboarding;

    /// <summary>
    /// The first-sign-in checklist (plan §12.3), derived from the account itself: a confirmed phone, a
    /// preferences blob (null until the person has saved their choices once), a photo, and an
    /// acknowledgement of the tenant's acceptable-use notice when one is set. Nothing is ticked by hand.
    /// </summary>
    private async Task<OnboardingChecklistDto> BuildOnboardingChecklistAsync(QMgr.Domain.Entities.Identity.User me, Guid organizationId)
    {
        var onboarding = await _onboarding.GetAsync(organizationId);
        Guid? noticeId = null;
        var acknowledged = false;
        if (onboarding.AcceptableUseNoticeId is { } id)
        {
            var acks = await Db.StaffNotices.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.Id == id && n.OrganizationId == organizationId && n.IsActive)
                .Select(n => n.Acknowledgements).FirstOrDefaultAsync();
            if (acks != null)
            {
                noticeId = id;
                try { acknowledged = (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(acks) ?? new()).ContainsKey(me.Id.ToString()); }
                catch (System.Text.Json.JsonException) { }
            }
        }
        return new OnboardingChecklistDto
        {
            PhoneConfirmed = me.PhoneVerifiedAt != null,
            NotificationPreferencesReviewed = !string.IsNullOrWhiteSpace(me.NotificationPreferences),
            ProfilePhotoAdded = !string.IsNullOrWhiteSpace(me.PhotoUrl),
            AcceptableUseNoticeId = noticeId,
            AcceptableUseAcknowledged = acknowledged
        };
    }

    // ---------------------------------------------------------------------
    // The hub
    // ---------------------------------------------------------------------

    [HttpGet]
    [ProducesResponseType(typeof(StaffPortalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPortal()
    {
        var self = await ResolveSelfAsync();
        if (self.Error != null) return self.Error;
        var (me, organizationId, branchId) = (self.User!, self.OrganizationId, self.BranchId);
        var now = DateTime.UtcNow;

        var policy = await _policy.GetAsync(organizationId);
        var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(now));
        var score = await _scoring.ComputeAsync(organizationId, branchId, me.Id, period, includeRank: true);
        var mayManageDuties = await HasPermissionAsync(Permissions.StaffDutiesManage);
        var myDepartments = me.DepartmentIds ?? Array.Empty<Guid>();
        var myRole = me.Role?.Code ?? string.Empty;
        var myGroup = _policy.GroupFor(myRole);

        // ---- Coming up: duties I am expected at or record, not yet over ----
        // Rota slots have their own card and lessons have My Day, so Coming up is sessions only.
        var upcoming = await Db.StaffDuties.AsNoTracking().Include(d => d.Parameter)
            .Where(d => d.BranchId == branchId && d.IsActive && d.EndsAt >= now && d.Kind == DutyKind.Session
                        && (d.ExpectedUserIds == null || d.ExpectedUserIds.Contains(me.Id) || d.RecorderUserIds.Contains(me.Id)))
            .OrderBy(d => d.StartsAt)
            .Take(10)
            .ToListAsync();
        var comingUp = await MapDutiesAsync(upcoming, me.Id, organizationId, branchId, mayManageDuties);

        // ---- On duty (plan §4.2, §10): rota slots I am on or supervise, now or within two weeks ----
        var rotaHorizon = now.AddDays(14);
        var rotaSlots = await Db.StaffDuties.AsNoTracking().Include(d => d.Parameter)
            .Where(d => d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Rota && d.EndsAt >= now && d.StartsAt <= rotaHorizon
                        && ((d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me.Id)) || d.SupervisorUserIds.Contains(me.Id)))
            .OrderBy(d => d.StartsAt)
            .Take(8)
            .ToListAsync();
        var onDuty = await MapDutiesAsync(rotaSlots, me.Id, organizationId, branchId, mayManageDuties);

        // ---- Reports to write (plan §4.3): my started, unwritten duty reports on slots under way or recently over ----
        var myRotaSlots = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Rota && d.ReportCadence != ReportCadence.None
                        && d.StartsAt <= now && d.EndsAt >= now.AddDays(-31)
                        && ((d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me.Id)) || d.SupervisorUserIds.Contains(me.Id)))
            .ToListAsync();
        if (myRotaSlots.Count > 0)
        {
            var zone = AppointmentScheduling.ResolveTimeZone(await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());
            foreach (var slot in myRotaSlots) await StaffDutyReports.EnsureRowsAsync(Db, slot, policy, zone, now, _logger);
        }
        var toWrite = await Db.StaffDutyReports.AsNoTracking().Include(r => r.Duty)
            .Where(r => r.BranchId == branchId && r.AuthorUserId == me.Id && r.Duty!.IsActive
                        && (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned) && r.DueAt >= now.AddDays(-31))
            .OrderBy(r => r.DueAt).Take(10).ToListAsync();
        var reportNames = await BuildNamesAsync(new Guid?[] { me.Id });
        var reportsToWrite = toWrite.Select(r => StaffDutyReports.ToSummary(r, r.Duty!, reportNames, 0, now)).ToList();

        // ---- Open items ----
        var openItems = new List<PortalItemDto>();

        var registersDue = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.IsActive && d.EndsAt < now && d.RegisterClosedAt == null && d.RecorderUserIds.Contains(me.Id) && d.Kind != DutyKind.Lesson)
            .OrderBy(d => d.EndsAt)
            .Take(10)
            .ToListAsync();
        // Saving a register records its marks at once and leaves it open, so "not closed" can mean
        // "nothing marked" or "half marked"; the line says which.
        var dueIds = registersDue.Select(d => d.Id).ToList();
        var markedByDuty = dueIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await Db.StaffPerformanceRecords.AsNoTracking()
                .Where(r => r.DutyId != null && dueIds.Contains(r.DutyId.Value) && r.Status == StaffRecordStatus.Final)
                .GroupBy(r => r.DutyId!.Value)
                .Select(g => new { g.Key, Count = g.Select(r => r.SubjectUserId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
        openItems.AddRange(registersDue.Select(d => new PortalItemDto
        {
            Kind = "register-due",
            Title = d.Title,
            Detail = markedByDuty.GetValueOrDefault(d.Id) is var marked && marked > 0
                ? $"You are the recorder. {marked} marked and recorded; close the register when everyone is marked."
                : "You are the recorder and the register has not been taken.",
            DueAt = d.EndsAt,
            IsOverdue = true,
            Url = $"/admin/staff/duties/{d.Id}/register"
        }));

        // Lessons still unrecorded (plan §7.3): ONE line with the count, linking to My Day — never a line per lesson.
        var lessonWindow = now.AddDays(-Math.Max(policy.UnrecordedLessonWindowDays, 1) - 7);
        var unrecordedLessons = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Lesson && d.EndsAt < now && d.StartsAt >= lessonWindow
                        && d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me.Id)
                        && !Db.StaffPerformanceRecords.Any(r => r.DutyId == d.Id && r.Status == StaffRecordStatus.Final))
            .Select(d => d.EndsAt).ToListAsync();
        if (unrecordedLessons.Count > 0)
            openItems.Add(new PortalItemDto
            {
                Kind = "lessons-unrecorded",
                Title = unrecordedLessons.Count == 1 ? "1 lesson of yours is unrecorded" : $"{unrecordedLessons.Count} lessons of yours are unrecorded",
                Detail = "Mark each one taught or not taught.",
                DueAt = unrecordedLessons.Min(),
                IsOverdue = unrecordedLessons.Min() < now.AddDays(-Math.Max(policy.UnrecordedLessonWindowDays, 1)),
                Url = "/my-day"
            });

        // A slot I supervise that has reached its final pre-duty stage with somebody still unacknowledged (plan §4.2:
        // the PagerDuty-style escalation lands on the supervisor's to-do, by name, behind the login).
        foreach (var slot in onDuty.Where(d => d.IsSupervisedByMe && d.StartsAt > now && d.StartsAt <= now.AddHours(24)))
        {
            var unacked = (slot.ExpectedUserIds ?? new List<Guid>()).Where(id => slot.Acknowledgements == null || !slot.Acknowledgements.ContainsKey(id)).ToList();
            if (unacked.Count == 0) continue;
            var who = await BuildNamesAsync(unacked.Select(id => (Guid?)id));
            openItems.Add(new PortalItemDto
            {
                Kind = "rota-unacknowledged",
                Title = $"{string.Join(", ", unacked.Select(id => who[id]))} {(unacked.Count == 1 ? "has" : "have")} not acknowledged {slot.Title}",
                Detail = "You are the administrator on duty for this slot.",
                DueAt = slot.StartsAt,
                IsOverdue = slot.StartsAt <= now.AddHours(2),
                Url = "/admin/staff/duties?tab=rota"
            });
        }

        // Plan §4.4 stage 3: a report a day overdue on a slot I supervise is a line on my to-do (names behind the login).
        var supervisedOverdue = await Db.StaffDutyReports.AsNoTracking().Include(r => r.Duty)
            .Where(r => r.BranchId == branchId && r.Duty!.IsActive && r.Duty.SupervisorUserIds.Contains(me.Id) && r.AuthorUserId != me.Id
                        && (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned) && r.DueAt <= now.AddHours(-24) && r.DueAt >= now.AddDays(-31))
            .OrderBy(r => r.DueAt).Take(10).ToListAsync();
        if (supervisedOverdue.Count > 0)
        {
            var authors = await BuildNamesAsync(supervisedOverdue.Select(r => (Guid?)r.AuthorUserId));
            openItems.AddRange(supervisedOverdue.Select(r => new PortalItemDto
            {
                Kind = "duty-report-overdue",
                Title = $"{authors[r.AuthorUserId]}'s duty report is overdue: {r.Duty!.Title}",
                Detail = $"For {StaffDutyReports.PeriodText(r.PeriodStart, r.PeriodEnd)}. You are the administrator on duty.",
                DueAt = r.DueAt,
                IsOverdue = true,
                Url = "/admin/staff/duties?tab=reports"
            }));
        }

        var appraisal = await Db.StaffAppraisals.AsNoTracking().Include(a => a.Subject).Include(a => a.Appraiser)
            .FirstOrDefaultAsync(a => a.SubjectUserId == me.Id && a.PeriodKey == period.Key);
        if (appraisal != null)
        {
            if (appraisal.Stage is AppraisalStage.Open or AppraisalStage.SelfAssessment)
                openItems.Add(new PortalItemDto
                {
                    Kind = "appraisal",
                    Title = $"Self-assessment for {period.Name}",
                    Detail = appraisal.Stage == AppraisalStage.Open ? "Your targets are set; your self-assessment is next." : "Your self-assessment is waiting for you.",
                    DueAt = appraisal.PeriodEnd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    IsOverdue = appraisal.PeriodEnd < DateOnly.FromDateTime(now),
                    Url = $"/portal/appraisals/{appraisal.Id}"
                });
            else if (appraisal.Stage == AppraisalStage.Signed && appraisal.SignedAt is { } signed && signed > now.AddDays(-14))
                openItems.Add(new PortalItemDto
                {
                    Kind = "appraisal-signed",
                    Title = $"Your {period.Name} appraisal has been signed",
                    Detail = "Read it. You may appeal within the period.",
                    DueAt = signed,
                    Url = $"/portal/appraisals/{appraisal.Id}"
                });
        }

        // ---- Notices for my audience ----
        var notices = await NoticesForMeQuery(organizationId, branchId, me.Id, myDepartments, myRole, myGroup, now)
            .OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.PublishAt)
            .Take(10)
            .ToListAsync();
        var noticeDtos = await MapNoticesAsync(notices, me.Id);
        openItems.AddRange(noticeDtos.Where(n => n.RequiresAcknowledgement && n.AcknowledgedByMeAt == null).Select(n => new PortalItemDto
        {
            Kind = "notice-ack",
            Title = n.Title,
            Detail = "This notice asks you to confirm you have read it.",
            DueAt = n.ExpiresAt,
            IsOverdue = false,
            Url = $"/portal/notices/{n.Id}"
        }));

        // ---- My records ----
        var recent = await MyRecordsQuery(me.Id, branchId)
            .OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.CreatedAt)
            .Take(20)
            .ToListAsync();
        var received = await MyRecordsQuery(me.Id, branchId)
            .Where(r => r.Source == RecordSource.Recognition && r.Status == StaffRecordStatus.Final)
            .OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync();
        var given = await RecordsWithIncludes()
            .Where(r => r.OrganizationId == organizationId && r.LoggedByUserId == me.Id && r.Source == RecordSource.Recognition && r.Status == StaffRecordStatus.Final)
            .OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync();

        var unacknowledged = await Db.StaffPerformanceRecords
            .CountAsync(r => r.SubjectUserId == me.Id && r.Status == StaffRecordStatus.Final && r.AcknowledgedAt == null && r.Visibility != WelfareVisibility.Restricted);
        // Unseen records: ONE to-do line, not one per record. Found in Chrome, 2026-09-16: a teacher
        // with eleven recognitions had a to-do list of eleven identical "Recognition logged about you"
        // rows burying the appraisal that actually needed them. A single record still links straight
        // to itself; several link to the timeline, which has "Mark all as seen".
        var unseen = recent
            .Where(r => r.Status == StaffRecordStatus.Final && r.AcknowledgedAt == null && r.LoggedByUserId != me.Id)
            .ToList();
        if (unacknowledged == 1 && unseen.Count == 1)
        {
            var one = unseen[0];
            openItems.Add(new PortalItemDto
            {
                Kind = "record-unread",
                Title = one.Visibility == WelfareVisibility.Confidential ? "A confidential record was logged about you" : $"{one.Parameter?.Name ?? "A record"} logged about you",
                Detail = one.Visibility == WelfareVisibility.Confidential ? "Open it to read and respond." : Truncate(one.Description, 120),
                DueAt = one.CreatedAt,
                Url = $"/portal/records/{one.Id}"
            });
        }
        else if (unacknowledged > 1)
        {
            var kinds = unseen.Select(x => x.Visibility == WelfareVisibility.Confidential ? "confidential record" : (x.Parameter?.Name ?? "record").ToLowerInvariant())
                .GroupBy(k => k).OrderByDescending(g => g.Count())
                .Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} × {g.Key}").Take(3);
            openItems.Add(new PortalItemDto
            {
                Kind = "record-unread",
                Title = $"{unacknowledged} records about you not yet seen",
                Detail = string.Join(", ", kinds) + (unacknowledged > unseen.Count ? ", and older ones" : ""),
                DueAt = unseen.Count > 0 ? unseen.Max(x => x.CreatedAt) : null,
                Url = "/portal#my-timeline"
            });
        }

        // ---- Welfare actions I owe (the same rows WelfareController's my-actions returns) ----
        var welfareActions = await Db.WelfareRecords.AsNoTracking().Include(w => w.Student).Include(w => w.Category)
            .Where(w => w.AssignedToUserId == me.Id && w.Status != WelfareStatus.Resolved && w.Status != WelfareStatus.Draft)
            .OrderBy(w => w.ActionDueDate ?? DateTime.MaxValue)
            .Take(10)
            .ToListAsync();
        openItems.AddRange(welfareActions.Select(w => new PortalItemDto
        {
            Kind = "welfare-action",
            Title = $"Welfare follow-up: {w.Student?.FullName ?? "a student"}",
            Detail = w.Category?.Name,
            DueAt = w.ActionDueDate,
            IsOverdue = w.ActionDueDate.HasValue && w.ActionDueDate.Value < now,
            Url = $"/admin/students/{w.StudentId}/welfare"
        }));

        // ---- Names, once ----
        var allRecords = recent.Concat(received).Concat(given).ToList();
        var names = await BuildNamesAsync(allRecords.Select(r => (Guid?)r.LoggedByUserId)
            .Concat(allRecords.Select(r => (Guid?)r.SubjectUserId))
            .Concat(allRecords.SelectMany(r => r.Notes).Select(n => (Guid?)n.AuthorUserId))
            .Concat(notices.Select(n => (Guid?)n.PublishedByUserId))
            .Append(me.LineManagerUserId)
            .Append(appraisal?.AppraiserUserId).Append(appraisal?.ModeratorUserId).Append(appraisal?.SignedByUserId));
        var departmentNames = await DepartmentNamesAsync(organizationId);
        var (departmentBoard, leaderboard) = await StaffReportBuilder.BuildPortalBoardsAsync(Db, _scoring, organizationId, branchId, period, policy);

        StaffAppraisalDto? appraisalDto = null;
        if (appraisal != null)
        {
            var band = appraisal.FinalRating is { } fr ? policy.Bands.FirstOrDefault(b => b.Rating == fr) : null;
            appraisalDto = StaffPerformanceMapping.ToDto(appraisal, names,
                string.Join(", ", myDepartments.Select(id => departmentNames.GetValueOrDefault(id)).Where(n => n != null)),
                band?.Name, score, me.Id,
                callerMayApprove: await HasPermissionAsync(Permissions.StaffAppraisalsApprove),
                callerMayConduct: await HasPermissionAsync(Permissions.StaffAppraisalsConduct));
        }

        return Ok(new StaffPortalDto
        {
            Me = StaffPerformanceMapping.ToDto(me, names, departmentNames, score, recent.FirstOrDefault(r => r.Status == StaffRecordStatus.Final)?.CreatedAt),
            BranchId = branchId,
            Score = score,
            ComingUp = comingUp,
            OnDuty = onDuty,
            ReportsToWrite = reportsToWrite,
            OpenItems = openItems.OrderBy(i => !i.IsOverdue).ThenBy(i => i.DueAt ?? DateTime.MaxValue).ToList(),
            Notices = noticeDtos,
            RecognitionReceived = received.Select(r => StaffPerformanceMapping.ToDto(r, names, policy.LateEntryThresholdDays, includeNotesAndAttachments: false)).ToList(),
            RecognitionGiven = given.Select(r => StaffPerformanceMapping.ToDto(r, names, policy.LateEntryThresholdDays, includeNotesAndAttachments: false)).ToList(),
            RecentRecords = recent.Select(r => StaffPerformanceMapping.ToDto(r, names, policy.LateEntryThresholdDays)).ToList(),
            Appraisal = appraisalDto,
            RecognitionBudget = await BuildRecognitionBudgetAsync(_policy, organizationId, me.Id),
            UnacknowledgedRecords = unacknowledged,
            LeaderboardMode = policy.LeaderboardMode,
            DepartmentBoard = departmentBoard,
            Leaderboard = leaderboard,
            Onboarding = await BuildOnboardingChecklistAsync(me, organizationId)
        });
    }

    // ---------------------------------------------------------------------
    // My records, my trail, my colleagues, my file
    // ---------------------------------------------------------------------

    [HttpGet("records")]
    [ProducesResponseType(typeof(StaffRecordSearchResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyRecords([FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var self = await ResolveSelfAsync();
        if (self.Error != null) return self.Error;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var policy = await _policy.GetAsync(self.OrganizationId);

        var query = MyRecordsQuery(self.User!.Id, self.BranchId);
        var total = await query.CountAsync();
        var records = await query.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();
        var names = await NamesForAsync(records);

        return Ok(new StaffRecordSearchResultDto
        {
            Items = records.Select(r => StaffPerformanceMapping.ToDto(r, names, policy.LateEntryThresholdDays)).ToList(),
            TotalCount = total
        });
    }

    /// <summary>The subject-access trail: everything anyone did on my file, including who viewed and who exported it.</summary>
    [HttpGet("activity")]
    [ProducesResponseType(typeof(ActivityLogPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyActivity([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var self = await ResolveSelfAsync();
        if (self.Error != null) return self.Error;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var policy = await _policy.GetAsync(self.OrganizationId);

        // Never Restricted on the subject's own trail: "Restricted record viewed" would tell them one exists.
        var query = Db.ActivityEvents.AsNoTracking().Where(e => e.OrganizationId == self.OrganizationId && e.SubjectUserId == self.User!.Id && e.Visibility != WelfareVisibility.Restricted);
        var total = await query.CountAsync();
        var counts = await query.GroupBy(e => e.Action).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var events = await query.OrderByDescending(e => e.OccurredAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var names = await BuildNamesAsync(events.Select(e => e.ActorUserId).Append(self.User!.Id));

        return Ok(new ActivityLogPageDto
        {
            Items = events.Select(e => StaffPerformanceMapping.ToDto(e, names)).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            AttributionRetentionDays = policy.ActivityAttributionRetentionDays,
            CountsByAction = counts
        });
    }

    /// <summary>Everyone I might recognise: the active staff of my organization, minus me and the platform SuperAdmin.</summary>
    [HttpGet("colleagues")]
    [ProducesResponseType(typeof(List<StaffColleagueDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetColleagues()
    {
        var self = await ResolveSelfAsync();
        if (self.Error != null) return self.Error;

        var colleagues = await Db.Users.AsNoTracking()
            .Where(u => u.OrganizationId == self.OrganizationId && u.IsActive && u.Id != self.User!.Id && u.Role.Code != RoleCodes.SuperAdmin)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username, u.JobTitle, u.DepartmentIds })
            .ToListAsync();
        var departmentNames = await DepartmentNamesAsync(self.OrganizationId);

        return Ok(colleagues.Select(c => new StaffColleagueDto
        {
            UserId = c.Id,
            FullName = $"{c.FirstName} {c.LastName}".Trim() is { Length: > 0 } n ? n : c.Username,
            JobTitle = c.JobTitle,
            DepartmentNames = c.DepartmentIds == null || c.DepartmentIds.Length == 0
                ? null
                : string.Join(", ", c.DepartmentIds.Select(id => departmentNames.GetValueOrDefault(id)).Where(x => x != null))
        }).ToList());
    }

    /// <summary>
    /// Everything the module holds about me, for the subject-access request: records with their
    /// notes and evidence (Standard and Confidential), my appraisals, my activity trail, the notices
    /// I acknowledged, and who to write to about it. The export itself is logged on the trail — the
    /// next export will show this one.
    /// </summary>
    [HttpGet("export")]
    [ProducesResponseType(typeof(StaffFileExportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportMyFile()
    {
        var self = await ResolveSelfAsync();
        if (self.Error != null) return self.Error;
        var (me, organizationId, branchId) = (self.User!, self.OrganizationId, self.BranchId);
        var policy = await _policy.GetAsync(organizationId);
        var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));

        var records = await RecordsWithIncludes()
            .Where(r => r.SubjectUserId == me.Id && r.Visibility != WelfareVisibility.Restricted && (r.Status != StaffRecordStatus.Draft || r.LoggedByUserId == me.Id))
            .OrderByDescending(r => r.OccurredAt)
            .ToListAsync();
        var appraisals = await Db.StaffAppraisals.AsNoTracking().Include(a => a.Subject).Include(a => a.Appraiser)
            .Where(a => a.SubjectUserId == me.Id)
            .OrderByDescending(a => a.PeriodStart)
            .ToListAsync();
        var events = await Db.ActivityEvents.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.SubjectUserId == me.Id && e.Visibility != WelfareVisibility.Restricted)
            .OrderByDescending(e => e.OccurredAt)
            .ToListAsync();
        // Acknowledgements live in a jsonb map; the key is the user id as a string, and Npgsql's
        // jsonb containment is not something we want to hand-write, so filter in memory over the
        // notices that could have reached me at all.
        var acknowledged = (await Db.StaffNotices.AsNoTracking()
                .Where(n => n.OrganizationId == organizationId && n.Acknowledgements != "{}")
                .ToListAsync())
            .Where(n => StaffPerformanceMapping.ParseAcknowledgements(n.Acknowledgements).ContainsKey(me.Id))
            .OrderByDescending(n => n.PublishAt)
            .ToList();

        var names = await BuildNamesAsync(records.Select(r => (Guid?)r.LoggedByUserId)
            .Concat(records.SelectMany(r => r.Notes).Select(n => (Guid?)n.AuthorUserId))
            .Concat(appraisals.SelectMany(a => new[] { (Guid?)a.AppraiserUserId, a.ModeratorUserId, a.SignedByUserId }))
            .Concat(events.Select(e => e.ActorUserId))
            .Concat(acknowledged.Select(n => (Guid?)n.PublishedByUserId))
            .Append(me.Id).Append(me.LineManagerUserId));
        var departmentNames = await DepartmentNamesAsync(organizationId);
        var myDepartmentText = string.Join(", ", (me.DepartmentIds ?? Array.Empty<Guid>()).Select(id => departmentNames.GetValueOrDefault(id)).Where(n => n != null));
        var score = await _scoring.ComputeAsync(organizationId, branchId, me.Id, period, includeRank: true);
        var mayApprove = await HasPermissionAsync(Permissions.StaffAppraisalsApprove);
        var mayConduct = await HasPermissionAsync(Permissions.StaffAppraisalsConduct);

        var export = new StaffFileExportDto
        {
            Me = StaffPerformanceMapping.ToDto(me, names, departmentNames, score),
            GeneratedAt = DateTime.UtcNow,
            Records = records.Select(r => StaffPerformanceMapping.ToDto(r, names, policy.LateEntryThresholdDays)).ToList(),
            Appraisals = appraisals.Select(a => StaffPerformanceMapping.ToDto(a, names, myDepartmentText,
                a.FinalRating is { } fr ? policy.Bands.FirstOrDefault(b => b.Rating == fr)?.Name : null,
                liveScore: null, me.Id, mayApprove, mayConduct)).ToList(),
            Activity = events.Select(e => StaffPerformanceMapping.ToDto(e, names)).ToList(),
            AcknowledgedNotices = await MapNoticesAsync(acknowledged, me.Id),
            DataProtectionOfficerContact = policy.DataProtectionOfficerContact
        };

        await Activity.RecordAsync(ActivityActions.FileExported, nameof(User), me.Id, me.Id,
            $"{StaffPerformanceMapping.FullName(me)} exported their own file ({records.Count} records, {appraisals.Count} appraisals)",
            new { Records = records.Count, Appraisals = appraisals.Count, Events = events.Count }, branchId, organizationId);

        return Ok(export);
    }

    // ---------------------------------------------------------------------
    // Notices: acknowledge
    // ---------------------------------------------------------------------

    /// <summary>
    /// Marks every record about me that I have not seen as seen, in one statement. Restricted records
    /// are excluded (the subject cannot see them, so cannot have seen them) and so are drafts. The
    /// first timestamp on a record already acknowledged stands. Returns how many were marked.
    /// </summary>
    [HttpPost("records/acknowledge-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> AcknowledgeAllRecords()
    {
        var self = await ResolveSelfAsync();
        if (self.Error != null) return self.Error;
        var me = self.User!;
        var now = DateTime.UtcNow;

        var marked = await Db.StaffPerformanceRecords
            .Where(r => r.SubjectUserId == me.Id && r.Status == StaffRecordStatus.Final && r.AcknowledgedAt == null
                        && r.Visibility != WelfareVisibility.Restricted && r.LoggedByUserId != me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.AcknowledgedAt, now));

        if (marked > 0)
            await Activity.RecordAsync(ActivityActions.RecordAcknowledged, nameof(StaffPerformanceRecord), null, me.Id,
                $"{StaffPerformanceMapping.FullName(me)} marked {marked} record(s) as seen", new { Marked = marked }, self.BranchId, self.OrganizationId);

        return Ok(new { marked });
    }

    /// <summary>
    /// Adds me to the notice's acknowledgement map. Idempotent: the first timestamp stands. The
    /// notice must be live and addressed to me — an id for one that is not reads as not found, so
    /// the endpoint cannot be used to enumerate notices meant for other audiences.
    /// </summary>
    [HttpPost("notices/{id:guid}/acknowledge")]
    [ProducesResponseType(typeof(StaffNoticeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeNotice(Guid id)
    {
        var self = await ResolveSelfAsync();
        if (self.Error != null) return self.Error;
        var me = self.User!;
        var now = DateTime.UtcNow;

        var notice = await NoticesForMeQuery(self.OrganizationId, self.BranchId, me.Id, me.DepartmentIds ?? Array.Empty<Guid>(), me.Role?.Code ?? string.Empty, _policy.GroupFor(me.Role?.Code), now)
            .FirstOrDefaultAsync(n => n.Id == id);
        if (notice == null) return NotFoundProblem("Notice not found");

        var acks = StaffPerformanceMapping.ParseAcknowledgements(notice.Acknowledgements);
        if (!acks.ContainsKey(me.Id))
        {
            // CONCURRENCY (found by the e2e, 2026-09-16): this used to read the jsonb map, add one
            // key and write the WHOLE map back, so two people acknowledging at the same moment each
            // wrote a map without the other's key — measured: six simultaneous acknowledgements from
            // three people kept one. The merge now happens inside Postgres in one statement ("||"
            // adds a key to the stored value, not to a copy), guarded by jsonb_exists so the first
            // timestamp stands. Raw SQL, so the table is schema-qualified explicitly.
            var key = me.Id.ToString();
            var stamp = now.ToString("O");
            var written = await Db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE qmgr.\"StaffNotices\" SET \"Acknowledgements\" = COALESCE(\"Acknowledgements\", '{{}}'::jsonb) || jsonb_build_object({key}::text, {stamp}::text) WHERE \"Id\" = {notice.Id} AND NOT jsonb_exists(COALESCE(\"Acknowledgements\", '{{}}'::jsonb), {key}::text)");
            await Db.Entry(notice).ReloadAsync();
            if (written == 0) return Ok((await MapNoticesAsync(new[] { notice }, me.Id)).First());

            await Activity.RecordAsync(ActivityActions.NoticeAcknowledged, nameof(StaffNotice), notice.Id, me.Id,
                $"{StaffPerformanceMapping.FullName(me)} acknowledged notice '{notice.Title}'", null, notice.BranchId ?? self.BranchId, self.OrganizationId);
        }

        return Ok((await MapNoticesAsync(new[] { notice }, me.Id)).First());
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>Who is calling, from the JWT, and which branch their portal is about.</summary>
    private async Task<(IActionResult? Error, User? User, Guid OrganizationId, Guid BranchId)> ResolveSelfAsync()
    {
        var userId = CurrentUserId();
        if (userId == Guid.Empty)
            return (Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "User ID not found in authentication token.", Status = StatusCodes.Status401Unauthorized }), null, default, default);

        // Tracked (not AsNoTracking): nothing here writes the user, but the notice acknowledgement
        // shares the context and a mixed tracking state is not worth the saving.
        var me = await Db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (me == null)
            return (Unauthorized(new ProblemDetails { Title = "Unauthorized", Detail = "Your account is not active.", Status = StatusCodes.Status401Unauthorized }), null, default, default);

        var branchId = me.AssignedBranchId;
        if (branchId == null || !await Db.Branches.AnyAsync(b => b.Id == branchId && b.OrganizationId == me.OrganizationId))
        {
            branchId = await Db.Branches.Where(b => b.OrganizationId == me.OrganizationId && b.IsActive)
                .OrderBy(b => b.CreatedAt).Select(b => (Guid?)b.Id).FirstOrDefaultAsync();
        }
        if (branchId == null)
            return (NotFoundProblem("Your organization has no active branch"), null, default, default);

        return (null, me, me.OrganizationId, branchId.Value);
    }

    private IQueryable<StaffPerformanceRecord> RecordsWithIncludes()
        => Db.StaffPerformanceRecords.AsNoTracking()
            .Include(r => r.Parameter).Include(r => r.Subject).Include(r => r.Duty)
            .Include(r => r.Notes).Include(r => r.Attachments);

    /// <summary>The self rule: about me, below Restricted, no draft unless I wrote it. Across branches, since a file follows the person.</summary>
    private IQueryable<StaffPerformanceRecord> MyRecordsQuery(Guid me, Guid branchId)
        => RecordsWithIncludes().Where(r => r.SubjectUserId == me
                                            && r.Visibility != WelfareVisibility.Restricted
                                            && (r.Status != StaffRecordStatus.Draft || r.LoggedByUserId == me));

    /// <summary>
    /// The audience rule as a query: live, published, for my branch or every branch, for my
    /// department (null or empty = any), for my role (null or empty = any), for my staff group
    /// (null or AllStaff = any). This is <see cref="StaffNoticeFanOut.IsRecipient"/> translated to
    /// SQL — the fan-out decides who is TOLD, this decides who can SEE, and the two must agree or a
    /// person is notified about a notice their portal then hides.
    /// </summary>
    private IQueryable<StaffNotice> NoticesForMeQuery(Guid organizationId, Guid branchId, Guid me, Guid[] myDepartments, string myRole, StaffGroup myGroup, DateTime now)
        => Db.StaffNotices
            .Where(n => n.OrganizationId == organizationId && n.IsActive
                        && n.PublishAt <= now && (n.ExpiresAt == null || n.ExpiresAt > now)
                        && (n.BranchId == null || n.BranchId == branchId)
                        && (n.AudienceDepartmentIds == null || n.AudienceDepartmentIds.Length == 0 || n.AudienceDepartmentIds.Any(id => myDepartments.Contains(id)))
                        && (n.AudienceRoleCodes == null || n.AudienceRoleCodes.Length == 0 || n.AudienceRoleCodes.Contains(myRole))
                        && (n.AudienceStaffGroup == null || n.AudienceStaffGroup == StaffGroup.AllStaff || n.AudienceStaffGroup == myGroup));

    private async Task<List<StaffNoticeDto>> MapNoticesAsync(IReadOnlyCollection<StaffNotice> notices, Guid me)
    {
        if (notices.Count == 0) return new List<StaffNoticeDto>();

        var mediaIds = notices.Where(n => n.AttachmentMediaContentIds != null).SelectMany(n => n.AttachmentMediaContentIds!).Distinct().ToList();
        var media = mediaIds.Count == 0
            ? new Dictionary<Guid, (string Name, string? Url)>()
            : await Db.MediaContents.AsNoTracking()
                .Where(m => mediaIds.Contains(m.Id))
                .Select(m => new { m.Id, m.Name, m.FileUrl })
                .ToDictionaryAsync(m => m.Id, m => (Name: m.Name, Url: m.FileUrl));

        var names = await BuildNamesAsync(notices.Select(n => (Guid?)n.PublishedByUserId));
        return notices.Select(n => StaffPerformanceMapping.ToDto(n, names, me, recipientCount: 0,
            (n.AttachmentMediaContentIds ?? Array.Empty<Guid>())
                .Where(media.ContainsKey)
                .Select(id => new NoticeAttachmentDto { MediaContentId = id, Name = media[id].Name, FileUrl = UploadLinks.Sign(media[id].Url) })
                .ToList())).ToList();
    }

    private async Task<List<StaffDutyDto>> MapDutiesAsync(List<StaffDuty> duties, Guid me, Guid organizationId, Guid branchId, bool mayManage)
    {
        if (duties.Count == 0) return new List<StaffDutyDto>();
        var dutyIds = duties.Select(d => d.Id).ToList();

        var marked = await Db.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.DutyId != null && dutyIds.Contains(r.DutyId.Value) && r.Status == StaffRecordStatus.Final)
            .Select(r => new { DutyId = r.DutyId!.Value, r.SubjectUserId, r.Outcome })
            .ToListAsync();
        var markedCounts = marked.GroupBy(m => m.DutyId).ToDictionary(g => g.Key, g => g.Count());
        var myOutcomes = marked.Where(m => m.SubjectUserId == me).GroupBy(m => m.DutyId).ToDictionary(g => g.Key, g => (DutyOutcome?)g.First().Outcome);

        var needsBranchCount = duties.Any(d => d.ExpectedUserIds == null);
        var branchStaffCount = needsBranchCount
            ? await Db.Users.CountAsync(u => u.OrganizationId == organizationId && u.IsActive && (u.AssignedBranchId == branchId || u.AssignedBranchId == null) && u.Role.Code != RoleCodes.SuperAdmin)
            : 0;

        var minutesIds = duties.Where(d => d.MinutesMediaContentId != null).Select(d => d.MinutesMediaContentId!.Value).Distinct().ToList();
        var minutes = minutesIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await Db.MediaContents.AsNoTracking().Where(m => minutesIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.FileUrl);

        var names = await BuildNamesAsync(duties.SelectMany(d => d.RecorderUserIds.Select(id => (Guid?)id))
            .Concat(duties.Select(d => d.RegisterClosedByUserId))
            .Concat(duties.SelectMany(d => d.SupervisorUserIds.Select(id => (Guid?)id)))
            .Concat(duties.Where(d => d.Kind == DutyKind.Rota).SelectMany(d => (d.ExpectedUserIds ?? Array.Empty<Guid>()).Select(id => (Guid?)id))));

        return duties.Select(d => StaffPerformanceMapping.ToDto(d, names, me, mayManage,
            expectedCount: d.ExpectedUserIds?.Length ?? branchStaffCount,
            markedCount: markedCounts.GetValueOrDefault(d.Id),
            myOutcome: myOutcomes.GetValueOrDefault(d.Id),
            minutesUrl: d.MinutesMediaContentId is { } mid ? minutes.GetValueOrDefault(mid) : null)).ToList();
    }

    private Task<StaffPerformanceMapping.NameLookup> NamesForAsync(IEnumerable<StaffPerformanceRecord> records)
    {
        var list = records.ToList();
        return BuildNamesAsync(list.Select(r => (Guid?)r.LoggedByUserId)
            .Concat(list.Select(r => (Guid?)r.SubjectUserId))
            .Concat(list.SelectMany(r => r.Notes).Select(n => (Guid?)n.AuthorUserId)));
    }
}
