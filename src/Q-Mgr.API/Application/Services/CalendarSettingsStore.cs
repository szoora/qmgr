using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Platform;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// The organization's calendar settings, <c>Organization.Settings["Calendar"]</c> — the ONE reader and the one
/// normaliser (plan TERM_PROGRAMME_CALENDAR_AND_GATES, 2026-09-23). Written only through
/// <see cref="OrganizationSettingsLock.MutateAsync"/>, so a save here can never drop another writer's key.
/// </summary>
public static class CalendarSettingsStore
{
    public const string Key = "Calendar";
    public const int MaxCategories = 30;
    public const int MaxCategoryLength = 60;

    /// <summary>The settings out of an organization's settings blob; the defaults when the key is absent or unreadable.</summary>
    public static CalendarSettingsDto Read(string? organizationSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(organizationSettingsJson)) return new CalendarSettingsDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(organizationSettingsJson);
            if (root != null && root.TryGetValue(Key, out var element) && element.ValueKind == JsonValueKind.Object)
            {
                var stored = JsonSerializer.Deserialize<CalendarSettingsDto>(element.GetRawText()) ?? new CalendarSettingsDto();
                var (clean, _) = Normalize(stored);
                return clean ?? new CalendarSettingsDto();
            }
        }
        catch (JsonException) { /* a malformed blob reads as the defaults */ }
        return new CalendarSettingsDto();
    }

    public static async Task<CalendarSettingsDto> GetAsync(QMgrDbContext db, Guid organizationId, CancellationToken ct = default)
    {
        var json = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => o.Settings).FirstOrDefaultAsync(ct);
        return Read(json);
    }

    /// <summary>
    /// Trimmed, de-duplicated (case-insensitively, first spelling kept) and bounded. Returns the clean copy, or the
    /// sentence a form shows when it cannot be made valid.
    /// </summary>
    public static (CalendarSettingsDto? Clean, string? Error) Normalize(CalendarSettingsDto input)
    {
        var categories = new List<string>();
        foreach (var raw in input.Categories ?? new List<string>())
        {
            var c = (raw ?? string.Empty).Trim();
            if (c.Length == 0) continue;
            if (c.Length > MaxCategoryLength) return (null, $"A category cannot exceed {MaxCategoryLength} characters (\"{c[..20]}…\").");
            if (categories.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) continue;
            categories.Add(c);
        }
        if (categories.Count == 0) return (null, "Keep at least one category.");
        if (categories.Count > MaxCategories) return (null, $"At most {MaxCategories} categories.");

        if (input.PeoplePerRotaDay is < 1 or > 20) return (null, "People on duty per day must be between 1 and 20.");

        var perDay = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in input.PeoplePerRotaDayOn ?? new Dictionary<string, int>())
        {
            var key = (k ?? string.Empty).Trim();
            if (key.Length == 0) continue;
            if (key.Length > 40) return (null, "A named day cannot exceed 40 characters.");
            if (v is < 1 or > 20) return (null, $"People on duty on \"{key}\" must be between 1 and 20.");
            perDay[key] = v;
        }
        if (perDay.Count > 60) return (null, "At most 60 named days.");

        return (new CalendarSettingsDto
        {
            Categories = categories,
            PeoplePerRotaDay = input.PeoplePerRotaDay,
            PeoplePerRotaDayOn = new Dictionary<string, int>(perDay)
        }, null);
    }

    /// <summary>The configured spelling of a category, or null when it is not one of them.</summary>
    public static string? MatchCategory(CalendarSettingsDto settings, string? category)
        => string.IsNullOrWhiteSpace(category) ? null
            : settings.Categories.FirstOrDefault(c => string.Equals(c, category.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The national calendar (decision D9): Ministry terms, UNEB examinations and public holidays, kept yearly by the
/// PLATFORM administrator in the <c>PlatformSettings</c> row of category <see cref="Category"/>, and read by every
/// tenant as reference — a warning, never a rule. Read straight from its row rather than the settings service's
/// cache, so a platform edit is visible on the next request, and created on first save because
/// <c>InitializeDefaultSettingsAsync</c> never runs again on an install that already has settings.
/// The generic Platform Settings editor refuses this category: it has its own validated endpoint.
/// </summary>
public static class NationalCalendarStore
{
    public const string Category = "NationalCalendar";
    public const int MaxEntries = 400;

    public static async Task<NationalCalendarDto> GetAsync(QMgrDbContext db, CancellationToken ct = default)
    {
        var row = await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Category == Category, ct);
        var dto = row?.GetSettings<NationalCalendarDto>() ?? new NationalCalendarDto();
        dto.Entries ??= new();
        dto.Entries = dto.Entries.OrderBy(e => e.StartsOn).ThenBy(e => e.Title).ToList();
        return dto;
    }

    public static IEnumerable<NationalCalendarEntryDto> Overlapping(NationalCalendarDto calendar, DateOnly from, DateOnly to)
        => calendar.Entries.Where(e => e.StartsOn <= to && e.EndsOn >= from);

    public static (NationalCalendarDto? Clean, string? Error) Normalize(NationalCalendarDto input)
    {
        var entries = input.Entries ?? new();
        if (entries.Count > MaxEntries) return (null, $"At most {MaxEntries} entries.");
        var clean = new List<NationalCalendarEntryDto>();
        foreach (var e in entries)
        {
            var title = (e.Title ?? string.Empty).Trim();
            if (title.Length == 0) return (null, "Every entry needs a title.");
            if (title.Length > 200) return (null, "A title cannot exceed 200 characters.");
            if (e.EndsOn < e.StartsOn) return (null, $"\"{title}\" ends before it starts.");
            var kind = string.IsNullOrWhiteSpace(e.Kind) ? null : e.Kind.Trim();
            if (kind is { Length: > 60 }) return (null, $"The kind of \"{title}\" cannot exceed 60 characters.");
            var source = string.IsNullOrWhiteSpace(e.Source) ? null : e.Source.Trim();
            if (source is { Length: > 300 }) return (null, $"The source of \"{title}\" cannot exceed 300 characters.");
            clean.Add(new NationalCalendarEntryDto { Title = title, StartsOn = e.StartsOn, EndsOn = e.EndsOn, Kind = kind, Source = source });
        }
        return (new NationalCalendarDto { Entries = clean.OrderBy(x => x.StartsOn).ThenBy(x => x.Title).ToList() }, null);
    }

    public static async Task SaveAsync(QMgrDbContext db, NationalCalendarDto clean, Guid? byUserId, CancellationToken ct = default)
    {
        var row = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Category == Category, ct);
        if (row == null)
        {
            row = new PlatformSetting
            {
                Category = Category,
                DisplayName = "National calendar",
                Description = "Ministry terms, UNEB examinations and public holidays — reference for every school's calendar.",
                IsEnabled = true,
                IsEditable = true,
                DisplayOrder = 90,
                Icon = "bi-calendar3",
                CreatedBy = byUserId
            };
            db.PlatformSettings.Add(row);
        }
        row.SetSettings(clean);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = byUserId;
        await db.SaveChangesAsync(ct);
    }
}
