using System.Globalization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Email;
using QMgr.Infrastructure.Services;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// The Staff Performance module's scheduled sweeps (plan §8), one job class, the welfare pattern:
///
///   Duty reminders and register chases     moved to ReminderLadderJob, every 15 minutes (duty rota plan §8.1)
///   Appraisal stage due / overdue          daily 07:00 ReminderSentAt with the 24h re-notify window
///   Scheduled notices reaching PublishAt   every 15 min NotificationsSentAt == null → StaffNoticeFanOut
///   Weekly digest to each staff member     hourly      policy hour/weekday, branch-local; LastStaffDigestSentAt in the preferences blob
///   Monthly administrator summary          hourly      first business day, after the policy hour; LastSummarySentAt in the org policy
///   Activity-log attribution purge         03:30 daily policy retention (the DocumentShareEvent two-tier rule)
///
/// One reminder per row, gated by a timestamp; no reminder table. A job has no tenant, so every
/// query uses IgnoreQueryFilters with the organization joined explicitly, and per-organization
/// policy is read INSIDE the loop (lead times and hours differ per tenant). Each sweep is per-item
/// try/catch with a summary log line; a gate timestamp is written even when some sends fail, so a
/// half-failed fan-out does not re-page everyone on the next pass (the AppointmentJobs rationale).
/// </summary>
public class StaffPerformanceJobs
{
    private readonly QMgrDbContext _context;
    private readonly INotificationService _notifications;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly IStaffScoringService _scoring;
    private readonly INotificationPreferenceResolver _preferences;
    private readonly IModuleAccessService _modules;
    private readonly ILogger<StaffPerformanceJobs> _logger;

    private const int AppraisalStaleDays = 7;

    /// <summary>
    /// Per-sweep memo of "is Staff Performance active for this organization". Before 2026-09-17 only the
    /// digest and the monthly summary asked, so a tenant that had let the module lapse still got duty
    /// reminders, register chases, appraisal nags and scheduled notices for a module it could not open.
    /// </summary>
    private readonly Dictionary<Guid, bool> _moduleActive = new();

    private async Task<bool> ModuleActiveAsync(Guid organizationId)
    {
        if (_moduleActive.TryGetValue(organizationId, out var active)) return active;
        return _moduleActive[organizationId] = await _modules.IsModuleActiveAsync(organizationId, ModuleCodes.StudentWelfare);
    }

    public StaffPerformanceJobs(
        QMgrDbContext context,
        INotificationService notifications,
        IStaffPerformancePolicyService policy,
        IStaffScoringService scoring,
        INotificationPreferenceResolver preferences,
        IModuleAccessService modules,
        ILogger<StaffPerformanceJobs> logger)
    {
        _context = context;
        _notifications = notifications;
        _policy = policy;
        _scoring = scoring;
        _preferences = preferences;
        _modules = modules;
        _logger = logger;
    }

    // ---- (a), (b) Duty reminders and register chases moved to ReminderLadderJob (duty rota plan §8.1) -----

    // ---- (c) Appraisal stage reminders ----------------------------------------------------------------

