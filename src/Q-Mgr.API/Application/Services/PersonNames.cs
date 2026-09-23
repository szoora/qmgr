using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// THE ONE HOME FOR HOW THE API WRITES A PERSON'S NAME (2026-09-23).
///
/// <para>A school asked why names started with the first name and where to change it. There was nowhere:
/// <c>User.FullName</c> and about seventy hand-written <c>$"{FirstName} {LastName}"</c> fixed the order in
/// code — in lists, notification text, emails, exports and error messages alike. Every one of them now
/// comes here, and here reads the organisation's own choice (<see cref="PeopleNameSettingsDto"/>).</para>
///
/// <para><b>A static face, for the same reason as UploadLinks.</b> The names are written inside static
/// mappers, EF projections and the <c>User.FullName</c> property, none of which can take a service by
/// injection. <c>Program.cs</c> attaches the cache at startup; before that (design-time, migrations) every
/// name is written given-first, which is also the default for a school that has not chosen.</para>
///
/// <para><b>What is NOT here:</b> a student's name (one whole string, never split), a visitor's (typed at a
/// desk as a whole), and a sign-up contact's on the platform review page (no organisation exists yet).</para>
/// </summary>
public static class PersonNames
{
    public const string SettingsKey = "People";

    private static PersonNameSettingsCache? _cache;

    public static void Use(PersonNameSettingsCache cache) => _cache = cache;

    /// <summary>The organisation's choice, or the default when it has made none or nothing is attached.</summary>
    public static PeopleNameSettingsDto For(Guid organizationId)
        => organizationId == Guid.Empty || _cache == null ? new PeopleNameSettingsDto() : _cache.Get(organizationId);

    /// <summary>A person's name as this organisation writes it; <paramref name="fallback"/> (a username) when both halves are blank.</summary>
    public static string Display(Guid organizationId, string? firstName, string? lastName, string? fallback = null)
    {
        var name = PersonName.Join(firstName, lastName, For(organizationId).DisplayOrder);
        return name.Length > 0 ? name : (fallback ?? string.Empty);
    }

    public static string Display(User user) => Display(user.OrganizationId, user.FirstName, user.LastName, user.Username);

    /// <summary>What a list of this organisation's people sorts on — see PeopleNameSettingsDto.SortOrder.</summary>
    public static string SortKey(Guid organizationId, string? firstName, string? lastName)
        => PersonName.SortKey(firstName, lastName, For(organizationId).EffectiveSortOrder);

    public static string SortKey(User user) => SortKey(user.OrganizationId, user.FirstName, user.LastName);

    /// <summary>True when this organisation files people by surname — for a list the SERVER sorts in SQL.</summary>
    public static bool SortsByFamilyName(Guid organizationId) => For(organizationId).EffectiveSortOrder == NameOrder.FamilyFirst;

    /// <summary>Orders a query of users the way this organisation files people. Translates to SQL.</summary>
    public static IOrderedQueryable<User> OrderByName(IQueryable<User> query, Guid organizationId)
        => SortsByFamilyName(organizationId)
            ? query.OrderBy(u => u.LastName).ThenBy(u => u.FirstName)
            : query.OrderBy(u => u.FirstName).ThenBy(u => u.LastName);

    /// <summary>
    /// Does typed text name this person, in EITHER order? A search, or an import naming a teacher,
    /// cannot know which order the person typing used — "Ayebare Agatha" and "Agatha Ayebare" are one
    /// person whatever the school's display choice is.
    /// </summary>
    public static bool Matches(string? text, string? firstName, string? lastName)
    {
        var t = PersonName.Clean(text ?? string.Empty);
        if (t.Length == 0) return false;
        return string.Equals(t, PersonName.Join(firstName, lastName, NameOrder.GivenFirst), StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, PersonName.Join(firstName, lastName, NameOrder.FamilyFirst), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ONE reader of <c>Organization.Settings["People"]</c>. A missing or unreadable key is the default.</summary>
    public static PeopleNameSettingsDto ReadSettings(string? organizationSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(organizationSettingsJson)) return new PeopleNameSettingsDto();
        try
        {
            using var doc = JsonDocument.Parse(organizationSettingsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(SettingsKey, out var people)
                && people.ValueKind == JsonValueKind.Object)
            {
                return people.Deserialize<PeopleNameSettingsDto>(SettingsJson) ?? new PeopleNameSettingsDto();
            }
        }
        catch (JsonException) { }
        return new PeopleNameSettingsDto();
    }

    /// <summary>Enums as names, so the stored blob reads "FamilyFirst" rather than 1 and survives a reordering.</summary>
    public static readonly JsonSerializerOptions SettingsJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}

/// <summary>
/// Each organisation's name settings, held in memory for five minutes and dropped the moment they are
/// saved. A singleton, and synchronous by design: it is read from a property getter and from inside EF
/// projections. A miss reads one column of one row through its own scope, so it never shares a
/// DbContext with the query whose rows it is naming.
/// </summary>
public sealed class PersonNameSettingsCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, (PeopleNameSettingsDto Settings, DateTime LoadedAt)> _entries = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PersonNameSettingsCache> _logger;

    public PersonNameSettingsCache(IServiceScopeFactory scopes, ILogger<PersonNameSettingsCache> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public PeopleNameSettingsDto Get(Guid organizationId)
    {
        if (_entries.TryGetValue(organizationId, out var hit) && DateTime.UtcNow - hit.LoadedAt < Lifetime)
            return hit.Settings;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QMgrDbContext>();
            var json = db.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Id == organizationId)
                .Select(o => o.Settings)
                .FirstOrDefault();
            var settings = PersonNames.ReadSettings(json);
            _entries[organizationId] = (settings, DateTime.UtcNow);
            return settings;
        }
        catch (Exception ex)
        {
            // A name written in the default order is a far smaller harm than a list that fails to load.
            _logger.LogWarning(ex, "Could not read name settings for organization {OrganizationId}; using the default order", organizationId);
            return hit.Settings ?? new PeopleNameSettingsDto();
        }
    }

    /// <summary>Called by the endpoint that saves the settings, so the next name written follows at once.</summary>
    public void Forget(Guid organizationId) => _entries.TryRemove(organizationId, out _);
}
