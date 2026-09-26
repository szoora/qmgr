using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Lesson plans and schemes of work (plan LESSON_PLANS_AND_SCHEMES_OF_WORK). EVERY CALL THROWS on failure with the API's
/// own words (<see cref="ApiFieldException"/>), the contract of every typed service here except IQueueApiService.
/// </summary>
public interface ITeachingPlanApiService
{
    Task<PlanbookDto> GetPlanbookAsync(Guid branchId, DateOnly? weekStart = null);
    Task<List<TeachingPlanSummaryDto>> GetMineAsync(Guid branchId, TeachingPlanKind? kind = null, TeachingPlanStatus? status = null);
    Task<TeachingPlanQueueDto> GetQueueAsync(Guid branchId, TeachingPlanStatus? status = null, TeachingPlanKind? kind = null, Guid? subjectId = null,
        Guid? departmentId = null, bool awaitingMe = false, string? search = null);
    Task<TeachingPlanDto> GetAsync(Guid branchId, Guid id);
    Task<TeachingPlanDto> CreateAsync(Guid branchId, CreateTeachingPlanRequest request);
    Task<TeachingPlanDto> SaveAsync(Guid branchId, Guid id, SaveTeachingPlanRequest request);
    Task<TeachingPlanDto> SubmitAsync(Guid branchId, Guid id);
    Task<TeachingPlanDto> WithdrawAsync(Guid branchId, Guid id);
    Task DiscardAsync(Guid branchId, Guid id);
    Task<TeachingPlanDto> ForwardAsync(Guid branchId, Guid id, string? note);
    Task<TeachingPlanDto> ApproveAsync(Guid branchId, Guid id, string? note);
    Task<TeachingPlanDto> ReturnAsync(Guid branchId, Guid id, string reason, string? sectionKey = null);
    Task<TeachingPlanDto> CommentAsync(Guid branchId, Guid id, string body, string? sectionKey = null);
    Task<TeachingPlanDto> ReflectAsync(Guid branchId, Guid id, string? text);
    Task<TeachingPlanDto> ReviseAsync(Guid branchId, Guid id);
    Task<TeachingPlanDto> AlsoForAsync(Guid branchId, Guid id, List<string> classNames);
    Task<TeachingPlanDto> BumpAsync(Guid branchId, Guid id, Guid toDutyId);
    Task<TeachingPlanDto> UploadFileAsync(Guid branchId, Guid id, byte[] pdf, string fileName, long originalSize);
    Task RemoveFileAsync(Guid branchId, Guid id);
    Task<PlanTemplateDto> GetTemplateAsync(Guid branchId);
    Task<byte[]> DownloadLessonTemplateAsync(Guid branchId, Guid? dutyId);
    Task<byte[]> DownloadSchemeTemplateAsync(Guid branchId, Guid? subjectId, IEnumerable<string>? classNames, string? periodKey);
    Task<ReadTemplateResultDto> ReadTemplateAsync(Guid branchId, byte[] docx, string fileName);
    Task<RecordOfWorkDto> GetRecordOfWorkAsync(Guid branchId, Guid schemeId);
    Task<TeachingPlanReportDto> GetReportAsync(Guid branchId, DateOnly? from, DateOnly? to);
    Task<CurriculumDto> GetCurriculumAsync(Guid branchId, Guid? subjectId = null);
    Task<CurriculumDto> SaveCurriculumAsync(Guid branchId, CurriculumDto curriculum);
    Task<CurriculumImportResultDto> MergeCurriculumAsync(Guid branchId, CurriculumDto topics);
}

