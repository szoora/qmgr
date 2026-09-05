using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Client for the batch endpoints. One service for every bulk action in the app — students,
/// welfare, visitors, queue, appointments and staff accounts all go through the same three calls,
/// because a batch is the same shape whatever it touches.
/// </summary>
public interface IBatchApiService
{
    /// <summary>
    /// What would happen. Never collapses a failure to null: an operator about to change 800
    /// records has to be told why the preview could not be produced, not shown an empty list.
    /// </summary>
    Task<BatchPreviewDto> PreviewAsync(Guid branchId, BatchRequest request);

    /// <summary>Queues the batch. Throws with the API's own message so the dialog can show it.</summary>
    Task<BatchAcceptedDto> RunAsync(Guid branchId, BatchRequest request);

    /// <summary>Reverses a finished batch, or explains why it will not.</summary>
    Task<BatchUndoResultDto> UndoAsync(Guid branchId, Guid jobId);
}

public class BatchApiService : IBatchApiService
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<BatchApiService> _logger;

    public BatchApiService(HttpClient httpClient, JsonSerializerOptions jsonOptions, ILogger<BatchApiService> logger)
    {
        _httpClient = httpClient;
        _jsonOptions = jsonOptions;
        _logger = logger;
    }

    private static string Base(Guid branchId) => $"api/v1/branches/{branchId}/batch";

    public async Task<BatchPreviewDto> PreviewAsync(Guid branchId, BatchRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{Base(branchId)}/preview", request, _jsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<BatchPreviewDto>(_jsonOptions) ?? Blocked("The preview came back empty.");

            return Blocked(await ApiErrorService.GetErrorMessageAsync(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch preview failed for branch {BranchId}", branchId);
            return Blocked("Couldn't reach the server to work out what this would do.");
        }

        BatchPreviewDto Blocked(string error) => new() { Operation = request.Operation, BlockingError = error };
    }

    public async Task<BatchAcceptedDto> RunAsync(Guid branchId, BatchRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync(Base(branchId), request, _jsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));

        return (await response.Content.ReadFromJsonAsync<BatchAcceptedDto>(_jsonOptions))!;
    }

    public async Task<BatchUndoResultDto> UndoAsync(Guid branchId, Guid jobId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"{Base(branchId)}/{jobId}/undo", null);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<BatchUndoResultDto>(_jsonOptions)
                       ?? new BatchUndoResultDto { Success = false, Message = "The server gave no answer." };

            return new BatchUndoResultDto { Success = false, Message = await ApiErrorService.GetErrorMessageAsync(response) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch undo failed for job {JobId}", jobId);
            return new BatchUndoResultDto { Success = false, Message = "Couldn't reach the server." };
        }
    }
}
