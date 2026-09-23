using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// How the signed-in person's organisation writes a name (2026-09-23). The API writes nearly every
/// name itself — that is the point, so a screen, an export and an email cannot disagree — and this is
/// for the few places the Web assembles one from two halves (an import preview), and for the Settings
/// page that changes it.
/// </summary>
public interface IPeopleNamesService
{
    /// <summary>The organisation's choice, fetched once per circuit. Never throws: the default order is always a safe answer.</summary>
    Task<PeopleNameSettingsDto> GetAsync();

    /// <summary>Saves the choice. Throws InvalidOperationException carrying the API's own message.</summary>
    Task<PeopleNameSettingsDto> SaveAsync(PeopleNameSettingsDto settings);
}

public class PeopleNamesService : IPeopleNamesService
{
    private readonly HttpClient _http;
    private readonly IAuthService _auth;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<PeopleNamesService> _logger;
    private PeopleNameSettingsDto? _cached;

    public PeopleNamesService(HttpClient http, IAuthService auth, JsonSerializerOptions json, ILogger<PeopleNamesService> logger)
    {
        _http = http;
        _auth = auth;
        _json = json;
        _logger = logger;
    }

    public async Task<PeopleNameSettingsDto> GetAsync()
    {
        if (_cached != null) return _cached;
        try
        {
            var orgId = (await _auth.GetCurrentUserAsync())?.OrganizationId ?? Guid.Empty;
            if (orgId == Guid.Empty) return new PeopleNameSettingsDto();
            _cached = await _http.GetFromJsonAsync<PeopleNameSettingsDto>($"api/v1/organizations/{orgId}/people-names", _json)
                      ?? new PeopleNameSettingsDto();
            return _cached;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the organisation's name order; writing names given-first");
            return new PeopleNameSettingsDto();
        }
    }

    public async Task<PeopleNameSettingsDto> SaveAsync(PeopleNameSettingsDto settings)
    {
        var orgId = (await _auth.GetCurrentUserAsync())?.OrganizationId ?? Guid.Empty;
        if (orgId == Guid.Empty) throw new InvalidOperationException("You are not signed in to an organisation.");

        var response = await _http.PutAsJsonAsync($"api/v1/organizations/{orgId}/people-names", settings, _json);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));

        _cached = await response.Content.ReadFromJsonAsync<PeopleNameSettingsDto>(_json) ?? settings;
        return _cached;
    }
}
