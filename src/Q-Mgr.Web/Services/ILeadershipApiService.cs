using System.Net.Http.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// The leadership posts (safeguarding lead, acting head, house and dormitory posts) and the access review
/// (2026-09-24). Every call THROWS with the server's own words on failure, like its neighbours, so a caller's
/// try/catch shows the real reason. Reads with the app's one JsonSerializerOptions — PastoralUnitKind is an enum
/// the API sends as a string.
/// </summary>
public interface ILeadershipApiService
{
    Task<LeadershipViewDto?> GetAsync();
    Task<LeadershipViewDto?> UpdateSafeguardingAsync(UpdateSafeguardingLeadsRequest request);
    Task<LeadershipViewDto?> UpdateActingHeadAsync(UpdateActingHeadRequest request);
    Task<PastoralUnitPostsViewDto?> GetPastoralPostsAsync(Guid branchId);
    Task<PastoralUnitPostsViewDto?> UpdatePastoralPostsAsync(Guid branchId, UpdatePastoralUnitPostsRequest request);
    Task<AccessReviewDto?> GetAccessReviewAsync();
    Task<AccessReviewDto?> ConfirmAccessReviewAsync(string note);
}

public class LeadershipApiService : ILeadershipApiService
{
    private readonly HttpClient _http;
    private readonly System.Text.Json.JsonSerializerOptions _json;

    public LeadershipApiService(HttpClient http, System.Text.Json.JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<T>(_json);
    }

    public async Task<LeadershipViewDto?> GetAsync()
        => await ReadAsync<LeadershipViewDto>(await _http.GetAsync("api/v1/leadership"));

    public async Task<LeadershipViewDto?> UpdateSafeguardingAsync(UpdateSafeguardingLeadsRequest request)
        => await ReadAsync<LeadershipViewDto>(await _http.PutAsJsonAsync("api/v1/leadership/safeguarding", request, _json));

    public async Task<LeadershipViewDto?> UpdateActingHeadAsync(UpdateActingHeadRequest request)
        => await ReadAsync<LeadershipViewDto>(await _http.PutAsJsonAsync("api/v1/leadership/acting-head", request, _json));

    public async Task<PastoralUnitPostsViewDto?> GetPastoralPostsAsync(Guid branchId)
        => await ReadAsync<PastoralUnitPostsViewDto>(await _http.GetAsync($"api/v1/branches/{branchId}/pastoral-posts"));

    public async Task<PastoralUnitPostsViewDto?> UpdatePastoralPostsAsync(Guid branchId, UpdatePastoralUnitPostsRequest request)
        => await ReadAsync<PastoralUnitPostsViewDto>(await _http.PutAsJsonAsync($"api/v1/branches/{branchId}/pastoral-posts", request, _json));

    public async Task<AccessReviewDto?> GetAccessReviewAsync()
        => await ReadAsync<AccessReviewDto>(await _http.GetAsync("api/v1/access-review"));

    public async Task<AccessReviewDto?> ConfirmAccessReviewAsync(string note)
        => await ReadAsync<AccessReviewDto>(await _http.PostAsJsonAsync("api/v1/access-review/confirm", new ConfirmAccessReviewRequest(note), _json));
}
