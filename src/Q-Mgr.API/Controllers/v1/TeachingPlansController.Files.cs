using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Import.Documents;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Domain.Identity;
using QMgr.Filters;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers.v1;

public partial class TeachingPlansController
{
    /// <summary>The largest request the plan-file endpoint reads at all: the school's own cap is checked inside.</summary>
    private const long MaxPlanRequestBytes = TeachingPlanLimits.CeilingSchemeKb * 1024L + 64 * 1024;

    // ---- The one uploaded PDF ---------------------------------------------------------------------------------

    /// <summary>
    /// Attaches the plan's PDF (plan §5.4–5.6): the fallback route, after the form and the Word template. The browser has
    /// already rebuilt it; this checks it again — the school's size cap, the page limit, and <see cref="PdfGate"/>'s
    /// refusal of anything active — and counts it against the school's storage. Replaces any earlier file.
    /// </summary>
    [HttpPost("{id:guid}/file")]
    [RequestSizeLimit(MaxPlanRequestBytes)]
    [ProducesResponseType(typeof(TeachingPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> UploadFile(Guid branchId, Guid id, IFormFile file, [FromForm] long? originalSize)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanEdit) return ConflictProblem("Attach a file while the plan is a draft or returned");
        if (!ctx.Settings.UploadsEnabled) return BadRequestProblem("The school has switched plan uploads off", "Type the plan on the form, or fill the Word template and upload that — it is read into the form.");
        if (file == null || file.Length == 0) return BadRequestProblem("No file was provided");

        var p = ctx.Plan;
        var capKb = p.Kind == TeachingPlanKind.SchemeOfWork ? ctx.Settings.SchemeCapKb : ctx.Settings.LessonPlanCapKb;
        var maxPages = p.Kind == TeachingPlanKind.SchemeOfWork ? ctx.Settings.SchemeMaxPages : ctx.Settings.LessonPlanMaxPages;
        if (file.Length > capKb * 1024L)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails
            {
                Title = $"The file is {file.Length / 1024} KB; the school allows {capKb} KB",
                Detail = "Open it on the plan page, where it is shrunk before it is sent, or type the plan on the form.",
                Status = StatusCodes.Status413PayloadTooLarge
            });

        byte[] bytes;
        await using (var s = file.OpenReadStream()) { using var ms = new MemoryStream(); await s.CopyToAsync(ms); bytes = ms.ToArray(); }
        var gate = PdfGate.Check(bytes, maxPages);
        if (!gate.Ok) return BadRequestProblem(gate.Problem ?? "That PDF was refused");
        if (!await _usage.IsWithinStorageLimitAsync(p.OrganizationId))
            return BadRequestProblem("The school's storage is full", "Remove old files, or ask an administrator about the storage allowance. Typing the plan on the form uses almost none.");

        var stored = await _mediaStorage.UploadAsync(new MemoryStream(bytes), "plan.pdf", "application/pdf");
        if (!stored.Success) return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "Failed to store the file" });

        var old = p.FileUrl;
        p.FileUrl = stored.FileUrl;
        p.FileName = Truncate(Path.GetFileName(file.FileName) is { Length: > 0 } n ? n : "plan.pdf", 255);
        p.FileSizeBytes = bytes.Length;
        p.OriginalSizeBytes = originalSize is > 0 ? originalSize : bytes.Length;
        p.FilePages = gate.Pages;
        TeachingPlans.AddTrail(p, PlanTrailKind.FileAdded, CurrentUserId(),
            string.Create(CultureInfo.InvariantCulture, $"{p.FileName}, {gate.Pages} page(s), {bytes.Length / 1024.0:0.#} KB{(p.OriginalSizeBytes > bytes.Length ? $" (shrunk from {p.OriginalSizeBytes / 1024.0:0.#} KB)" : "")}"));
        Touch(p, CurrentUserId());
        if (await SaveOrConflictAsync() is { } conflict)
        {
            await DeleteStoredAsync(stored.FileUrl);
            return conflict;
        }
        if (old != null) await DeleteStoredAsync(old);
        await StorageUsage.RecalculateAsync(Db, _usage, p.OrganizationId, _logger);
        await Activity.RecordAsync(ActivityActions.TeachingPlanFileAdded, nameof(TeachingPlan), p.Id, p.AuthorUserId,
            $"PDF attached to {p.Title}", new { p.FileSizeBytes, p.OriginalSizeBytes, p.FilePages }, branchId, p.OrganizationId);
        return Ok(await MapAsync((await LoadAsync(branchId, id, track: false))!));
    }

    [HttpDelete("{id:guid}/file")]
    public async Task<IActionResult> RemoveFile(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var ctx = await LoadAsync(branchId, id, track: true);
        if (ctx == null || !ctx.Access.CanRead) return PlanNotFound();
        if (!ctx.Access.CanEdit) return ConflictProblem("Remove the file while the plan is a draft or returned");
        var p = ctx.Plan;
        if (p.FileUrl == null) return NoContent();
        var old = p.FileUrl;
        p.FileUrl = null; p.FileName = null; p.FileSizeBytes = null; p.OriginalSizeBytes = null; p.FilePages = null;
        TeachingPlans.AddTrail(p, PlanTrailKind.FileRemoved, CurrentUserId());
        Touch(p, CurrentUserId());
        if (await SaveOrConflictAsync() is { } conflict) return conflict;
        await DeleteStoredAsync(old);
        await StorageUsage.RecalculateAsync(Db, _usage, p.OrganizationId, _logger);
        return NoContent();
    }

    /// <summary>Removes a stored file unless another row still points at it. Never throws.</summary>
    private async Task DeleteStoredAsync(string? fileUrl)
    {
        if (string.IsNullOrWhiteSpace(fileUrl)) return;
        try
        {
            var name = fileUrl.Split('?')[0].Split('/').Last();
            var stillUsed = await Db.TeachingPlans.AnyAsync(x => x.FileUrl != null && x.FileUrl.EndsWith("/" + name));
            if (!stillUsed) await _mediaStorage.DeleteAsync(name);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Old plan file {File} could not be removed", fileUrl); }
    }

    // ---- The school's format, for printing a blank ----------------------------------------------------------------

    /// <summary>The school's sections, phases and columns and the current term's length: what a printable blank is drawn
    /// from. Any member of staff; it carries nothing a teacher could not already see on a plan.</summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(PlanTemplateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Template(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var settings = _policy.PlanSettings(policy);
        var zone = await ZoneAsync(branchId);
        var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone)));
        return Ok(new PlanTemplateDto
        {
            Sections = TeachingPlanDefaults.SectionsOf(settings).ToList(),
            Phases = TeachingPlanDefaults.PhasesOf(settings).ToList(),
            Columns = TeachingPlanDefaults.ColumnsOf(settings).ToList(),
            PeriodName = period.Name,
            Weeks = Math.Clamp((period.End.DayNumber - period.Start.DayNumber + 1) / 7, 1, 20),
            TeacherName = await NameAsync(CurrentUserId())
        });
    }

    // ---- Word templates -----------------------------------------------------------------------------------------

    /// <summary>
    /// The school's lesson-plan template as a Word file (plan §5.2), its header filled when it is for one of the caller's
    /// lessons. Anybody on the staff may download it; it carries nothing but what they could already see.
    /// </summary>
    [HttpGet("templates/lesson-plan.docx")]
    public async Task<IActionResult> LessonPlanTemplate(Guid branchId, [FromQuery] Guid? dutyId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var settings = _policy.PlanSettings(policy);
        var zone = await ZoneAsync(branchId);
        var me = CurrentUserId();
        var header = new PlanHeaderDto
        {
            SchoolName = await Db.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Name).FirstOrDefaultAsync(),
            TeacherName = await NameAsync(me)
        };
        string? dateText = null;
        var fileName = "Lesson plan template.docx";
        if (dutyId is { } d)
        {
            var duty = await Db.StaffDuties.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d && x.BranchId == branchId && x.Kind == DutyKind.Lesson
                                                                                   && x.ExpectedUserIds != null && x.ExpectedUserIds.Contains(me));
            if (duty != null)
            {
                var start = TimeZoneInfo.ConvertTimeFromUtc(duty.StartsAt, zone);
                var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(start));
                var subject = duty.SubjectId is { } sid ? (await TeachingPlans.ChainAsync(Db, organizationId, sid)).SubjectName : null;
                header = header with
                {
                    SubjectName = subject, ClassText = duty.ClassName, TermName = period.Name,
                    Week = Math.Max(1, (DateOnly.FromDateTime(start).DayNumber - period.Start.DayNumber) / 7 + 1),
                    Time = string.Create(CultureInfo.InvariantCulture, $"{start:HH:mm}–{TimeZoneInfo.ConvertTimeFromUtc(duty.EndsAt, zone):HH:mm}"),
                    DurationMinutes = (int)Math.Round((duty.EndsAt - duty.StartsAt).TotalMinutes),
                    Learners = duty.ClassName == null ? null : await LearnersAsync(branchId, new[] { duty.ClassName }),
                    Room = duty.Room ?? duty.Location
                };
                dateText = string.Create(CultureInfo.InvariantCulture, $"{start:dddd dd MMM yyyy}");
                fileName = string.Create(CultureInfo.InvariantCulture, $"Lesson plan {subject} {duty.ClassName} {start:yyyy-MM-dd}.docx");
            }
        }
        var bytes = TeachingPlanTemplates.LessonPlan(TeachingPlanDefaults.SectionsOf(settings), TeachingPlanDefaults.PhasesOf(settings), header, dateText);
        return File(bytes, TeachingPlanTemplates.ContentType, SafeFileName(fileName));
    }

    /// <summary>The school's scheme-of-work template as a Word file, filled with the curriculum's topics when there are any.</summary>
    [HttpGet("templates/scheme-of-work.docx")]
    public async Task<IActionResult> SchemeTemplate(Guid branchId, [FromQuery] Guid? subjectId, [FromQuery] string? classNames, [FromQuery] string? periodKey)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var settings = _policy.PlanSettings(policy);
        var zone = await ZoneAsync(branchId);
        var period = _policy.FindPeriod(policy, periodKey) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone)));
        var school = await Db.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Name).FirstOrDefaultAsync();
        var classes = (classNames ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var rows = new List<SchemeRowDto>();
        var title = $"{period.Name}";
        if (subjectId is { } sid)
        {
            var chain = await TeachingPlans.ChainAsync(Db, organizationId, sid);
            title = $"{chain.SubjectName}{(classes.Count > 0 ? " · " + string.Join(", ", classes) : "")} · {period.Name}";
            if (classes.Count > 0)
            {
                var draft = new TeachingPlan { OrganizationId = organizationId, BranchId = branchId, SubjectId = sid, AuthorUserId = CurrentUserId(), ClassNames = classes.ToArray() };
                rows = await PrefillSchemeAsync(draft, period, settings);
            }
        }
        var weeks = Math.Max(1, (period.End.DayNumber - period.Start.DayNumber + 1) / 7);
        var bytes = TeachingPlanTemplates.Scheme(TeachingPlanDefaults.ColumnsOf(settings), title, school, rows, weeks);
        return File(bytes, TeachingPlanTemplates.ContentType, SafeFileName($"Scheme of work {title}.docx"));
    }

    /// <summary>Reads a filled Word template into a plan's content. Nothing is stored; the page puts it in the form.</summary>
    [HttpPost("templates/read")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    [ProducesResponseType(typeof(ReadTemplateResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadTemplate(Guid branchId, IFormFile file)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (file == null || file.Length == 0) return BadRequestProblem("No file was provided");
        if (!file.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            return BadRequestProblem("Upload the Word file (.docx)", "An older .doc cannot be read back; open it in Word and save it as .docx.");
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var settings = _policy.PlanSettings(await _policy.GetAsync(organizationId));
        byte[] bytes;
        await using (var s = file.OpenReadStream()) { using var ms = new MemoryStream(); await s.CopyToAsync(ms); bytes = ms.ToArray(); }
        try
        {
            return Ok(TeachingPlanTemplates.Read(bytes, file.FileName, TeachingPlanDefaults.SectionsOf(settings), TeachingPlanDefaults.ColumnsOf(settings)));
        }
        catch (ImportDocumentException ex) { return BadRequestProblem(ex.Message); }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Replace('·', '-');
        return clean.Length > 150 ? clean[..145] + ".docx" : clean;
    }

    // ---- The curriculum list -----------------------------------------------------------------------------------

    [HttpGet("curriculum")]
    [ProducesResponseType(typeof(CurriculumDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurriculum(Guid branchId, [FromQuery] Guid? subjectId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var c = await CurriculumStore.ReadAsync(Db, await ResolveOrganizationIdAsync(branchId));
        if (subjectId is { } sid) c = new CurriculumDto { Topics = c.Topics.Where(t => t.SubjectId == sid).ToList() };
        return Ok(c);
    }

    /// <summary>Replaces the whole list — the editor's save. <c>staff.parameters.manage</c>, beside the templates.</summary>
    [HttpPut("curriculum")]
    [RequirePermission(Permissions.StaffParametersManage)]
    public async Task<IActionResult> SaveCurriculum(Guid branchId, [FromBody] CurriculumDto request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var tidy = CurriculumStore.Tidy(request ?? new());
        var subjects = (await Db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == organizationId).Select(s => s.Id).ToListAsync()).ToHashSet();
        if (CurriculumStore.Problem(tidy, subjects) is { } problem) return BadRequestProblem(problem);
        await CurriculumStore.WriteAsync(Db, organizationId, tidy);
        await Activity.RecordAsync(ActivityActions.CurriculumChanged, "Curriculum", organizationId, null, $"Curriculum list saved: {tidy.Topics.Count} topic(s)", null, branchId, organizationId);
        return Ok(tidy);
    }

    /// <summary>
    /// Adds or updates topics from an imported sheet — the page parses the CSV, as every import here does, and sends rows.
    /// A topic is the same topic when its subject, class level, term and name match (case and spacing folded).
    /// </summary>
    [HttpPost("curriculum/merge")]
    [RequirePermission(Permissions.StaffParametersManage)]
    [ProducesResponseType(typeof(CurriculumImportResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> MergeCurriculum(Guid branchId, [FromBody] CurriculumDto request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var subjects = (await Db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == organizationId).Select(s => s.Id).ToListAsync()).ToHashSet();
        var result = new CurriculumImportResultDto();
        string Key(CurriculumTopicDto t) => $"{t.SubjectId}|{ClassName.Key(t.ClassLevel)}|{t.Term}|{string.Join(' ', t.Topic.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))}";

        CurriculumDto? saved = null;
        await QMgr.Infrastructure.Services.OrganizationSettingsLock.MutateAsync(Db, organizationId, org =>
        {
            var current = CurriculumStore.Parse(org.Settings);
            var byKey = current.Topics.ToDictionary(Key);
            foreach (var incoming in CurriculumStore.Tidy(request ?? new()).Topics)
            {
                if (string.IsNullOrWhiteSpace(incoming.Topic) || string.IsNullOrWhiteSpace(incoming.ClassLevel) || !subjects.Contains(incoming.SubjectId))
                { result.Refused.Add(string.IsNullOrWhiteSpace(incoming.Topic) ? "A line with no topic." : $"\"{incoming.Topic}\": unknown subject or missing class level."); continue; }
                if (byKey.TryGetValue(Key(incoming), out var existing))
                {
                    var i = current.Topics.IndexOf(existing);
                    current.Topics[i] = incoming with { Id = existing.Id };
                    result.Updated++;
                }
                else { current.Topics.Add(incoming); byKey[Key(incoming)] = incoming; result.Added++; }
            }
            if (result.Added + result.Updated == 0) return false;
            org.Settings = QMgr.Infrastructure.Services.OrganizationSettingsLock.WithKey(org.Settings, CurriculumStore.Key, current);
            org.UpdatedAt = DateTime.UtcNow;
            saved = current;
            return true;
        });
        if (saved != null)
            await Activity.RecordAsync(ActivityActions.CurriculumChanged, "Curriculum", organizationId, null,
                $"Curriculum imported: {result.Added} added, {result.Updated} updated", null, branchId, organizationId);
        return Ok(result);
    }
}

