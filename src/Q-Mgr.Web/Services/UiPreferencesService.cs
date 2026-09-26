using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using QMgr.Application;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// The signed-in person's own view choices — the calendar's view, scope and past-events switch, and the notification
/// sound (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E1, E4, E7, E8). The server copy (<c>Users.UiPreferences</c>) follows
/// the person to their phone; a localStorage copy lets the calendar open on the right view before the server answers.
/// "Mute on this device" is the one choice that belongs to the DEVICE (a shared office PC), so it lives only in the
/// browser.
///
/// Scoped: one per circuit, so every page reads one copy. Reads never throw — a preference that cannot be read falls back
/// to the defaults, which are right for somebody who has never opened a settings page. Writes throw, so a settings page
/// can say it did not save.
/// </summary>
public interface IUiPreferencesService
{
    UserUiPreferencesDto Current { get; }
    QuietHoursDto? QuietHours { get; }
    string? TimeZone { get; }

    /// <summary>Loads once per circuit (the browser copy first, then the server's). Safe to call from every page.</summary>
    Task<UserUiPreferencesDto> LoadAsync(Guid? branchId = null);

    /// <summary>Saves a changed copy. <paramref name="quiet"/> = a view choice made in passing: failures are swallowed.</summary>
    Task SaveAsync(UserUiPreferencesDto preferences, bool quiet = false);

    Task<bool> IsMutedOnThisDeviceAsync();
    Task SetMutedOnThisDeviceAsync(bool muted);

    /// <summary>Should a notification of this priority chime right now? The person's choice, the device, the school's quiet hours.</summary>
    Task<bool> ShouldChimeAsync(string? priority);

    event Action? Changed;
}

public class UiPreferencesService : IUiPreferencesService
{
    private const string LocalKey = "qmgr-ui-preferences";
    private const string MutedKey = "qmgr-sound-muted";

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly IJSRuntime _js;
    private readonly ILogger<UiPreferencesService> _logger;
    private Task<UserUiPreferencesDto>? _loading;
    private bool? _muted;

    public UiPreferencesService(HttpClient http, JsonSerializerOptions json, IJSRuntime js, ILogger<UiPreferencesService> logger)
    {
        _http = http;
        _json = json;
        _js = js;
        _logger = logger;
    }

    public UserUiPreferencesDto Current { get; private set; } = new();
    public QuietHoursDto? QuietHours { get; private set; }
    public string? TimeZone { get; private set; }
    public event Action? Changed;

    public Task<UserUiPreferencesDto> LoadAsync(Guid? branchId = null) => _loading ??= LoadCoreAsync(branchId);

    private async Task<UserUiPreferencesDto> LoadCoreAsync(Guid? branchId)
    {
        try
        {
            var local = await _js.InvokeAsync<string?>("localStorage.getItem", LocalKey);
            if (!string.IsNullOrWhiteSpace(local) && JsonSerializer.Deserialize<UserUiPreferencesDto>(local, _json) is { } cached) Current = cached;
        }
        catch { /* storage unavailable or prerendering: the server copy decides */ }

        try
        {
            var response = await _http.GetAsync($"api/v1/profile/ui-preferences{(branchId is { } b && b != Guid.Empty ? $"?branchId={b}" : "")}");
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<UserUiPreferencesEnvelopeDto>(_json) is { } envelope)
            {
                Current = envelope.Preferences ?? new UserUiPreferencesDto();
                QuietHours = envelope.QuietHours;
                TimeZone = envelope.TimeZone;
                await MirrorAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UI preferences could not be read; defaults stand");
            _loading = null; // try again next time
        }
        return Current;
    }

    public async Task SaveAsync(UserUiPreferencesDto preferences, bool quiet = false)
    {
        Current = preferences;
        await MirrorAsync();
        Changed?.Invoke();
        try
        {
            var response = await _http.PutAsJsonAsync("api/v1/profile/ui-preferences", preferences, _json);
            if (!response.IsSuccessStatusCode)
            {
                if (quiet) return;
                await ApiFieldException.ThrowAsync(response);
            }
        }
        catch (Exception ex) when (quiet)
        {
            _logger.LogDebug(ex, "A view choice was not saved to the server; it is kept in this browser");
        }
    }

    private async Task MirrorAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", LocalKey, JsonSerializer.Serialize(Current, _json)); }
        catch { /* a private window: the server copy still holds it */ }
    }

    public async Task<bool> IsMutedOnThisDeviceAsync()
    {
        if (_muted is { } known) return known;
        try { _muted = await _js.InvokeAsync<string?>("localStorage.getItem", MutedKey) == "1"; }
        catch { _muted = false; }
        return _muted.Value;
    }

    public async Task SetMutedOnThisDeviceAsync(bool muted)
    {
        _muted = muted;
        try
        {
            if (muted) await _js.InvokeVoidAsync("localStorage.setItem", MutedKey, "1");
            else await _js.InvokeVoidAsync("localStorage.removeItem", MutedKey);
        }
        catch { /* nothing stored, nothing lost: it holds for this circuit */ }
        Changed?.Invoke();
    }

    public async Task<bool> ShouldChimeAsync(string? priority)
    {
        await LoadAsync();
        var sound = Current.Sound ?? new NotificationSoundPreferencesDto();
        if (!sound.Enabled) return false;
        if (await IsMutedOnThisDeviceAsync()) return false;
        var important = priority is "High" or "Urgent";
        if (priority == "Low") return false;
        if (sound.ImportantOnly && !important) return false;
        // The school's quiet hours, on the school's clock. An urgent notice still chimes: that is what urgent means.
        if (!important && QuietHours is { Enabled: true } q)
        {
            var hour = BranchClock.Now(BranchClock.Resolve(TimeZone)).Hour;
            if (BranchClock.InQuietHours(hour, q.StartHour, q.EndHour)) return false;
        }
        return true;
    }
}