    /// <summary>
    /// Reminds whoever owns the current stage — the subject at Open / Self-assessment, the appraiser
    /// at Appraiser review, the Approve holders at Moderation / Appealed — once the stage has sat
    /// untouched for seven days (UpdatedAt) or the period has ended, with the 24h re-notify gate.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task SendAppraisalRemindersAsync()
    {
        var now = DateTime.UtcNow;
        var renotifyBefore = now.AddDays(-1);
        var staleBefore = now.AddDays(-AppraisalStaleDays);
        var today = DateOnly.FromDateTime(now);

        var due = await _context.StaffAppraisals
            .Include(a => a.Subject)
            .Where(a => a.IsActive && a.Stage != AppraisalStage.Signed
                        && (a.ReminderSentAt == null || a.ReminderSentAt < renotifyBefore)
                        && ((a.UpdatedAt ?? a.CreatedAt) < staleBefore || a.PeriodEnd < today))
            .ToListAsync();

        var approversByOrg = new Dictionary<Guid, List<Guid>>();
        var sent = 0;
        foreach (var a in due)
        {
            try
            {
                if (!await ModuleActiveAsync(a.OrganizationId)) continue;
                var subjectName = a.Subject != null ? StaffPerformanceMapping.FullName(a.Subject) : "a member of staff";
                var recipients = new List<(Guid UserId, string Title, string Message, string Url)>();

                switch (a.Stage)
                {
                    case AppraisalStage.Open:
                    case AppraisalStage.SelfAssessment:
                        recipients.Add((a.SubjectUserId, $"Your {a.PeriodKey} self-assessment is waiting",
                            a.PeriodEnd < today ? "The period has ended. Complete your self-assessment so your appraiser can review it." : "Rate yourself against the period's parameters and add your reflection.",
                            "/portal"));
                        break;
                    case AppraisalStage.AppraiserReview:
                        recipients.Add((a.AppraiserUserId, $"{subjectName}'s appraisal awaits your review",
                            $"{a.PeriodKey}: the self-assessment was submitted {(a.SelfSubmittedAt.HasValue ? Ago(now - a.SelfSubmittedAt.Value) : "some time ago")}.",
                            "/admin/staff/appraisals"));
                        break;
                    case AppraisalStage.Moderation:
                    case AppraisalStage.Appealed:
                        if (!approversByOrg.TryGetValue(a.OrganizationId, out var approvers))
                            approversByOrg[a.OrganizationId] = approvers = await StaffLookups.UsersWithPermissionAsync(_context, a.OrganizationId, Permissions.StaffAppraisalsApprove);
                        foreach (var approver in approvers)
                            recipients.Add((approver, a.Stage == AppraisalStage.Appealed ? $"{subjectName}'s appeal awaits moderation" : $"{subjectName}'s appraisal awaits moderation and signing",
                                $"{a.PeriodKey}: waiting since {(a.UpdatedAt ?? a.CreatedAt):dd MMM yyyy}.",
                                "/admin/staff/appraisals"));
                        break;
                }

                foreach (var (userId, title, message, url) in recipients)
                {
                    try
                    {
                        await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                        {
                            UserId = userId,
                            OrganizationId = a.OrganizationId,
                            BranchId = a.BranchId,
                            Title = title,
                            Message = message,
                            Type = NotificationType.StaffPerformance,
                            Priority = a.PeriodEnd < today ? NotificationPriority.High : NotificationPriority.Normal,
                            Channels = NotificationChannel.InApp | NotificationChannel.Email,
                            EventKey = NotificationEventKeys.StaffAppraisalStage,
                            ActionUrl = url,
                            IconClass = "clock-history"
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Appraisal reminder for {AppraisalId} could not reach user {UserId}", a.Id, userId);
                    }
                }

                a.ReminderSentAt = now;
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Appraisal reminder failed for {AppraisalId}", a.Id);
            }
        }

        if (sent > 0) await _context.SaveChangesAsync();
        _logger.LogInformation("Staff appraisal reminder sweep: {Sent} reminder(s) sent out of {Total} stale appraisal(s)", sent, due.Count);
    }

    // ---- (d) Scheduled notices -----------------------------------------------------------------------

    [AutomaticRetry(Attempts = 3)]
    public async Task PublishScheduledNoticesAsync()
    {
        var now = DateTime.UtcNow;
        var due = await _context.StaffNotices.IgnoreQueryFilters()
            .Where(n => n.IsActive && n.NotificationsSentAt == null && n.PublishAt <= now)
            .ToListAsync();

        var published = 0;
        foreach (var notice in due)
        {
            try
            {
                // Left unstamped while the module is off, so it goes out if the tenant renews.
                if (!await ModuleActiveAsync(notice.OrganizationId)) continue;
                // The same helper the controller uses; it stamps NotificationsSentAt itself.
                await StaffNoticeFanOut.FanOutAsync(_context, _notifications, notice, _logger);
                published++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled staff notice {NoticeId} could not be published", notice.Id);
            }
        }

        _logger.LogInformation("Staff scheduled-notice sweep: {Published} of {Total} due notice(s) fanned out", published, due.Count);
    }

    // ---- (e) Weekly digest ---------------------------------------------------------------------------

    /// <summary>
    /// To each active staff member of a tenant with the module active, once a week: recognition
    /// received, points and band, duties coming up, anything awaiting their response. Branch-local
    /// on the policy weekday after the policy hour; the per-user gate is LastStaffDigestSentAt in
    /// the preferences blob, read and written only through INotificationPreferenceResolver.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task SendWeeklyDigestsAsync()
    {
        var now = DateTime.UtcNow;
        var organizations = await _context.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.IsActive)
            .Select(o => new { o.Id, o.Name, o.BrandName })
            .ToListAsync();

        var sent = 0;
        foreach (var org in organizations)
        {
            try
            {
                if (!await ModuleActiveAsync(org.Id)) continue;
                var policy = await _policy.GetAsync(org.Id);
                var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(now));

                var branches = await _context.Branches.IgnoreQueryFilters().AsNoTracking()
                    .Where(b => b.OrganizationId == org.Id && b.IsActive)
                    .Select(b => new { b.Id, b.Name, b.Timezone })
                    .ToListAsync();

                // A user assigned to no branch belongs to every branch; they get ONE digest, on the
                // organization's first branch, rather than one per campus.
                var firstBranchId = branches.Count > 0 ? branches[0].Id : Guid.Empty;
                foreach (var branch in branches)
                {
                    var zone = AppointmentScheduling.ResolveTimeZone(branch.Timezone);
                    var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
                    if (local.DayOfWeek != policy.DigestWeekday || local.Hour < policy.DigestHour) continue;

                    var branchId = branch.Id;
                    var includeUnassigned = branchId == firstBranchId;
                    var staff = await StaffLookups.BranchStaff(_context, org.Id, branchId)
                        .Where(u => u.AssignedBranchId == branchId || (u.AssignedBranchId == null && includeUnassigned))
                        .Select(u => new { u.Id, u.FirstName, u.Email })
                        .ToListAsync();

                    foreach (var user in staff)
                    {
                        try
                        {
                            var prefs = await _preferences.GetAsync(user.Id);
                            if (prefs.LastStaffDigestSentAt.HasValue && prefs.LastStaffDigestSentAt.Value > now.AddDays(-6)) continue;

                            var html = await BuildWeeklyDigestAsync(org.Id, branch.Id, branch.Name, org.BrandName ?? org.Name, user.Id, user.FirstName, period, policy, zone);
                            var score = html.Score;

                            await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                            {
                                UserId = user.Id,
                                OrganizationId = org.Id,
                                BranchId = branch.Id,
                                Title = $"Your weekly digest — {period.Name}",
                                Message = score.Composite.HasValue
                                    ? $"{score.Points} points ({Signed(html.PointsThisWeek)} this week), composite {score.Composite:0.#} ({score.BandName}); {html.UpcomingCount} dut(ies) coming up; {html.OpenCount} item(s) awaiting you."
                                    : $"{score.Points} points so far; {html.UpcomingCount} dut(ies) coming up; {html.OpenCount} item(s) awaiting you.",
                                Type = NotificationType.StaffPerformance,
                                Priority = NotificationPriority.Low,
                                Channels = NotificationChannel.InApp | NotificationChannel.Email,
                                EventKey = NotificationEventKeys.StaffWeeklyDigest,
                                EmailSubject = $"Your weekly digest — {branch.Name}, week of {local:dd MMM yyyy}",
                                EmailHtmlBody = html.Html,
                                ActionUrl = "/portal",
                                IconClass = "envelope-paper"
                            });

                            prefs.LastStaffDigestSentAt = now;
                            await _preferences.SaveAsync(user.Id, prefs);
                            sent++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Weekly staff digest failed for user {UserId}", user.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Weekly staff digest sweep failed for organization {OrganizationId}", org.Id);
            }
        }

        _logger.LogInformation("Staff weekly digest sweep: {Sent} digest(s) sent", sent);
    }