public class TeachingPlanApiService : ITeachingPlanApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public TeachingPlanApiService(HttpClient http, JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    private static string B(Guid branchId) => $"api/v1/branches/{branchId}/teaching-plans";

    public async Task<PlanbookDto> GetPlanbookAsync(Guid branchId, DateOnly? weekStart = null)
        => await ReadAsync<PlanbookDto>(await _http.GetAsync($"{B(branchId)}/planbook{(weekStart is { } w ? $"?weekStart={w:yyyy-MM-dd}" : "")}")) ?? new();

    public async Task<List<TeachingPlanSummaryDto>> GetMineAsync(Guid branchId, TeachingPlanKind? kind = null, TeachingPlanStatus? status = null)
        => await ReadAsync<List<TeachingPlanSummaryDto>>(await _http.GetAsync($"{B(branchId)}/mine{Q(("kind", kind?.ToString()), ("status", status?.ToString()))}")) ?? new();

    public async Task<TeachingPlanQueueDto> GetQueueAsync(Guid branchId, TeachingPlanStatus? status = null, TeachingPlanKind? kind = null, Guid? subjectId = null,
        Guid? departmentId = null, bool awaitingMe = false, string? search = null)
        => await ReadAsync<TeachingPlanQueueDto>(await _http.GetAsync($"{B(branchId)}/queue{Q(("status", status?.ToString()), ("kind", kind?.ToString()),
            ("subjectId", subjectId?.ToString()), ("departmentId", departmentId?.ToString()), ("awaitingMe", awaitingMe ? "true" : null), ("search", search))}")) ?? new();

    public async Task<TeachingPlanDto> GetAsync(Guid branchId, Guid id) => await Required(await _http.GetAsync($"{B(branchId)}/{id}"));
    public async Task<TeachingPlanDto> CreateAsync(Guid branchId, CreateTeachingPlanRequest request) => await Required(await _http.PostAsJsonAsync(B(branchId), request, _json));
    public async Task<TeachingPlanDto> SaveAsync(Guid branchId, Guid id, SaveTeachingPlanRequest request) => await Required(await _http.PutAsJsonAsync($"{B(branchId)}/{id}", request, _json));
    public async Task<TeachingPlanDto> SubmitAsync(Guid branchId, Guid id) => await Required(await _http.PostAsync($"{B(branchId)}/{id}/submit", null));
    public async Task<TeachingPlanDto> WithdrawAsync(Guid branchId, Guid id) => await Required(await _http.PostAsync($"{B(branchId)}/{id}/withdraw", null));
    public async Task DiscardAsync(Guid branchId, Guid id) => await ReadAsync<object>(await _http.PostAsync($"{B(branchId)}/{id}/discard", null));
    public async Task<TeachingPlanDto> ForwardAsync(Guid branchId, Guid id, string? note) => await Decision(branchId, id, "forward", note, null);
    public async Task<TeachingPlanDto> ApproveAsync(Guid branchId, Guid id, string? note) => await Decision(branchId, id, "approve", note, null);
    public async Task<TeachingPlanDto> ReturnAsync(Guid branchId, Guid id, string reason, string? sectionKey = null) => await Decision(branchId, id, "return", reason, sectionKey);
    public async Task<TeachingPlanDto> CommentAsync(Guid branchId, Guid id, string body, string? sectionKey = null) => await Decision(branchId, id, "comment", body, sectionKey);
    public async Task<TeachingPlanDto> ReflectAsync(Guid branchId, Guid id, string? text) => await Decision(branchId, id, "reflection", text, null);
    public async Task<TeachingPlanDto> ReviseAsync(Guid branchId, Guid id) => await Required(await _http.PostAsync($"{B(branchId)}/{id}/revise", null));
    public async Task<TeachingPlanDto> AlsoForAsync(Guid branchId, Guid id, List<string> classNames)
        => await Required(await _http.PostAsJsonAsync($"{B(branchId)}/{id}/copy", new CopyTeachingPlanRequest { AlsoClassNames = classNames }, _json));
    public async Task<TeachingPlanDto> BumpAsync(Guid branchId, Guid id, Guid toDutyId)
        => await Required(await _http.PostAsJsonAsync($"{B(branchId)}/{id}/bump", new BumpTeachingPlanRequest { ToDutyId = toDutyId }, _json));

    public async Task<TeachingPlanDto> UploadFileAsync(Guid branchId, Guid id, byte[] pdf, string fileName, long originalSize)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "plan.pdf" : fileName);
        form.Add(new StringContent(originalSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), "originalSize");
        return await Required(await _http.PostAsync($"{B(branchId)}/{id}/file", form));
    }

    public async Task RemoveFileAsync(Guid branchId, Guid id) => await ReadAsync<object>(await _http.DeleteAsync($"{B(branchId)}/{id}/file"));

    public async Task<PlanTemplateDto> GetTemplateAsync(Guid branchId)
        => await ReadAsync<PlanTemplateDto>(await _http.GetAsync($"{B(branchId)}/templates")) ?? new();

    public async Task<byte[]> DownloadLessonTemplateAsync(Guid branchId, Guid? dutyId)
        => await BytesAsync(await _http.GetAsync($"{B(branchId)}/templates/lesson-plan.docx{(dutyId is { } d ? $"?dutyId={d}" : "")}"));

    public async Task<byte[]> DownloadSchemeTemplateAsync(Guid branchId, Guid? subjectId, IEnumerable<string>? classNames, string? periodKey)
        => await BytesAsync(await _http.GetAsync($"{B(branchId)}/templates/scheme-of-work.docx{Q(("subjectId", subjectId?.ToString()),
            ("classNames", classNames == null ? null : string.Join(",", classNames)), ("periodKey", periodKey))}"));

    public async Task<ReadTemplateResultDto> ReadTemplateAsync(Guid branchId, byte[] docx, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(docx);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        form.Add(file, "file", fileName);
        return await ReadAsync<ReadTemplateResultDto>(await _http.PostAsync($"{B(branchId)}/templates/read", form)) ?? new();
    }

    public async Task<RecordOfWorkDto> GetRecordOfWorkAsync(Guid branchId, Guid schemeId)
        => await ReadAsync<RecordOfWorkDto>(await _http.GetAsync($"{B(branchId)}/{schemeId}/record-of-work")) ?? new();

    public async Task<TeachingPlanReportDto> GetReportAsync(Guid branchId, DateOnly? from, DateOnly? to)
        => await ReadAsync<TeachingPlanReportDto>(await _http.GetAsync($"{B(branchId)}/reports{Q(("from", from?.ToString("yyyy-MM-dd")), ("to", to?.ToString("yyyy-MM-dd")))}")) ?? new();

    public async Task<CurriculumDto> GetCurriculumAsync(Guid branchId, Guid? subjectId = null)
        => await ReadAsync<CurriculumDto>(await _http.GetAsync($"{B(branchId)}/curriculum{Q(("subjectId", subjectId?.ToString()))}")) ?? new();

    public async Task<CurriculumDto> SaveCurriculumAsync(Guid branchId, CurriculumDto curriculum)
        => await ReadAsync<CurriculumDto>(await _http.PutAsJsonAsync($"{B(branchId)}/curriculum", curriculum, _json)) ?? curriculum;

    public async Task<CurriculumImportResultDto> MergeCurriculumAsync(Guid branchId, CurriculumDto topics)
        => await ReadAsync<CurriculumImportResultDto>(await _http.PostAsJsonAsync($"{B(branchId)}/curriculum/merge", topics, _json)) ?? new();

    // ---- plumbing ----

    private async Task<TeachingPlanDto> Decision(Guid branchId, Guid id, string action, string? note, string? sectionKey)
        => await Required(await _http.PostAsJsonAsync($"{B(branchId)}/{id}/{action}", new PlanDecisionRequest { Note = note, SectionKey = sectionKey }, _json));

    private async Task<TeachingPlanDto> Required(HttpResponseMessage response)
        => await ReadAsync<TeachingPlanDto>(response) ?? throw new InvalidOperationException("The plan was not returned.");

    private async Task<byte[]> BytesAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default;
        return await response.Content.ReadFromJsonAsync<T>(_json);
    }

    private static string Q(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
