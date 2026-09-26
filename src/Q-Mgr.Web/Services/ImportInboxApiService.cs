using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// The Import inbox (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E11). EVERY CALL THROWS on failure, the same contract as
/// every typed service here except IQueueApiService; a write throws <see cref="ApiFieldException"/> so the page can say
/// the API's own words.
/// </summary>
public interface IImportInboxApiService
{
    Task<List<ImportInboxJobDto>> ListAsync(Guid branchId, bool awaitingOnly = false);
    Task<ImportInboxJobDto?> SubmitAsync(Guid branchId, SubmitImportRequest request);
    Task<ImportInboxStagedDto> GetStagedAsync(Guid branchId, Guid jobId, string sectionKey);
    Task<ImportInboxJobDto?> RejectAsync(Guid branchId, Guid jobId, string sectionKey, string? note);
    /// <summary>A handed-off section finished in its own importer.</summary>
    Task<ImportInboxJobDto?> CompleteAsync(Guid branchId, Guid jobId, string sectionKey, Guid? resultJobId, string? note = null);
    Task<ImportSettingsDto> GetSettingsAsync();
    Task<ImportSettingsDto> SaveSettingsAsync(ImportSettingsDto settings);
}

public class ImportInboxApiService : IImportInboxApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public ImportInboxApiService(HttpClient http, JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    private static string B(Guid branchId) => $"api/v1/branches/{branchId}/imports";

    public async Task<List<ImportInboxJobDto>> ListAsync(Guid branchId, bool awaitingOnly = false)
        => await ReadAsync<List<ImportInboxJobDto>>(await _http.GetAsync($"{B(branchId)}?awaiting={(awaitingOnly ? "true" : "false")}")) ?? new();

    public async Task<ImportInboxJobDto?> SubmitAsync(Guid branchId, SubmitImportRequest request)
        => await ReadAsync<ImportInboxJobDto>(await _http.PostAsJsonAsync(B(branchId), request, _json));

    public async Task<ImportInboxStagedDto> GetStagedAsync(Guid branchId, Guid jobId, string sectionKey)
        => await ReadAsync<ImportInboxStagedDto>(await _http.GetAsync($"{B(branchId)}/{jobId}/sections/{Uri.EscapeDataString(sectionKey)}"))
           ?? throw new InvalidOperationException("That part of the document was not returned.");

    public async Task<ImportInboxJobDto?> RejectAsync(Guid branchId, Guid jobId, string sectionKey, string? note)
        => await ReadAsync<ImportInboxJobDto>(await _http.PostAsJsonAsync($"{B(branchId)}/{jobId}/sections/{Uri.EscapeDataString(sectionKey)}/reject",
            new DecideImportSectionRequest { Note = note }, _json));

    public async Task<ImportInboxJobDto?> CompleteAsync(Guid branchId, Guid jobId, string sectionKey, Guid? resultJobId, string? note = null)
        => await ReadAsync<ImportInboxJobDto>(await _http.PostAsJsonAsync($"{B(branchId)}/{jobId}/sections/{Uri.EscapeDataString(sectionKey)}/complete",
            new DecideImportSectionRequest { Note = note, ResultJobId = resultJobId }, _json));

    public async Task<ImportSettingsDto> GetSettingsAsync()
        => await ReadAsync<ImportSettingsDto>(await _http.GetAsync("api/v1/imports/settings")) ?? new ImportSettingsDto();

    public async Task<ImportSettingsDto> SaveSettingsAsync(ImportSettingsDto settings)
        => await ReadAsync<ImportSettingsDto>(await _http.PutAsJsonAsync("api/v1/imports/settings", settings, _json)) ?? settings;

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
        return await response.Content.ReadFromJsonAsync<T>(_json);
    }
}
