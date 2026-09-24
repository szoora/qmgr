using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// THE ONE READER AND WRITER of <c>Organization.Settings["Leadership"]</c> (2026-09-24): the designated
/// safeguarding lead and deputies, an acting head for a fixed period, and house and dormitory posts.
///
/// <para>These are POSTS in the <see cref="PostPermissionService"/> sense: they grant permissions on top of a
/// role, and those permissions carry their own reach. <see cref="PostPermissionService.GrantsForAsync"/> reads
/// this; nothing else parses the key. Writes go through <see cref="OrganizationSettingsLock.MutateAsync"/>, the
/// one writer of the column, or a save of this key would silently drop another.</para>
///
/// <para><b>Every write must call <c>IStaffProfileChangeNotifier.PostChangedAsync</c> for the people on BOTH
/// sides</b> — the permission cache holds a set for five minutes, and the person REMOVED is the one who would
/// otherwise keep access.</para>
/// </summary>
public static class LeadershipPosts
{
    public const string SettingsKey = "Leadership";

    /// <summary>What the safeguarding lead and each deputy hold. Whole school on the student axis: that is the job.</summary>
    public static readonly string[] SafeguardingLeadPost =
    {
        Permissions.StudentsView,
        Permissions.WelfareView,
        Permissions.WelfareCreate,
        Permissions.WelfareEdit,
        Permissions.WelfareNotify,
        Permissions.WelfareConfidentialView,
        Permissions.WelfareRestrictedView,
        Permissions.WelfareReportsView,
    };

    /// <summary>What an acting head holds while the period covers today: exactly the Head Teacher's set.</summary>
    public static string[] ActingHeadPost() => Permissions.HeadTeacherPermissions(Permissions.All.Select(p => (p.Code, p.IsVisible)));

    /// <summary>An acting-head period may run at most this long. A longer absence is a role change, made openly.</summary>
    public const int MaxActingHeadDays = 120;

    public const int MaxDeputySafeguardingLeads = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static LeadershipPostsDto Read(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return new LeadershipPostsDto();
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (!doc.RootElement.TryGetProperty(SettingsKey, out var el) || el.ValueKind != JsonValueKind.Object)
                return new LeadershipPostsDto();
            return el.Deserialize<LeadershipPostsDto>(Json) ?? new LeadershipPostsDto();
        }
        catch (JsonException)
        {
            return new LeadershipPostsDto();
        }
    }

    public static async Task<LeadershipPostsDto> ReadAsync(QMgrDbContext db, Guid organizationId, CancellationToken ct = default)
    {
        var json = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => o.Settings).FirstOrDefaultAsync(ct);
        return Read(json);
    }

    /// <summary>Replaces the key under the organization lock. <paramref name="change"/> receives the value read UNDER
    /// the lock and returns the value to store — never a copy read before the lock, or a concurrent save is lost.</summary>
    public static async Task<LeadershipPostsDto> WriteAsync(QMgrDbContext db, Guid organizationId,
        Func<LeadershipPostsDto, LeadershipPostsDto> change, CancellationToken ct = default)
    {
        LeadershipPostsDto result = new();
        await OrganizationSettingsLock.MutateAsync(db, organizationId, org =>
        {
            var current = Read(org.Settings);
            result = change(current);
            org.Settings = OrganizationSettingsLock.WithKey(org.Settings, SettingsKey,
                JsonSerializer.SerializeToElement(result, Json));
            return true;
        }, ct);
        return result;
    }

    /// <summary>Everybody named anywhere in <paramref name="posts"/>, for cache invalidation.</summary>
    public static IEnumerable<Guid> People(LeadershipPostsDto posts)
    {
        if (posts.SafeguardingLeadUserId is { } l) yield return l;
        foreach (var d in posts.DeputySafeguardingLeadUserIds) yield return d;
        if (posts.ActingHead is { } a) yield return a.UserId;
        foreach (var p in posts.PastoralUnitPosts) yield return p.UserId;
    }

    public static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>The same document with <paramref name="userId"/> removed from every post — what the leaver sweep writes.</summary>
    public static LeadershipPostsDto Without(LeadershipPostsDto posts, Guid userId) => posts with
    {
        SafeguardingLeadUserId = posts.SafeguardingLeadUserId == userId ? null : posts.SafeguardingLeadUserId,
        DeputySafeguardingLeadUserIds = posts.DeputySafeguardingLeadUserIds.Where(d => d != userId).ToList(),
        ActingHead = posts.ActingHead?.UserId == userId ? null : posts.ActingHead,
        PastoralUnitPosts = posts.PastoralUnitPosts.Where(p => p.UserId != userId).ToList(),
    };
}
