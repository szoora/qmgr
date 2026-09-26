using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
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
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Duty reports (plan §4.3): write, submit, comment, respond, return, review, "no duty that day", evidence, and the review
/// queue. Every decision about who may read or act goes through <see cref="StaffDutyReports.AccessForAsync"/>; out of reach
/// is 404, never 403. A submitted report is append-only: its text is locked and everything after is a note.
///
/// Notifications name the slot and the period and link to the report; they never carry its sections, summary or a
/// comment's words (plan §8.3). Activity summaries likewise never carry report text.
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff/duty-reports")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffDutyReportsController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly INotificationService _notifications;
    private readonly IStudentScopeService _studentScope;
    private readonly IMediaStorageService _mediaStorage;
    private readonly ILogger<StaffDutyReportsController> _logger;

    private const long MaxAttachmentSizeBytes = 25 * 1024 * 1024;
    private static readonly string[] AllowedAttachmentMimePrefixes = { "image/", "application/pdf", "video/", "audio/" };

    public StaffDutyReportsController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        INotificationService notifications,
        IStudentScopeService studentScope,
        IMediaStorageService mediaStorage,
        ILogger<StaffDutyReportsController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _notifications = notifications;
        _studentScope = studentScope;
        _mediaStorage = mediaStorage;
        _logger = logger;
    }

    // ---- Lists ----------------------------------------------------------------------------------------

    /// <summary>My reports: those whose period has started on rota slots I am on or supervise in the last two months.</summary>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(List<DutyReportSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(Guid branchId, [FromQuery] bool openOnly = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var now = DateTime.UtcNow;

        await EnsureForBranchAsync(branchId, organizationId, now, me);
        var query = Db.StaffDutyReports.AsNoTracking().Include(r => r.Duty)
            .Where(r => r.BranchId == branchId && r.AuthorUserId == me && r.Duty!.IsActive && r.DueAt >= now.AddDays(-62));
        if (openOnly) query = query.Where(r => r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned);
        var reports = await query.OrderBy(r => r.DueAt).Take(100).ToListAsync();
        return Ok(await SummariesAsync(reports, now));
    }

    /// <summary>
    /// The review queue (plan §4.3): every report the caller may read in the window, with on-time %, overdue, awaiting review
    /// and returned. A holder of staff.dutyreports.view sees the authors in their staff scope; a supervisor without it sees the
    /// on-duty reports of the slots they supervise. Drafts are never listed to anyone but their author — an overdue draft is
    /// shown as a line without its content.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(DutyReportQueueDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Queue(Guid branchId, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] DutyReportStatus? status = null, [FromQuery] Guid? dutyId = null, [FromQuery] Guid? authorUserId = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var now = DateTime.UtcNow;
        var start = DateTime.SpecifyKind(from ?? now.AddDays(-30), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to ?? now.AddDays(1), DateTimeKind.Utc);

        var canView = await HasPermissionAsync(Permissions.StaffDutyReportsView) || await HasPermissionAsync(Permissions.StaffDutyReportsReview);
        if (!canView && !await Db.StaffDuties.AnyAsync(d => d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Rota && d.SupervisorUserIds.Contains(me)))
            return StatusCode(StatusCodes.Status403Forbidden);

        await EnsureForBranchAsync(branchId, organizationId, now, null);

        var query = Db.StaffDutyReports.AsNoTracking().Include(r => r.Duty)
            .Where(r => r.BranchId == branchId && r.Duty!.IsActive && r.DueAt >= start && r.DueAt <= end);
        if (dutyId is { } d) query = query.Where(r => r.DutyId == d);
        if (authorUserId is { } a) query = query.Where(r => r.AuthorUserId == a);
        var candidates = await query.OrderByDescending(r => r.DueAt).Take(1000).ToListAsync();

        var policy = await _policy.GetAsync(organizationId);
        var readable = new List<(StaffDutyReport Report, bool Draft)>();
        foreach (var r in candidates)
        {
            var access = await AccessAsync(r, r.Duty!, policy);
            if (access.CanRead || (r.Status == DutyReportStatus.Draft && (access.CanMarkNoDuty || canView && await StaffScope.CanSeeStaffAsync(branchId, r.AuthorUserId))))
                readable.Add((r, r.Status == DutyReportStatus.Draft));
        }

        var due = readable.Where(x => x.Report.DueAt <= now && x.Report.Status != DutyReportStatus.NoDuty).ToList();
        var onTime = due.Count(x => x.Report.SubmittedAt.HasValue && x.Report.SubmittedAt <= x.Report.DueAt);
        var filtered = status is { } s ? readable.Where(x => x.Report.Status == s).ToList() : readable;

        return Ok(new DutyReportQueueDto
        {
            Items = await SummariesAsync(filtered.Select(x => x.Report).Take(300).ToList(), now),
            Total = filtered.Count,
            OnTimePercent = due.Count == 0 ? null : (int)Math.Round(onTime * 100.0 / due.Count),
            Overdue = readable.Count(x => x.Report.Status is DutyReportStatus.Draft or DutyReportStatus.Returned && x.Report.DueAt < now),
            AwaitingReview = readable.Count(x => x.Report.Status == DutyReportStatus.Submitted),
            Returned = readable.Count(x => x.Report.Status == DutyReportStatus.Returned),
            ScopedToDepartments = (await StaffScope.GetScopedDepartmentNamesAsync()).ToList()
        });
    }

    // ---- One report -----------------------------------------------------------------------------------

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DutyReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, policy) = await LoadAsync(branchId, id);
        if (report == null || !access!.CanRead) return NotFoundProblem("Report not found");

        if (!access.IsAuthor)
            await Activity.RecordAsync(ActivityActions.DutyReportViewed, nameof(StaffDutyReport), report.Id, report.AuthorUserId,
                string.Create(CultureInfo.InvariantCulture, $"Duty report for \"{report.Duty!.Title}\", {StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd)} viewed"),
                null, branchId, report.OrganizationId, visibility: report.Visibility);

        return Ok(await MapAsync(report, access, policy!));
    }

    /// <summary>Saves the author's draft (or a returned report being corrected). Sections are checked against the template.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(DutyReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Save(Guid branchId, Guid id, [FromBody] SaveDutyReportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, policy) = await LoadAsync(branchId, id, track: true);
        if (report == null || !access!.IsAuthor) return NotFoundProblem("Report not found");
        if (!access.CanEdit)
            return BadRequestProblem("This report has been submitted", "A submitted report is locked. Add a response to correct or add to it.");

        var problem = await ApplyAsync(report, request, policy!);
        if (problem != null) return problem;
        report.UpdatedAt = DateTime.UtcNow;
        report.UpdatedBy = CurrentUserId();
        await Db.SaveChangesAsync();
        return Ok(await MapAsync(report, access, policy!));
    }

    /// <summary>
    /// Submits. The status change is a conditional UPDATE (Draft/Returned → Submitted), so two submits at once make one
    /// submission, never two notifications. Required sections must be answered.
    /// </summary>
    [HttpPost("{id:guid}/submit")]
    [ProducesResponseType(typeof(DutyReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Submit(Guid branchId, Guid id, [FromBody] SaveDutyReportRequest? request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, policy) = await LoadAsync(branchId, id, track: true);
        if (report == null || !access!.IsAuthor) return NotFoundProblem("Report not found");
        if (!access.CanEdit) return ConflictProblem("Already submitted", "This report has already been submitted.");

        if (request != null)
        {
            var problem = await ApplyAsync(report, request, policy!);
            if (problem != null) return problem;
            report.UpdatedAt = DateTime.UtcNow;
            report.UpdatedBy = CurrentUserId();
            await Db.SaveChangesAsync();
        }

        var sections = StaffDutyReports.ParseSections(report.SectionsJson);
        var missing = _policy.ReportTemplate(policy!).Where(t => t.Required && string.IsNullOrWhiteSpace(sections.GetValueOrDefault(t.Key))).Select(t => t.Title).ToList();
        if (missing.Count > 0) return BadRequestProblem("Answer the required sections", string.Join(", ", missing));

        var now = DateTime.UtcNow;
        var wasReturned = report.Status == DutyReportStatus.Returned;
        var claimed = await Db.StaffDutyReports
            .Where(r => r.Id == report.Id && (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, DutyReportStatus.Submitted).SetProperty(r => r.SubmittedAt, now).SetProperty(r => r.UpdatedAt, now));
        if (claimed == 0) return ConflictProblem("Already submitted", "This report has already been submitted.");
        await Db.Entry(report).ReloadAsync();

        var period = StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd);
        await Activity.RecordAsync(ActivityActions.DutyReportSubmitted, nameof(StaffDutyReport), report.Id, report.AuthorUserId,
            $"Duty report for \"{report.Duty!.Title}\", {period} {(wasReturned ? "resubmitted" : "submitted")}{(now > report.DueAt ? " late" : "")}",
            new { report.DutyId, report.AuthorRole, Late = now > report.DueAt }, branchId, report.OrganizationId, visibility: report.Visibility);

        var readers = await ReviewersAsync(report, report.Duty);
        await NotifyAsync(readers.Where(u => u != report.AuthorUserId), report, NotificationEventKeys.StaffDutyReportSubmitted,
            $"Duty report {(wasReturned ? "resubmitted" : "submitted")}: {report.Duty.Title}", $"{await NameAsync(report.AuthorUserId)}'s report for {period} is ready to read.");

        return Ok(await MapAsync(report, (await AccessAsync(report, report.Duty, policy!)), policy!));
    }

    [HttpPost("{id:guid}/notes")]
    [ProducesResponseType(typeof(DutyReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddNote(Guid branchId, Guid id, [FromBody] DutyReportNoteRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, policy) = await LoadAsync(branchId, id);
        if (report == null || !access!.CanRead) return NotFoundProblem("Report not found");
        if (string.IsNullOrWhiteSpace(request.Body)) return BadRequestProblem("Write something first");

        DutyReportNoteKind kind;
        if (access.IsAuthor)
        {
            if (!access.CanRespond) return BadRequestProblem("Submit the report first", "A response follows a submitted report.");
            kind = DutyReportNoteKind.Response;
        }
        else
        {
            if (!access.CanComment) return StatusCode(StatusCodes.Status403Forbidden);
            kind = DutyReportNoteKind.Comment;
        }

        var me = CurrentUserId();
        Db.StaffDutyReportNotes.Add(new StaffDutyReportNote { ReportId = report.Id, AuthorUserId = me, Kind = kind, Body = request.Body.Trim() });
        await Db.SaveChangesAsync();

        var period = StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd);
        await Activity.RecordAsync(ActivityActions.DutyReportCommented, nameof(StaffDutyReport), report.Id, report.AuthorUserId,
            $"{(kind == DutyReportNoteKind.Response ? "Response" : "Comment")} added to the duty report for \"{report.Duty!.Title}\", {period}",
            null, branchId, report.OrganizationId, visibility: report.Visibility);

        if (kind == DutyReportNoteKind.Comment)
            await NotifyAsync(new[] { report.AuthorUserId }, report, NotificationEventKeys.StaffDutyReportComment,
                "A comment on your duty report", $"A comment was added to your duty report for {report.Duty.Title}, {period}.");
        else
        {
            var others = await Db.StaffDutyReportNotes.AsNoTracking()
                .Where(n => n.ReportId == report.Id && n.AuthorUserId != me)
                .Select(n => n.AuthorUserId).Distinct().ToListAsync();
            await NotifyAsync(others, report, NotificationEventKeys.StaffDutyReportComment,
                "A response on a duty report", $"{await NameAsync(me)} responded on the duty report for {report.Duty.Title}, {period}.");
        }

        return Ok(await MapAsync(await ReloadAsync(report.Id), access, policy!));
    }

    /// <summary>Sends a submitted report back for changes. The returned version is kept in the note.</summary>
    [HttpPost("{id:guid}/return")]
    public async Task<IActionResult> Return(Guid branchId, Guid id, [FromBody] DutyReportNoteRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, policy) = await LoadAsync(branchId, id);
        if (report == null || !access!.CanRead) return NotFoundProblem("Report not found");
        if (DutySeparation.Refusal(CurrentUserId(), report.AuthorUserId, "return your own report") is { } own) return DutySeparation.Problem(own);
        if (!access.CanReview) return access.IsAuthor ? StatusCode(StatusCodes.Status403Forbidden) : BadRequestProblem("Only a submitted report can be returned");
        if (string.IsNullOrWhiteSpace(request.Body)) return BadRequestProblem("Say what needs changing");

        var now = DateTime.UtcNow;
        var claimed = await Db.StaffDutyReports.Where(r => r.Id == report.Id && r.Status == DutyReportStatus.Submitted)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, DutyReportStatus.Returned).SetProperty(r => r.UpdatedAt, now).SetProperty(r => r.ReminderStage, 0));
        if (claimed == 0) return ConflictProblem("The report is no longer awaiting review");

        Db.StaffDutyReportNotes.Add(new StaffDutyReportNote
        {
            ReportId = report.Id,
            AuthorUserId = CurrentUserId(),
            Kind = DutyReportNoteKind.Return,
            Body = request.Body.Trim(),
            SnapshotJson = System.Text.Json.JsonSerializer.Serialize(new { sections = StaffDutyReports.ParseSections(report.SectionsJson), summary = report.Summary })
        });
        await Db.SaveChangesAsync();

        var period = StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd);
        await Activity.RecordAsync(ActivityActions.DutyReportReturned, nameof(StaffDutyReport), report.Id, report.AuthorUserId,
            $"Duty report for \"{report.Duty!.Title}\", {period} returned for changes", null, branchId, report.OrganizationId, visibility: report.Visibility);
        await NotifyAsync(new[] { report.AuthorUserId }, report, NotificationEventKeys.StaffDutyReportReturned,
            "Your duty report was returned", $"Your duty report for {report.Duty.Title}, {period} was returned for changes.");

        var reloaded = await ReloadAsync(report.Id);
        return Ok(await MapAsync(reloaded, await AccessAsync(reloaded, reloaded.Duty!, policy!), policy!));
    }

    [HttpPost("{id:guid}/review")]
    public async Task<IActionResult> Review(Guid branchId, Guid id, [FromBody] DutyReportNoteRequest? request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, policy) = await LoadAsync(branchId, id);
        if (report == null || !access!.CanRead) return NotFoundProblem("Report not found");
        if (DutySeparation.Refusal(CurrentUserId(), report.AuthorUserId, "review your own report") is { } own) return DutySeparation.Problem(own);
        // Plan §13.8: the author cannot mark their own report reviewed, and neither can a supervisor their own.
        if (!access.CanReview) return access.IsAuthor ? StatusCode(StatusCodes.Status403Forbidden) : BadRequestProblem("Only a submitted report can be marked reviewed");

        var now = DateTime.UtcNow;
        var me = CurrentUserId();
        var claimed = await Db.StaffDutyReports.Where(r => r.Id == report.Id && r.Status == DutyReportStatus.Submitted)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, DutyReportStatus.Reviewed).SetProperty(r => r.ReviewedAt, now).SetProperty(r => r.ReviewedByUserId, me).SetProperty(r => r.UpdatedAt, now));
        if (claimed == 0) return ConflictProblem("The report is no longer awaiting review");

        Db.StaffDutyReportNotes.Add(new StaffDutyReportNote { ReportId = report.Id, AuthorUserId = me, Kind = DutyReportNoteKind.Review, Body = string.IsNullOrWhiteSpace(request?.Body) ? "Reviewed." : request!.Body.Trim() });
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.DutyReportReviewed, nameof(StaffDutyReport), report.Id, report.AuthorUserId,
            $"Duty report for \"{report.Duty!.Title}\", {StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd)} marked reviewed", null, branchId, report.OrganizationId, visibility: report.Visibility);

        var reloaded = await ReloadAsync(report.Id);
        return Ok(await MapAsync(reloaded, await AccessAsync(reloaded, reloaded.Duty!, policy!), policy!));
    }

    /// <summary>"No duty that day" (plan §4.4): a closure or cancellation, approved by the slot's supervisor or a duty manager. Stops the reminders.</summary>
    [HttpPost("{id:guid}/no-duty")]
    public async Task<IActionResult> NoDuty(Guid branchId, Guid id, [FromBody] DutyReportNoteRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, policy) = await LoadAsync(branchId, id);
        if (report == null || (!access!.CanRead && !access.CanMarkNoDuty)) return NotFoundProblem("Report not found");
        if (access.IsAuthor && DutySeparation.Refusal(CurrentUserId(), report.AuthorUserId, "excuse your own report") is { } own)
            return DutySeparation.Problem(own);
        if (!access.CanMarkNoDuty) return StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Body)) return BadRequestProblem("Say why there was no duty");

        var now = DateTime.UtcNow;
        var claimed = await Db.StaffDutyReports.Where(r => r.Id == report.Id && (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, DutyReportStatus.NoDuty).SetProperty(r => r.UpdatedAt, now));
        if (claimed == 0) return ConflictProblem("The report has already been submitted");
        Db.StaffDutyReportNotes.Add(new StaffDutyReportNote { ReportId = report.Id, AuthorUserId = CurrentUserId(), Kind = DutyReportNoteKind.Comment, Body = $"No duty: {request.Body.Trim()}" });
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.DutyReportNoDuty, nameof(StaffDutyReport), report.Id, report.AuthorUserId,
            $"Duty report for \"{report.Duty!.Title}\", {StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd)} marked \"no duty that day\"", null, branchId, report.OrganizationId, visibility: report.Visibility);
        return Ok(new { status = DutyReportStatus.NoDuty });
    }

    /// <summary>Reopens a reviewed report for another look (a reviewer, with a reason). It goes back to awaiting review.</summary>
    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid branchId, Guid id, [FromBody] DutyReportNoteRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, policy) = await LoadAsync(branchId, id);
        if (report == null || !access!.CanRead) return NotFoundProblem("Report not found");
        if (access.IsAuthor || !access.CanComment || report.Status != DutyReportStatus.Reviewed) return StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Body)) return BadRequestProblem("Give a reason");

        var now = DateTime.UtcNow;
        var claimed = await Db.StaffDutyReports.Where(r => r.Id == report.Id && r.Status == DutyReportStatus.Reviewed)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, DutyReportStatus.Submitted).SetProperty(r => r.ReviewedAt, (DateTime?)null).SetProperty(r => r.ReviewedByUserId, (Guid?)null).SetProperty(r => r.UpdatedAt, now));
        if (claimed == 0) return ConflictProblem("The report is no longer reviewed");
        Db.StaffDutyReportNotes.Add(new StaffDutyReportNote { ReportId = report.Id, AuthorUserId = CurrentUserId(), Kind = DutyReportNoteKind.Reopen, Body = request.Body.Trim() });
        await Db.SaveChangesAsync();
        await Activity.RecordAsync(ActivityActions.DutyReportReopened, nameof(StaffDutyReport), report.Id, report.AuthorUserId,
            $"Duty report for \"{report.Duty!.Title}\", {StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd)} reopened", null, branchId, report.OrganizationId, visibility: report.Visibility);
        var reloaded = await ReloadAsync(report.Id);
        return Ok(await MapAsync(reloaded, await AccessAsync(reloaded, reloaded.Duty!, policy!), policy!));
    }

    /// <summary>Evidence, by the author while the report is still theirs to edit. Stored like every upload; served signed.</summary>
    [HttpPost("{id:guid}/attachments")]
    [RequestSizeLimit(MaxAttachmentSizeBytes)]
    [ProducesResponseType(typeof(StaffPerformanceAttachmentDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> UploadAttachment(Guid branchId, Guid id, IFormFile file)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var (report, access, _) = await LoadAsync(branchId, id);
        if (report == null || !access!.IsAuthor) return NotFoundProblem("Report not found");
        if (!access.CanEdit) return BadRequestProblem("This report has been submitted", "Evidence is added while the report is being written.");

        if (file == null || file.Length == 0) return BadRequestProblem("No file was provided");
        if (file.Length > MaxAttachmentSizeBytes) return BadRequestProblem($"File exceeds the {MaxAttachmentSizeBytes / 1024 / 1024}MB size limit");
        var mimeType = file.ContentType ?? "";
        if (!AllowedAttachmentMimePrefixes.Any(p => mimeType.StartsWith(p)))
            return BadRequestProblem("Only images, PDF documents, video, or audio are accepted as evidence");

        await using var stream = file.OpenReadStream();
        var upload = await _mediaStorage.UploadAsync(stream, file.FileName, mimeType);
        if (!upload.Success)
        {
            _logger.LogError("Duty report evidence upload failed for report {ReportId}: {Error}", id, upload.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "Failed to store the file" });
        }

        var attachment = new StaffDutyReportAttachment
        {
            ReportId = report.Id,
            FileUrl = upload.FileUrl!,
            FileName = file.FileName,
            ContentType = mimeType,
            FileSizeBytes = file.Length,
            UploadedByUserId = CurrentUserId()
        };
        Db.StaffDutyReportAttachments.Add(attachment);
        await Db.SaveChangesAsync();

        await Activity.RecordAsync(ActivityActions.DutyReportEvidenceAdded, nameof(StaffDutyReport), report.Id, report.AuthorUserId,
            $"Evidence added to the duty report for \"{report.Duty!.Title}\", {StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd)}",
            new { attachment.FileName, attachment.ContentType, attachment.FileSizeBytes }, branchId, report.OrganizationId, visibility: report.Visibility);

        return StatusCode(StatusCodes.Status201Created, ToDto(attachment));
    }

    /// <summary>Welfare records the caller logged recently, to link from a report (plan §4.3: incidents are links, never copied text).</summary>
    [HttpGet("linkable-welfare-records")]
    [ProducesResponseType(typeof(List<DutyReportLinkedRecordDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> LinkableWelfareRecords(Guid branchId, [FromQuery] int days = 31)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var me = CurrentUserId();
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 92));
        var rows = await Db.WelfareRecords.IgnoreQueryFilters().AsNoTracking().Include(w => w.Student).Include(w => w.Category)
            .Where(w => w.BranchId == branchId && w.ReportedByUserId == me && w.OccurredAt >= since)
            .OrderByDescending(w => w.OccurredAt).Take(50).ToListAsync();
        return Ok(rows.Select(w => new DutyReportLinkedRecordDto
        {
            Id = w.Id, StudentId = w.StudentId, StudentName = w.Student?.FullName ?? "", CategoryName = w.Category?.Name ?? "", CaseType = w.CaseType.ToString(), OccurredAt = w.OccurredAt
        }).ToList());
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private async Task<(StaffDutyReport? Report, StaffDutyReports.Access? Access, StaffPerformancePolicyDto? Policy)> LoadAsync(Guid branchId, Guid id, bool track = false)
    {
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var query = Db.StaffDutyReports.Include(r => r.Duty).Where(r => r.Id == id && r.BranchId == branchId && r.OrganizationId == organizationId);
        var report = track ? await query.FirstOrDefaultAsync() : await query.AsNoTracking().FirstOrDefaultAsync();
        if (report?.Duty == null) return (null, null, null);
        var policy = await _policy.GetAsync(organizationId);
        return (report, await AccessAsync(report, report.Duty, policy), policy);
    }

    private async Task<StaffDutyReport> ReloadAsync(Guid id)
        => await Db.StaffDutyReports.AsNoTracking().Include(r => r.Duty).FirstAsync(r => r.Id == id);

    private Task<StaffDutyReports.Access> AccessAsync(StaffDutyReport report, StaffDuty duty, StaffPerformancePolicyDto policy)
        => StaffDutyReports.AccessForAsync(CurrentUserId(), report, duty, policy, HasPermissionAsync, StaffScope.CanSeeStaffAsync);

    /// <summary>Validates and copies a draft's fields. Linked records must be ones the author logged or can see pastorally.</summary>
    private async Task<IActionResult?> ApplyAsync(StaffDutyReport report, SaveDutyReportRequest request, StaffPerformancePolicyDto policy)
    {
        var template = _policy.ReportTemplate(policy);
        var keys = template.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Sections ?? new())
        {
            if (!keys.TryGetValue(key, out var section)) return BadRequestProblem($"Unknown report section \"{key}\"");
            var text = (value ?? "").Trim();
            if (text.Length > 4000) return BadRequestProblem($"\"{section.Title}\" is longer than 4000 characters");
            if (section.Kind == DutyReportSectionKind.Choice && text.Length > 0 && !section.Choices.Contains(text, StringComparer.OrdinalIgnoreCase))
                return BadRequestProblem($"\"{text}\" is not one of the choices for \"{section.Title}\"");
            if (text.Length > 0) sections[section.Key] = text;
        }
        if (request.Visibility is not (WelfareVisibility.Standard or WelfareVisibility.Confidential))
            return BadRequestProblem("A duty report is Standard or Confidential");

        var linked = (request.LinkedWelfareRecordIds ?? new()).Distinct().ToList();
        if (linked.Count > 20) return BadRequestProblem("Link at most 20 records");
        if (linked.Count > 0)
        {
            var me = CurrentUserId();
            var rows = await Db.WelfareRecords.IgnoreQueryFilters().AsNoTracking()
                .Where(w => linked.Contains(w.Id) && w.BranchId == report.BranchId)
                .Select(w => new { w.Id, w.StudentId, w.ReportedByUserId, w.Visibility }).ToListAsync();
            // Only a link the author could already see: their own record, or one their welfare access reaches. Linking an id
            // must never become a way to attach (and so later read) somebody else's safeguarding record.
            var existingLinks = report.LinkedWelfareRecordIds.ToHashSet();
            foreach (var id in linked)
            {
                var w = rows.FirstOrDefault(x => x.Id == id);
                var allowed = w != null && (existingLinks.Contains(id) || w.ReportedByUserId == me || await CanSeeWelfareAsync(report.BranchId, w.StudentId, w.Visibility));
                if (!allowed) return BadRequestProblem("Welfare record not found", "Link records you logged, or that you can open.");
            }
        }

        report.SectionsJson = StaffDutyReports.SerializeSections(sections);
        report.Summary = string.IsNullOrWhiteSpace(request.Summary) ? null : request.Summary.Trim();
        report.LinkedWelfareRecordIds = linked.ToArray();
        report.Visibility = request.Visibility;
        return null;
    }

    /// <summary>The caller's own welfare reach for one record: welfare.view, the visibility rung, and the pastoral student scope.</summary>
    private async Task<bool> CanSeeWelfareAsync(Guid branchId, Guid studentId, WelfareVisibility visibility)
    {
        if (!await HasPermissionAsync(Permissions.WelfareView)) return false;
        var rung = visibility switch
        {
            WelfareVisibility.Standard => true,
            WelfareVisibility.Confidential => await HasPermissionAsync(Permissions.WelfareConfidentialView),
            WelfareVisibility.Restricted => await HasPermissionAsync(Permissions.WelfareRestrictedView),
            _ => false
        };
        return rung && await _studentScope.CanSeeStudentAsync(branchId, studentId);
    }

    private async Task<DutyReportDto> MapAsync(StaffDutyReport report, StaffDutyReports.Access access, StaffPerformancePolicyDto policy)
    {
        var duty = report.Duty!;
        var notes = await Db.StaffDutyReportNotes.AsNoTracking().Where(n => n.ReportId == report.Id).OrderBy(n => n.CreatedAt).ToListAsync();
        var attachments = await Db.StaffDutyReportAttachments.AsNoTracking().Where(a => a.ReportId == report.Id).OrderBy(a => a.CreatedAt).ToListAsync();
        var names = await BuildNamesAsync(new Guid?[] { report.AuthorUserId, report.ReviewedByUserId }
            .Concat(notes.Select(n => (Guid?)n.AuthorUserId)).Concat(duty.SupervisorUserIds.Select(id => (Guid?)id)));

        // Linked records render through the reader's OWN welfare access (plan §13.4): a report is never a side door.
        var me = CurrentUserId();
        var linkedRows = report.LinkedWelfareRecordIds.Length == 0
            ? new List<QMgr.Domain.Entities.Welfare.WelfareRecord>()
            : await Db.WelfareRecords.IgnoreQueryFilters().AsNoTracking().Include(w => w.Student).Include(w => w.Category)
                .Where(w => report.LinkedWelfareRecordIds.Contains(w.Id)).ToListAsync();
        var visible = new List<DutyReportLinkedRecordDto>();
        foreach (var w in linkedRows)
        {
            if (w.ReportedByUserId == me && w.Visibility != WelfareVisibility.Restricted || await CanSeeWelfareAsync(w.BranchId, w.StudentId, w.Visibility))
                visible.Add(new DutyReportLinkedRecordDto { Id = w.Id, StudentId = w.StudentId, StudentName = w.Student?.FullName ?? "", CategoryName = w.Category?.Name ?? "", CaseType = w.CaseType.ToString(), OccurredAt = w.OccurredAt });
        }

        var summary = StaffDutyReports.ToSummary(report, duty, names, notes.Count(n => n.Kind is DutyReportNoteKind.Comment or DutyReportNoteKind.Response), DateTime.UtcNow);
        return new DutyReportDto
        {
            Id = summary.Id, DutyId = summary.DutyId, DutyTitle = summary.DutyTitle, AuthorUserId = summary.AuthorUserId, AuthorName = summary.AuthorName,
            AuthorRole = summary.AuthorRole, PeriodStart = summary.PeriodStart, PeriodEnd = summary.PeriodEnd, DueAt = summary.DueAt, Status = summary.Status,
            IsOverdue = summary.IsOverdue, SubmittedLate = summary.SubmittedLate, SubmittedAt = summary.SubmittedAt, ReviewedAt = summary.ReviewedAt,
            CommentCount = summary.CommentCount, Visibility = summary.Visibility,
            BranchId = report.BranchId,
            DutyStartsAt = duty.StartsAt,
            DutyEndsAt = duty.EndsAt,
            SupervisorNames = duty.SupervisorUserIds.Select(id => names[id]).Where(n => n.Length > 0).ToList(),
            Template = _policy.ReportTemplate(policy).ToList(),
            Sections = StaffDutyReports.ParseSections(report.SectionsJson),
            Summary = report.Summary,
            LinkedRecords = visible,
            HiddenLinkedCount = report.LinkedWelfareRecordIds.Length - visible.Count,
            LinkedWelfareRecordIds = access.IsAuthor ? report.LinkedWelfareRecordIds.ToList() : visible.Select(v => v.Id).ToList(),
            ReviewedByName = names.Optional(report.ReviewedByUserId),
            Notes = notes.Select(n =>
            {
                Dictionary<string, string>? snap = null;
                string? snapSummary = null;
                if (n.SnapshotJson is { Length: > 0 } json)
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        snap = doc.RootElement.TryGetProperty("sections", out var s) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(s.GetRawText()) : null;
                        snapSummary = doc.RootElement.TryGetProperty("summary", out var sm) && sm.ValueKind == System.Text.Json.JsonValueKind.String ? sm.GetString() : null;
                    }
                    catch (System.Text.Json.JsonException) { }
                }
                return new DutyReportNoteDto { Id = n.Id, AuthorUserId = n.AuthorUserId, AuthorName = names[n.AuthorUserId], Kind = n.Kind, Body = n.Body, CreatedAt = n.CreatedAt, SnapshotSections = snap, SnapshotSummary = snapSummary };
            }).ToList(),
            Attachments = attachments.Select(ToDto).ToList(),
            CanEdit = access.CanEdit,
            CanComment = access.CanComment,
            CanRespond = access.CanRespond,
            CanReview = access.CanReview,
            CanMarkNoDuty = access.CanMarkNoDuty
        };
    }

    private async Task<List<DutyReportSummaryDto>> SummariesAsync(List<StaffDutyReport> reports, DateTime now)
    {
        if (reports.Count == 0) return new();
        var ids = reports.Select(r => r.Id).ToList();
        var counts = await Db.StaffDutyReportNotes.AsNoTracking()
            .Where(n => ids.Contains(n.ReportId) && (n.Kind == DutyReportNoteKind.Comment || n.Kind == DutyReportNoteKind.Response))
            .GroupBy(n => n.ReportId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var names = await BuildNamesAsync(reports.Select(r => (Guid?)r.AuthorUserId));
        return reports.Select(r => StaffDutyReports.ToSummary(r, r.Duty!, names, counts.GetValueOrDefault(r.Id), now)).ToList();
    }

    private static StaffPerformanceAttachmentDto ToDto(StaffDutyReportAttachment a) => new()
    {
        Id = a.Id,
        FileUrl = UploadLinks.Sign(a.FileUrl) ?? a.FileUrl,
        FileName = a.FileName,
        ContentType = a.ContentType,
        FileSizeBytes = a.FileSizeBytes,
        UploadedByUserId = a.UploadedByUserId,
        CreatedAt = a.CreatedAt
    };

    /// <summary>Ensures report rows for the branch's rota slots under way or recently over (optionally only those a person is on).</summary>
    private async Task EnsureForBranchAsync(Guid branchId, Guid organizationId, DateTime now, Guid? onlyFor)
    {
        var slots = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Rota && d.ReportCadence != ReportCadence.None
                        && d.StartsAt <= now && d.EndsAt >= now.AddDays(-62)
                        && (onlyFor == null || (d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(onlyFor.Value)) || d.SupervisorUserIds.Contains(onlyFor.Value)))
            .Take(300)
            .ToListAsync();
        if (slots.Count == 0) return;
        var policy = await _policy.GetAsync(organizationId);
        var zone = AppointmentScheduling.ResolveTimeZone(await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());
        foreach (var slot in slots) await StaffDutyReports.EnsureRowsAsync(Db, slot, policy, zone, now, _logger);
    }

    /// <summary>Who is told a report is ready: the slot's supervisors for an on-duty report; otherwise the review-permission holders.</summary>
    private async Task<List<Guid>> ReviewersAsync(StaffDutyReport report, StaffDuty duty)
    {
        if (report.AuthorRole == DutyReportAuthorRole.OnDuty && duty.SupervisorUserIds.Length > 0) return duty.SupervisorUserIds.Distinct().ToList();
        return await StaffLookups.UsersWithPermissionAsync(Db, report.OrganizationId, Permissions.StaffDutyReportsReview);
    }

    private async Task NotifyAsync(IEnumerable<Guid> userIds, StaffDutyReport report, string eventKey, string title, string message)
    {
        foreach (var userId in userIds.Distinct())
        {
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = report.OrganizationId,
                    BranchId = report.BranchId,
                    Title = title,
                    Message = message,
                    Type = NotificationType.StaffPerformance,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = eventKey,
                    ActionUrl = $"/admin/staff/duty-reports/{report.Id}",
                    IconClass = "journal-text"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Duty report notice could not reach user {UserId}", userId);
            }
        }
    }

    private async Task<string> NameAsync(Guid userId) => (await BuildNamesAsync(new Guid?[] { userId }))[userId];
}
