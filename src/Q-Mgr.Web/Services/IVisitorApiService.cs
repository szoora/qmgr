using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Web.Services;

public interface IVisitorApiService
{
    Task<List<VisitorDto>> GetVisitorsAsync(Guid branchId, VisitorStatus? status = null, bool watchlistOnly = false);
    Task<VisitorSummaryDto?> GetSummaryAsync(Guid branchId);

    /// <summary>Returning-visitor typeahead by name/phone/email/ID — debounce calls client-side.</summary>
    Task<List<VisitorProfileSearchResultDto>> SearchVisitorProfilesAsync(Guid branchId, string query);

    /// <summary>Throws InvalidOperationException with the API's ProblemDetails.Title on failure (e.g. an active-visit conflict) — callers show it directly.</summary>
    Task<VisitorDto> PreRegisterAsync(Guid branchId, PreRegisterVisitorRequest request);
    Task<VisitorDto> CheckInAsync(Guid branchId, CheckInVisitorRequest request);
    Task<VisitorDto> CheckInExistingAsync(Guid branchId, Guid visitorId, CheckInVisitorRequest? request = null);
    Task<VisitorDto?> CheckOutAsync(Guid branchId, Guid visitorId);
    Task<VisitorDto?> ReissueBadgeTokenAsync(Guid branchId, Guid visitorId);

    /// <summary>Uploads a check-in headshot (JPEG bytes) and returns its stored URL, or null on failure.</summary>
    Task<string?> UploadPhotoAsync(Guid branchId, byte[] jpegBytes);
    Task<VisitorDto?> UpdateVisitorAsync(Guid branchId, Guid visitorId, UpdateVisitorRequest request);
    Task<VisitorDto?> SetWatchlistAsync(Guid branchId, Guid visitorId, bool isWatchlisted, string? reason);

    /// <summary>Flags/unflags a guardian's card by profile id directly — used to flag a card BEFORE
    /// today's visit row exists yet (the "Flag &amp; Check In" path past the repeat-check-in gate).
    /// Throws InvalidOperationException with the API's ProblemDetails.Title on failure.</summary>
    Task SetProfileWatchlistAsync(Guid branchId, Guid profileId, bool isWatchlisted, string reason);
    Task<bool> DeleteVisitorAsync(Guid branchId, Guid visitorId, string reason);

    Task<List<VisitorPassDto>> GetPassesAsync(Guid branchId);
    Task<VisitorPassDto> CreatePassAsync(Guid branchId, CreateVisitorPassRequest request);
    Task<VisitorPassDto?> RevokePassAsync(Guid branchId, Guid passId);
    Task<VisitorScanResultDto> ScanAsync(Guid branchId, string token, string? direction = null);

    Task<VisitorConsentSettingsDto> GetConsentSettingsAsync(Guid branchId);
    Task<VisitorConsentSettingsDto?> UpdateConsentSettingsAsync(Guid branchId, VisitorConsentSettingsDto settings);

    Task<VisitorRetentionSettingsDto> GetRetentionSettingsAsync(Guid organizationId);
    Task<VisitorRetentionSettingsDto?> UpdateRetentionSettingsAsync(Guid organizationId, VisitorRetentionSettingsDto settings);

    Task<List<DeletedVisitorDto>> GetDeletedVisitorsAsync(Guid branchId);

    Task<VisitingDaySettingsDto> GetVisitingDaySettingsAsync(Guid branchId);
    Task<VisitingDaySettingsDto?> UpdateVisitingDaySettingsAsync(Guid branchId, VisitingDaySettingsDto settings);

    Task<VisitorReportDto?> GetVisitorReportAsync(Guid branchId, DateOnly from, DateOnly to);

    /// <summary>Returns the raw CSV text (or null on failure) for the same range GetVisitorReportAsync summarizes.</summary>
    Task<string?> ExportVisitorReportCsvAsync(Guid branchId, DateOnly from, DateOnly to);

    /// <summary>Point-in-time evacuation roll call — everyone on site right now. Null on failure.</summary>
    Task<EvacuationReportDto?> GetEvacuationReportAsync(Guid branchId);

    /// <summary>Books a party of expected arrivals. Throws InvalidOperationException with the API's ProblemDetails.Title on failure.</summary>
    Task<List<VisitorDto>> CreateExpectedVisitorsAsync(Guid branchId, CreateExpectedVisitorsRequest request);