    private sealed record DigestBuild(string Html, StaffScoreDto Score, int UpcomingCount, int OpenCount, int PointsThisWeek);

    private async Task<DigestBuild> BuildWeeklyDigestAsync(Guid organizationId, Guid branchId, string branchName, string orgName, Guid userId, string? firstName, PerformancePeriodDto period, StaffPerformancePolicyDto policy, TimeZoneInfo zone)
    {
        var now = DateTime.UtcNow;
        var weekAgo = now.AddDays(-7);
        var score = await _scoring.ComputeAsync(organizationId, branchId, userId, period, includeRank: policy.LeaderboardMode != LeaderboardMode.Private);
        // "Points and band MOVEMENT" (plan §8): the same score as it stood a week ago, from records that
        // existed then. Only meaningful inside one period; a new term starts from nothing.
        var lastWeek = period.Start <= DateOnly.FromDateTime(weekAgo)
            ? await _scoring.ComputeAsync(organizationId, branchId, userId, period, includeRank: false, knownBefore: weekAgo)
            : null;

        var recent = await _context.StaffPerformanceRecords.AsNoTracking()
            .Include(r => r.Parameter)
            .Where(r => r.SubjectUserId == userId && r.Status == StaffRecordStatus.Final && r.CreatedAt >= weekAgo
                        && r.Visibility == WelfareVisibility.Standard)
            .OrderByDescending(r => r.OccurredAt)
            .Take(20)
            .ToListAsync();
        var loggerNames = await StaffLookups.LoadNamesAsync(_context, recent.Select(r => (Guid?)r.LoggedByUserId));

        var upcoming = await _context.StaffDuties.AsNoTracking()
            .Include(d => d.Parameter)
            .Where(d => d.OrganizationId == organizationId && d.BranchId == branchId && d.IsActive
                        && d.StartsAt >= now && d.StartsAt < now.AddDays(7)
                        && (d.ExpectedUserIds == null || d.ExpectedUserIds.Contains(userId) || d.RecorderUserIds.Contains(userId)))
            .OrderBy(d => d.StartsAt)
            .Take(15)
            .ToListAsync();

        var unacknowledged = await _context.StaffPerformanceRecords.AsNoTracking()
            .CountAsync(r => r.SubjectUserId == userId && r.Status == StaffRecordStatus.Final && r.AcknowledgedAt == null && r.Visibility != WelfareVisibility.Restricted);
        var registersDue = await _context.StaffDuties.AsNoTracking()
            .CountAsync(d => d.OrganizationId == organizationId && d.IsActive && d.EndsAt < now && d.RegisterClosedAt == null && d.RecorderUserIds.Contains(userId));
        var appraisal = await _context.StaffAppraisals.AsNoTracking()
            .Where(a => a.SubjectUserId == userId && a.PeriodKey == period.Key && a.IsActive)
            .Select(a => new { a.Stage })
            .FirstOrDefaultAsync();
        var appraisalNeedsMe = appraisal != null && appraisal.Stage is AppraisalStage.Open or AppraisalStage.SelfAssessment;
        var openCount = unacknowledged + registersDue + (appraisalNeedsMe ? 1 : 0);

        var recognition = recent.Where(r => r.Parameter?.Kind == ParameterKind.Recognition).ToList();
        var body = new System.Text.StringBuilder();

        body.Append(EmailTemplates.ReportStat("Points this period", score.Points.ToString(CultureInfo.InvariantCulture)
            + (lastWeek != null ? $" ({Signed(score.Points - lastWeek.Points)} this week)" : "")));
        body.Append(EmailTemplates.ReportStat("Composite", (score.Composite.HasValue ? score.Composite.Value.ToString("0.#", CultureInfo.InvariantCulture) : "—")
            + (lastWeek?.Composite is { } was && score.Composite.HasValue && was != score.Composite.Value ? $" (was {was.ToString("0.#", CultureInfo.InvariantCulture)})" : "")));
        body.Append(EmailTemplates.ReportStat("Band", lastWeek?.BandName is { } oldBand && score.BandName != null && oldBand != score.BandName
            ? $"{oldBand} → {score.BandName}"
            : score.BandName ?? "—"));
        body.Append(EmailTemplates.ReportStat("Duties attended", score.DutiesExpected > 0 ? $"{score.DutiesAttended}/{score.DutiesExpected}" : "—"));
        if (score.RankInBranch.HasValue)
            body.Append(EmailTemplates.ReportStat("Position", $"{score.RankInBranch} of {score.RankedOutOf}"));

        if (recognition.Count > 0)
        {
            body.Append(EmailTemplates.ReportSection("Recognition received this week"));
            body.Append(EmailTemplates.ReportTable(new[] { "From", "For" },
                recognition.Select(r => new[] { EmailTemplates.P(loggerNames[r.LoggedByUserId]), EmailTemplates.P(r.Description) }), recognition.Count));
        }

        var other = recent.Where(r => r.Parameter?.Kind != ParameterKind.Recognition).ToList();
        if (other.Count > 0)
        {
            body.Append(EmailTemplates.ReportSection("Logged about you this week"));
            body.Append(EmailTemplates.ReportTable(new[] { "When", "Parameter", "Outcome", "Points" },
                other.Select(r => new[]
                {
                    EmailTemplates.P(TimeZoneInfo.ConvertTimeFromUtc(r.OccurredAt, zone).ToString("dd MMM", CultureInfo.InvariantCulture)),
                    EmailTemplates.P(r.Parameter?.Name ?? ""),
                    EmailTemplates.P(r.Outcome == DutyOutcome.NotApplicable ? "" : r.Outcome.ToString()),
                    EmailTemplates.P(r.Points.HasValue ? (r.Points.Value > 0 ? "+" : "") + r.Points.Value : "")
                }), other.Count));
        }

        body.Append(EmailTemplates.ReportSection("Coming up in the next seven days"));
        body.Append(EmailTemplates.ReportTable(new[] { "When", "Duty", "Where", "Your part" },
            upcoming.Select(d =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(d.StartsAt, zone);
                return new[]
                {
                    EmailTemplates.P(local.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)),
                    $"<strong>{EmailTemplates.P(d.Title)}</strong><br><span style=\"color:{EmailTemplates.ReportMuted}\">{EmailTemplates.P(d.Parameter?.Name ?? "")}</span>",
                    EmailTemplates.P(d.Location ?? ""),
                    EmailTemplates.P(d.RecorderUserIds.Contains(userId) ? "Take the register" : "Expected")
                };
            }), upcoming.Count, "Nothing scheduled for you this week."));

        body.Append(EmailTemplates.ReportSection("Awaiting you"));
        var items = new List<string[]>();
        if (unacknowledged > 0) items.Add(new[] { EmailTemplates.P($"{unacknowledged} record(s) about you you have not yet marked as seen"), "Open your portal timeline" });
        if (registersDue > 0) items.Add(new[] { EmailTemplates.P($"{registersDue} register(s) you are named recorder for, not yet closed"), "Duties" });
        if (appraisalNeedsMe) items.Add(new[] { EmailTemplates.P($"Your {period.Name} self-assessment"), "Appraisal card" });
        body.Append(EmailTemplates.ReportTable(new[] { "Item", "Where" }, items, items.Count, "Nothing is waiting for you."));

        var html = EmailTemplates.ReportShell(
            $"Your weekly digest — {branchName}",
            $"{orgName} · {period.Name} · {(string.IsNullOrWhiteSpace(firstName) ? "" : $"for {firstName} · ")}sent {TimeZoneInfo.ConvertTimeFromUtc(now, zone):dd MMM yyyy}",
            body.ToString(),
            "Sent by Q-Mgr Staff Performance. Turn this digest off under Notification preferences. Times are shown in the branch's local timezone.");

        return new DigestBuild(html, score, upcoming.Count, openCount, lastWeek != null ? score.Points - lastWeek.Points : score.Points);
    }

    // ---- (f) Monthly administrator summary -----------------------------------------------------------

    /// <summary>
    /// To every holder of staff.reports.view, on the first business day of the month after the
    /// policy hour (branch-local, first active branch decides the clock), idempotent on the policy's
    /// LastSummarySentAt: band distribution, attendance by department, registers not taken,
    /// appraisals by stage, observer dispersion and the coverage list — the reports page in an email.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task SendMonthlySummaryAsync()
    {
        var now = DateTime.UtcNow;
        var organizations = await _context.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.IsActive)
            .Select(o => new { o.Id, o.Name, o.BrandName })
            .ToListAsync();

        var sentOrgs = 0;
        foreach (var org in organizations)
        {
            try
            {
                if (!await ModuleActiveAsync(org.Id)) continue;
                var policy = await _policy.GetAsync(org.Id);

                var branches = await _context.Branches.IgnoreQueryFilters().AsNoTracking()
                    .Where(b => b.OrganizationId == org.Id && b.IsActive)
                    .OrderBy(b => b.CreatedAt)
                    .Select(b => new { b.Id, b.Name, b.Timezone })
                    .ToListAsync();
                if (branches.Count == 0) continue;

                var zone = AppointmentScheduling.ResolveTimeZone(branches[0].Timezone);
                var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
                if (!IsFirstBusinessDay(local.Date) || local.Hour < policy.SummaryHour) continue;
                if (policy.LastSummarySentAt.HasValue)
                {
                    var lastLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(policy.LastSummarySentAt.Value, DateTimeKind.Utc), zone);
                    if (lastLocal.Year == local.Year && lastLocal.Month == local.Month) continue;
                }

                var recipients = await StaffLookups.UsersWithPermissionAsync(_context, org.Id, Permissions.StaffReportsView);
                if (recipients.Count == 0)
                {
                    policy.LastSummarySentAt = now;
                    await _policy.SaveAsync(org.Id, policy);
                    continue;
                }

                // The completed month, reported in the period it fell in.
                var lastMonth = local.Date.AddDays(-1);
                var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(lastMonth));

                var body = new System.Text.StringBuilder();
                foreach (var branch in branches)
                {
                    var report = await StaffReportBuilder.BuildAsync(_context, _scoring, _policy, org.Id, branch.Id, period, policy, visible: null);
                    body.Append(RenderBranchSummary(branch.Name, report));
                }

                var subject = $"Staff performance — {org.BrandName ?? org.Name} — {lastMonth:MMMM yyyy}";
                var html = EmailTemplates.ReportShell(
                    $"Staff performance summary — {lastMonth:MMMM yyyy}",
                    $"{org.BrandName ?? org.Name} · {period.Name} to date · {branches.Count} branch(es)",
                    body.ToString(),
                    "Sent by Q-Mgr Staff Performance to holders of staff.reports.view on the first business day of each month.");

                var delivered = 0;
                foreach (var userId in recipients)
                {
                    try
                    {
                        await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                        {
                            UserId = userId,
                            OrganizationId = org.Id,
                            BranchId = branches[0].Id,
                            Title = $"Monthly staff performance summary — {lastMonth:MMMM yyyy}",
                            Message = $"Band distribution, attendance by department, registers not taken, appraisals by stage, observer dispersion and coverage for {period.Name}.",
                            Type = NotificationType.StaffPerformance,
                            Priority = NotificationPriority.Normal,
                            Channels = NotificationChannel.InApp | NotificationChannel.Email,
                            EventKey = NotificationEventKeys.StaffWeeklyDigest,
                            EmailSubject = subject,
                            EmailHtmlBody = html,
                            ActionUrl = "/admin/staff/reports",
                            IconClass = "bar-chart-line"
                        });
                        delivered++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Monthly staff summary could not reach user {UserId}", userId);
                    }
                }

