using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// THE ONE HOME for reading and writing <c>Users.UiPreferences</c> (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E8,
/// 2026-09-26): the calendar's view and scope, whether past events show, the notification sound. Same shape as
/// <c>INotificationPreferenceResolver</c>, and for the same reason — a second parser of a jsonb column is how the stored
/// shape and the reading of it drift apart.
///
/// A save COPIES the record (<c>with</c>) and only tidies known values, so a property added later survives a save made
/// by a page that has not heard of it — the lesson of the push opt-out that the notification blob lost every week.
/// </summary>
public interface IUserPreferencesService
{
    Task<UserUiPreferencesDto> GetAsync(Guid userId, CancellationToken ct = default);
    Task<UserUiPreferencesDto> SaveAsync(Guid userId, UserUiPreferencesDto preferences, CancellationToken ct = default);

    /// <summary>Defaults for null or malformed JSON — a corrupt blob must never stop a page loading.</summary>
    static UserUiPreferencesDto Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new UserUiPreferencesDto();
        try
        {
            return Normalize(JsonSerializer.Deserialize<UserUiPreferencesDto>(json) ?? new UserUiPreferencesDto());
        }
        catch (JsonException)
        {
            return new UserUiPreferencesDto();
        }
    }

    static UserUiPreferencesDto Normalize(UserUiPreferencesDto p)
    {
        var calendar = p.Calendar ?? new CalendarViewPreferencesDto();
        var view = calendar.View is "term" or "month" or "agenda" ? calendar.View : "term";
        return p with
        {
            Calendar = calendar with { View = view, Scope = CalendarScopes.Normalize(calendar.Scope) },
            Sound = p.Sound ?? new NotificationSoundPreferencesDto()
        };
    }
}

public class UserPreferencesService : IUserPreferencesService
{
    private readonly QMgrDbContext _db;

    public UserPreferencesService(QMgrDbContext db) => _db = db;

    public async Task<UserUiPreferencesDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var raw = await _db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == userId).Select(u => u.UiPreferences).FirstOrDefaultAsync(ct);
        return IUserPreferencesService.Parse(raw);
    }

    public async Task<UserUiPreferencesDto> SaveAsync(Guid userId, UserUiPreferencesDto preferences, CancellationToken ct = default)
    {
        var clean = IUserPreferencesService.Normalize(preferences);
        var json = JsonSerializer.Serialize(clean);
        // One statement, no read-modify-write of the user row: a preference save must never race a profile edit.
        await _db.Users.IgnoreQueryFilters().Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.UiPreferences, json), ct);
        return clean;
    }
}