    /// <summary>Expected arrivals over a date range (server defaults to today through a week out).</summary>
    Task<List<VisitorDto>> GetExpectedVisitorsAsync(Guid branchId, DateOnly? from = null, DateOnly? to = null);

    Task<VisitorDto?> CancelExpectedVisitorAsync(Guid branchId, Guid visitorId);

    /// <summary>Records or clears a person's contractor site induction (null completedAt clears it).
    /// Throws InvalidOperationException with the API's ProblemDetails.Title on failure.</summary>
    Task SetInductionAsync(Guid branchId, Guid profileId, DateTime? completedAt, string? notes);
}

public class VisitorApiService : IVisitorApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VisitorApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public VisitorApiService(HttpClient httpClient, ILogger<VisitorApiService> logger, JsonSerializerOptions jsonOptions)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public async Task<List<VisitorDto>> GetVisitorsAsync(Guid branchId, VisitorStatus? status = null, bool watchlistOnly = false)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/visitors?watchlistOnly={watchlistOnly}";
            if (status.HasValue) url += $"&status={status}";
            return await _httpClient.GetFromJsonAsync<List<VisitorDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitors for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<VisitorSummaryDto?> GetSummaryAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<VisitorSummaryDto>($"api/v1/branches/{branchId}/visitors/summary", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitor summary for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<List<VisitorProfileSearchResultDto>> SearchVisitorProfilesAsync(Guid branchId, string query)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/visitors/search?q={Uri.EscapeDataString(query)}";
            return await _httpClient.GetFromJsonAsync<List<VisitorProfileSearchResultDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search visitor profiles for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<VisitorDto> PreRegisterAsync(Guid branchId, PreRegisterVisitorRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitors/pre-register", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions))!;
    }

    public async Task<VisitorDto> CheckInAsync(Guid branchId, CheckInVisitorRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitors/checkin", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions))!;
    }

    public async Task<VisitorDto> CheckInExistingAsync(Guid branchId, Guid visitorId, CheckInVisitorRequest? request = null)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitors/{visitorId}/checkin", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions))!;
    }

    public async Task<VisitorDto?> CheckOutAsync(Guid branchId, Guid visitorId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/branches/{branchId}/visitors/{visitorId}/checkout", null);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check out visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task<string?> UploadPhotoAsync(Guid branchId, byte[] jpegBytes)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            using var byteContent = new ByteArrayContent(jpegBytes);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(byteContent, "file", "checkin-photo.jpg");

            var response = await _httpClient.PostAsync($"api/v1/branches/{branchId}/visitors/photo", content);
            if (!response.IsSuccessStatusCode) return null;
            var url = await response.Content.ReadAsStringAsync();
            return url.Trim('"');
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload visitor check-in photo");
            return null;
        }
    }

    public async Task<VisitorDto?> ReissueBadgeTokenAsync(Guid branchId, Guid visitorId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/branches/{branchId}/visitors/{visitorId}/badge-token", null);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reissue badge token for visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task<VisitorDto?> UpdateVisitorAsync(Guid branchId, Guid visitorId, UpdateVisitorRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/visitors/{visitorId}", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task<VisitorDto?> SetWatchlistAsync(Guid branchId, Guid visitorId, bool isWatchlisted, string? reason)
    {
        try
        {
            var request = new SetWatchlistRequest { IsWatchlisted = isWatchlisted, Reason = reason };
            var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/visitors/{visitorId}/watchlist", request, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set watchlist for visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task SetProfileWatchlistAsync(Guid branchId, Guid profileId, bool isWatchlisted, string reason)
    {
        var request = new SetWatchlistRequest { IsWatchlisted = isWatchlisted, Reason = reason };
        var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/visitor-profiles/{profileId}/watchlist", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }

    public async Task<bool> DeleteVisitorAsync(Guid branchId, Guid visitorId, string reason)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/branches/{branchId}/visitors/{visitorId}")
            {
                Content = JsonContent.Create(new DeleteVisitorRequest { Reason = reason }, options: _jsonOptions)
            };
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete visitor {VisitorId}", visitorId);
            return false;
        }
    }

    public async Task<List<VisitorPassDto>> GetPassesAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<VisitorPassDto>>($"api/v1/branches/{branchId}/visitor-passes", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitor passes for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<VisitorPassDto> CreatePassAsync(Guid branchId, CreateVisitorPassRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitor-passes", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<VisitorPassDto>(_jsonOptions))!;
    }

    public async Task<VisitorPassDto?> RevokePassAsync(Guid branchId, Guid passId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/branches/{branchId}/visitor-passes/{passId}/revoke", null);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorPassDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke visitor pass {PassId}", passId);
            return null;
        }
    }

    public async Task<VisitorScanResultDto> ScanAsync(Guid branchId, string token, string? direction = null)
    {
        var request = new VisitorScanRequest { Token = token, Direction = direction };
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitors/scan", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<VisitorScanResultDto>(_jsonOptions))!;
    }

    public async Task<VisitorConsentSettingsDto> GetConsentSettingsAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<VisitorConsentSettingsDto>($"api/v1/branches/{branchId}/visitors/consent-settings", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitor consent settings for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<VisitorConsentSettingsDto?> UpdateConsentSettingsAsync(Guid branchId, VisitorConsentSettingsDto settings)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/visitors/consent-settings", settings, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorConsentSettingsDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update visitor consent settings for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<VisitorRetentionSettingsDto> GetRetentionSettingsAsync(Guid organizationId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<VisitorRetentionSettingsDto>($"api/v1/organizations/{organizationId}/visitors/retention-settings", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitor retention settings for organization {OrganizationId}", organizationId);
            return new();
        }
    }

    public async Task<VisitorRetentionSettingsDto?> UpdateRetentionSettingsAsync(Guid organizationId, VisitorRetentionSettingsDto settings)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/organizations/{organizationId}/visitors/retention-settings", settings, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorRetentionSettingsDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update visitor retention settings for organization {OrganizationId}", organizationId);
            return null;
        }
    }

    public async Task<List<DeletedVisitorDto>> GetDeletedVisitorsAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<DeletedVisitorDto>>($"api/v1/branches/{branchId}/visitors/deleted", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get deleted visitors for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<VisitingDaySettingsDto> GetVisitingDaySettingsAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<VisitingDaySettingsDto>($"api/v1/branches/{branchId}/visitors/visiting-day-settings", _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visiting-day settings for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<VisitingDaySettingsDto?> UpdateVisitingDaySettingsAsync(Guid branchId, VisitingDaySettingsDto settings)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/visitors/visiting-day-settings", settings, _jsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitingDaySettingsDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update visiting-day settings for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<VisitorReportDto?> GetVisitorReportAsync(Guid branchId, DateOnly from, DateOnly to)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/visitors/report?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            return await _httpClient.GetFromJsonAsync<VisitorReportDto>(url, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get visitor report for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<string?> ExportVisitorReportCsvAsync(Guid branchId, DateOnly from, DateOnly to)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/visitors/report/export?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export visitor report for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<EvacuationReportDto?> GetEvacuationReportAsync(Guid branchId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<EvacuationReportDto>($"api/v1/branches/{branchId}/visitors/evacuation", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get the evacuation roll call for branch {BranchId}", branchId);
            return null;
        }
    }

    public async Task<List<VisitorDto>> CreateExpectedVisitorsAsync(Guid branchId, CreateExpectedVisitorsRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/v1/branches/{branchId}/visitors/expected", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<List<VisitorDto>>(_jsonOptions)) ?? new();
    }

    public async Task<List<VisitorDto>> GetExpectedVisitorsAsync(Guid branchId, DateOnly? from = null, DateOnly? to = null)
    {
        try
        {
            var url = $"api/v1/branches/{branchId}/visitors/expected";
            var query = new List<string>();
            if (from.HasValue) query.Add($"from={from.Value:yyyy-MM-dd}");
            if (to.HasValue) query.Add($"to={to.Value:yyyy-MM-dd}");
            if (query.Count > 0) url += "?" + string.Join("&", query);

            return await _httpClient.GetFromJsonAsync<List<VisitorDto>>(url, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get expected visitors for branch {BranchId}", branchId);
            return new();
        }
    }

    public async Task<VisitorDto?> CancelExpectedVisitorAsync(Guid branchId, Guid visitorId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/v1/branches/{branchId}/visitors/{visitorId}/cancel", null);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VisitorDto>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel expected visitor {VisitorId}", visitorId);
            return null;
        }
    }

    public async Task SetInductionAsync(Guid branchId, Guid profileId, DateTime? completedAt, string? notes)
    {
        var request = new SetInductionRequest { CompletedAt = completedAt, Notes = notes };
        var response = await _httpClient.PutAsJsonAsync($"api/v1/branches/{branchId}/visitor-profiles/{profileId}/induction", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
    }
}