                policy.LastSummarySentAt = now;
                await _policy.SaveAsync(org.Id, policy);
                sentOrgs++;
                _logger.LogInformation("Monthly staff summary for organization {OrganizationId} sent to {Delivered}/{Total} recipient(s)", org.Id, delivered, recipients.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monthly staff summary failed for organization {OrganizationId}", org.Id);
            }
        }

        _logger.LogInformation("Staff monthly summary sweep: {Sent} organization(s) sent", sentOrgs);
    }

    private static string RenderBranchSummary(string branchName, StaffReportsDto r)
    {
        var b = new System.Text.StringBuilder();
        b.Append(EmailTemplates.ReportSection(branchName));
        b.Append(EmailTemplates.ReportStat("Staff", r.StaffCount.ToString(CultureInfo.InvariantCulture)));
        b.Append(EmailTemplates.ReportStat("Records", r.RecordCount.ToString("N0", CultureInfo.InvariantCulture)));
        b.Append(EmailTemplates.ReportStat("Recognition", r.RecognitionCount.ToString("N0", CultureInfo.InvariantCulture)));
        b.Append(EmailTemplates.ReportStat("Registers not taken", r.RegistersNotTaken.ToString(CultureInfo.InvariantCulture), alert: r.RegistersNotTaken > 0));

        b.Append(EmailTemplates.ReportSection("Band distribution"));
        b.Append(EmailTemplates.ReportTable(new[] { "Band", "Staff" },
            r.BandDistribution.Select(x => new[] { $"{x.Rating} — {EmailTemplates.P(x.Name)}", x.Count.ToString(CultureInfo.InvariantCulture) }), r.BandDistribution.Count));

        b.Append(EmailTemplates.ReportSection("Attendance by department"));
        b.Append(EmailTemplates.ReportTable(new[] { "Department", "Staff", "Attendance", "Avg composite", "Recognition" },
            r.ByDepartment.Select(d => new[]
            {
                EmailTemplates.P(d.Name), d.StaffCount.ToString(CultureInfo.InvariantCulture),
                d.AttendanceRate.HasValue ? $"{d.AttendanceRate.Value.ToString("0.#", CultureInfo.InvariantCulture)}%" : "—",
                d.AverageComposite.HasValue ? d.AverageComposite.Value.ToString("0.#", CultureInfo.InvariantCulture) : "—",
                d.RecognitionCount.ToString(CultureInfo.InvariantCulture)
            }), r.ByDepartment.Count));

        b.Append(EmailTemplates.ReportSection("Appraisals by stage"));
        b.Append(EmailTemplates.ReportTable(new[] { "Stage", "Count" },
            r.AppraisalsByStage.Select(kv => new[] { EmailTemplates.P(kv.Key), kv.Value.ToString(CultureInfo.InvariantCulture) }), r.AppraisalsByStage.Count));

        b.Append(EmailTemplates.ReportSection("Observer dispersion"));
        b.Append(EmailTemplates.ReportTable(new[] { "Observer", "Observations", "Mean rating", "Branch mean" },
            r.ObserverDispersion.Select(o => new[]
            {
                EmailTemplates.P(o.Name), o.Observations.ToString(CultureInfo.InvariantCulture),
                o.MeanRating.ToString("0.00", CultureInfo.InvariantCulture), o.SchoolMean.ToString("0.00", CultureInfo.InvariantCulture)
            }), r.ObserverDispersion.Count, "No rated observations in the period."));

        var c = r.Coverage;
        b.Append(EmailTemplates.ReportSection("Coverage"));
        b.Append(EmailTemplates.ReportTable(new[] { "Gap", "Count", "Who" }, new[]
        {
            new[] { "Departments without a head", c.DepartmentsWithoutHead.Count.ToString(CultureInfo.InvariantCulture), EmailTemplates.P(Names(c.DepartmentsWithoutHead.Select(d => d.Name))) },
            new[] { "Staff in no department", c.StaffWithoutDepartment.Count.ToString(CultureInfo.InvariantCulture), EmailTemplates.P(Names(c.StaffWithoutDepartment.Select(s => s.FullName))) },
            new[] { "Staff with no line manager", c.StaffWithoutLineManager.Count.ToString(CultureInfo.InvariantCulture), EmailTemplates.P(Names(c.StaffWithoutLineManager.Select(s => s.FullName))) },
            new[] { "Staff with no appraisal this period", c.StaffWithoutAppraiserThisPeriod.Count.ToString(CultureInfo.InvariantCulture), EmailTemplates.P(Names(c.StaffWithoutAppraiserThisPeriod.Select(s => s.FullName))) },
            new[] { "Teaching staff not observed this period", c.StaffWithoutObservationThisPeriod.Count.ToString(CultureInfo.InvariantCulture), EmailTemplates.P(Names(c.StaffWithoutObservationThisPeriod.Select(s => s.FullName))) },
            new[] { "Parameters with no records this period", c.ParametersWithNoRecordsThisPeriod.Count.ToString(CultureInfo.InvariantCulture), EmailTemplates.P(Names(c.ParametersWithNoRecordsThisPeriod.Select(p => p.Name))) },
        }, 6));

        return b.ToString();
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.Take(8).ToList();
        var total = names.Count();
        return list.Count == 0 ? "—" : string.Join(", ", list) + (total > list.Count ? $" and {total - list.Count} more" : "");
    }

    private static bool IsFirstBusinessDay(DateTime localDate)
    {
        if (localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        // The first weekday of the month: the 1st unless the 1st fell on a weekend, then the following Monday.
        var first = new DateTime(localDate.Year, localDate.Month, 1);
        while (first.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) first = first.AddDays(1);
        return first.Date == localDate.Date;
    }

    // ---- (g) Activity-log attribution purge ----------------------------------------------------------

    /// <summary>Blanks IpAddress / UserAgent on ActivityEvents older than each organization's retention window; the row stays. The DocumentShareRetentionJob shape.</summary>
    public async Task PurgeActivityAttributionAsync()
    {
        var organizations = await _context.Organizations.IgnoreQueryFilters().AsNoTracking().Select(o => new { o.Id, o.Settings }).ToListAsync();

        var total = 0;
        foreach (var org in organizations)
        {
            try
            {
                var policy = _policy.ReadPolicy(org.Settings);
                var days = Math.Max(1, policy.ActivityAttributionRetentionDays);
                var cutoff = DateTime.UtcNow.AddDays(-days);

                var updated = await _context.ActivityEvents
                    .IgnoreQueryFilters()
                    .Where(e => e.OrganizationId == org.Id && e.OccurredAt < cutoff && (e.IpAddress != null || e.UserAgent != null))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.IpAddress, (string?)null)
                        .SetProperty(e => e.UserAgent, (string?)null));

                if (updated > 0)
                    _logger.LogInformation("Activity attribution purge: organization {OrganizationId} — {Count} event(s) older than {Days}d anonymised", org.Id, updated, days);
                total += updated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activity attribution purge failed for organization {OrganizationId}", org.Id);
            }
        }

        _logger.LogInformation("Activity attribution purge complete: {Total} event(s) anonymised", total);
    }

    private static string Signed(int n) => n > 0 ? $"+{n}" : n.ToString(CultureInfo.InvariantCulture);

    private static string Ago(TimeSpan span)
    {
        if (span.TotalHours < 1) return "less than an hour ago";
        if (span.TotalHours < 48) return $"{(int)span.TotalHours} hour(s) ago";
        return $"{(int)span.TotalDays} day(s) ago";
    }
}

