using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Application.Import.Programme;

namespace QMgr.Web.Services;

/// <summary>
/// The typed client for the programme import (ProgrammeImportController). THROWS on failure with the server's own
/// message, like every Web API service here except IQueueApiService — a page wraps a call in try/catch and shows why.
/// </summary>
public interface IProgrammeImportApiService
{
    Task<ProgrammeImportContextDto> GetContextAsync(Guid branchId);
    /// <summary>The same body previews (<c>Preview = true</c>, writes nothing) and commits.</summary>
    Task<ProgrammeImportResultDto> ImportAsync(Guid branchId, ProgrammeImportRequest request);
    Task<List<ProgrammeImportJobDto>> GetJobsAsync(Guid branchId);
    Task<ProgrammeUndoResultDto> UndoAsync(Guid branchId, Guid jobId);
    /// <summary>What an import left without a register, without a recorder, or refused (E10).</summary>
    Task<ProgrammeImportHealthDto> GetHealthAsync(Guid branchId, Guid jobId);
}

public class ProgrammeImportApiService : IProgrammeImportApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public ProgrammeImportApiService(HttpClient http, JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    private static string B(Guid branchId) => $"api/v1/branches/{branchId}/calendar/import";

    public Task<ProgrammeImportContextDto> GetContextAsync(Guid branchId) => GetAsync<ProgrammeImportContextDto>($"{B(branchId)}/context");

    public Task<ProgrammeImportResultDto> ImportAsync(Guid branchId, ProgrammeImportRequest request)
        => SendAsync<ProgrammeImportResultDto>(HttpMethod.Post, B(branchId), request);

    public Task<List<ProgrammeImportJobDto>> GetJobsAsync(Guid branchId) => GetAsync<List<ProgrammeImportJobDto>>($"{B(branchId)}/jobs");

    public Task<ProgrammeImportHealthDto> GetHealthAsync(Guid branchId, Guid jobId) => GetAsync<ProgrammeImportHealthDto>($"{B(branchId)}/jobs/{jobId}/health");

    public Task<ProgrammeUndoResultDto> UndoAsync(Guid branchId, Guid jobId)
        => SendAsync<ProgrammeUndoResultDto>(HttpMethod.Post, $"{B(branchId)}/jobs/{jobId}/undo");

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var message = new HttpRequestMessage(method, url);
        if (body != null) message.Content = JsonContent.Create(body, options: _json);
        var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }
}
