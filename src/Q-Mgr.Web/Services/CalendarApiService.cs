using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// The school calendar, its settings, the personal feed and the national calendar
/// (plan TERM_PROGRAMME_CALENDAR_AND_GATES §9).
///
/// EVERY CALL THROWS on failure, like every other API service in this project except
/// IQueueApiService. A write throws <see cref="ApiFieldException"/> (an
/// <see cref="InvalidOperationException"/>) so a form can mark the field the server refused; a
/// caller that only wants the message catches the base type. The one read that does not throw is
/// the anonymous signage list, which answers null: a wall screen has nobody to read an
/// error to, and the next refresh tries again.
/// </summary>
public interface ICalendarApiService
{
    Task<CalendarRangeDto> GetRangeAsync(Guid branchId, DateOnly from, DateOnly to);
    Task<SchoolEventDto> GetEventAsync(Guid branchId, Guid id);
    Task<SchoolEventDto> CreateEventAsync(Guid branchId, SaveSchoolEventRequest request);
    Task<SchoolEventDto> UpdateEventAsync(Guid branchId, Guid id, SaveSchoolEventRequest request);
    Task DeleteEventAsync(Guid branchId, Guid id);

    Task<CalendarSettingsDto> GetSettingsAsync();
    Task<CalendarSettingsDto> UpdateSettingsAsync(CalendarSettingsDto settings);

    /// <summary>Whether I have a feed. <see cref="CalendarFeedDto.Url"/> is never set here.</summary>
    Task<CalendarFeedDto> GetFeedAsync();
    /// <summary>Creates or replaces my feed; the answer carries the URL, ONCE.</summary>
    Task<CalendarFeedDto> CreateFeedAsync();
    Task DeleteFeedAsync();

    Task<NationalCalendarDto> GetNationalAsync(DateOnly from, DateOnly to);
    Task<NationalCalendarDto> GetPlatformNationalAsync();
    Task<NationalCalendarDto> UpdatePlatformNationalAsync(NationalCalendarDto calendar);

    /// <summary>Anonymous: a branch's upcoming PUBLIC events, for the signage "Coming up" zone. Never throws: null means the read failed, so a screen can keep what it showed rather than blank itself.</summary>
    Task<List<SchoolEventDto>?> GetUpcomingPublicAsync(Guid branchId, int days = 14, int take = 8);
}

public class CalendarApiService : ICalendarApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<CalendarApiService> _logger;
    // The API writes enums as strings (EventAudience "Staff, Public"); the default web options would refuse them.
    private readonly JsonSerializerOptions _json;

    public CalendarApiService(HttpClient http, JsonSerializerOptions json, ILogger<CalendarApiService> logger)
    {
        _http = http;
        _logger = logger;
        _json = json;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Branch(Guid branchId) => $"api/v1/branches/{branchId}/calendar";

    public async Task<CalendarRangeDto> GetRangeAsync(Guid branchId, DateOnly from, DateOnly to)
        => await ReadAsync<CalendarRangeDto>(await _http.GetAsync($"{Branch(branchId)}?from={Iso(from)}&to={Iso(to)}")) ?? new CalendarRangeDto { From = from, To = to };

    public async Task<SchoolEventDto> GetEventAsync(Guid branchId, Guid id)
        => await ReadAsync<SchoolEventDto>(await _http.GetAsync($"{Branch(branchId)}/events/{id}"))
           ?? throw new InvalidOperationException("The event was not returned.");

    public async Task<SchoolEventDto> CreateEventAsync(Guid branchId, SaveSchoolEventRequest request)
        => await ReadAsync<SchoolEventDto>(await _http.PostAsJsonAsync($"{Branch(branchId)}/events", request, _json))
           ?? throw new InvalidOperationException("The event was not returned.");

    public async Task<SchoolEventDto> UpdateEventAsync(Guid branchId, Guid id, SaveSchoolEventRequest request)
        => await ReadAsync<SchoolEventDto>(await _http.PutAsJsonAsync($"{Branch(branchId)}/events/{id}", request, _json))
           ?? throw new InvalidOperationException("The event was not returned.");

    public async Task DeleteEventAsync(Guid branchId, Guid id)
        => await EnsureAsync(await _http.DeleteAsync($"{Branch(branchId)}/events/{id}"));

    public async Task<CalendarSettingsDto> GetSettingsAsync()
        => await ReadAsync<CalendarSettingsDto>(await _http.GetAsync("api/v1/calendar/settings")) ?? new CalendarSettingsDto();

    public async Task<CalendarSettingsDto> UpdateSettingsAsync(CalendarSettingsDto settings)
    {
        var response = await _http.PutAsJsonAsync("api/v1/calendar/settings", settings, _json);
        await EnsureAsync(response);
        // A PUT that answers 204 still saved; read it back as sent.
        if (response.StatusCode == HttpStatusCode.NoContent) return settings;
        return await response.Content.ReadFromJsonAsync<CalendarSettingsDto>(_json) ?? settings;
    }

    public async Task<CalendarFeedDto> GetFeedAsync()
        => await ReadAsync<CalendarFeedDto>(await _http.GetAsync("api/v1/calendar/feed")) ?? new CalendarFeedDto();

    public async Task<CalendarFeedDto> CreateFeedAsync()
        => await ReadAsync<CalendarFeedDto>(await _http.PostAsync("api/v1/calendar/feed", null))
           ?? throw new InvalidOperationException("The link was not returned.");

    public async Task DeleteFeedAsync()
        => await EnsureAsync(await _http.DeleteAsync("api/v1/calendar/feed"));

    public async Task<NationalCalendarDto> GetNationalAsync(DateOnly from, DateOnly to)
        => await ReadAsync<NationalCalendarDto>(await _http.GetAsync($"api/v1/calendar/national?from={Iso(from)}&to={Iso(to)}")) ?? new NationalCalendarDto();

    public async Task<NationalCalendarDto> GetPlatformNationalAsync()
        => await ReadAsync<NationalCalendarDto>(await _http.GetAsync("api/v1/platform/national-calendar")) ?? new NationalCalendarDto();

    public async Task<NationalCalendarDto> UpdatePlatformNationalAsync(NationalCalendarDto calendar)
    {
        var response = await _http.PutAsJsonAsync("api/v1/platform/national-calendar", calendar, _json);
        await EnsureAsync(response);
        if (response.StatusCode == HttpStatusCode.NoContent) return calendar;
        return await response.Content.ReadFromJsonAsync<NationalCalendarDto>(_json) ?? calendar;
    }

    public async Task<List<SchoolEventDto>?> GetUpcomingPublicAsync(Guid branchId, int days = 14, int take = 8)
    {
        try
        {
            var response = await _http.GetAsync($"api/v1/public/branches/{branchId}/events/upcoming?days={days}&take={take}");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<List<SchoolEventDto>>(_json) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read upcoming public events for branch {BranchId}", branchId);
            return null;
        }
    }

    /// <summary>Field errors when the body carries them (the form marks the field), otherwise the API's own message.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) await ApiFieldException.ThrowAsync(response);
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response)
    {
        await EnsureAsync(response);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        return await response.Content.ReadFromJsonAsync<T>(_json);
    }
}