public static class StaffPerformanceJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Daily 07:00 UTC: a stage is due on a date, not at a moment; the 24h gate is on ReminderSentAt.
        RecurringJob.AddOrUpdate<StaffPerformanceJobs>("staff-appraisal-reminders", job => job.SendAppraisalRemindersAsync(), Cron.Daily(7));

        // Every 15 minutes: a scheduled notice should land close to the minute its publisher chose.
        RecurringJob.AddOrUpdate<StaffPerformanceJobs>("staff-scheduled-notices", job => job.PublishScheduledNoticesAsync(), "*/15 * * * *");

        // Hourly: the policy weekday/hour is branch-local and evaluated inside; LastStaffDigestSentAt per user is the gate.
        RecurringJob.AddOrUpdate<StaffPerformanceJobs>("staff-weekly-digests", job => job.SendWeeklyDigestsAsync(), "0 * * * *");

        // Hourly: first business day after the policy hour, branch-local; LastSummarySentAt in the org policy is the gate.
        RecurringJob.AddOrUpdate<StaffPerformanceJobs>("staff-monthly-summary", job => job.SendMonthlySummaryAsync(), "0 * * * *");

        // 03:30 UTC, alongside the share-link attribution purge.
        RecurringJob.AddOrUpdate<StaffPerformanceJobs>("staff-activity-attribution-purge", job => job.PurgeActivityAttributionAsync(), "30 3 * * *");
    }
}
