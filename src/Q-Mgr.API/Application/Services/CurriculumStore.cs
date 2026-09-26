using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// The school's curriculum list (lesson plans plan §5.3, decision L9): each subject's topics per class level and term,
/// with periods, competency and outcomes — NCDC's programme planner, entered or imported from CSV. Optional: without it
/// everything still works, with more typing.
///
/// Stored in <c>Organization.Settings["Curriculum"]</c>; this class is its ONE reader and writer, and writes go through
/// <see cref="OrganizationSettingsLock.MutateAsync"/> like every other key of that column.
/// </summary>
public static class CurriculumStore
{
    public const string Key = "Curriculum";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<CurriculumDto> ReadAsync(QMgrDbContext db, Guid organizationId, CancellationToken ct = default)
    {
        var settings = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => o.Settings).FirstOrDefaultAsync(ct);
        return Parse(settings);
    }

    public static CurriculumDto Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return new();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson);
            if (root != null && root.TryGetValue(Key, out var element))
                return JsonSerializer.Deserialize<CurriculumDto>(element.GetRawText(), Json) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public static Task<bool> WriteAsync(QMgrDbContext db, Guid organizationId, CurriculumDto curriculum, CancellationToken ct = default)
        => OrganizationSettingsLock.MutateAsync(db, organizationId, org =>
        {
            org.Settings = OrganizationSettingsLock.WithKey(org.Settings, Key, curriculum);
            org.UpdatedAt = DateTime.UtcNow;
            return true;
        }, ct);

    /// <summary>The topics for a subject, a class and a term, in order. A class LEVEL matches a class by key prefix:
    /// "S1" covers S1A, "S1 B" and S1-East, because the level is what the syllabus is written for.</summary>
    public static List<CurriculumTopicDto> TopicsFor(CurriculumDto c, Guid subjectId, IEnumerable<string> classNames, int? term)
    {
        var keys = classNames.Select(ClassName.Key).Where(k => k.Length > 0).ToList();
        return c.Topics
            .Where(t => t.SubjectId == subjectId && (term == null || t.Term == 0 || t.Term == term)
                        && keys.Any(k => ClassName.Key(t.ClassLevel) is { Length: > 0 } level && k.StartsWith(level, StringComparison.Ordinal)))
            .OrderBy(t => t.Term).ThenBy(t => t.Order).ToList();
    }

    /// <summary>Checks and tidies a list before it is stored. Null = fine; otherwise the sentence a page shows.</summary>
    public static string? Problem(CurriculumDto c, ISet<Guid> subjectIds)
    {
        if (c.Topics.Count > 3000) return "A curriculum list holds at most 3,000 topics.";
        foreach (var t in c.Topics)
        {
            if (string.IsNullOrWhiteSpace(t.Topic)) return "Every line needs a topic.";
            if (!subjectIds.Contains(t.SubjectId)) return $"\"{t.Topic}\" names a subject this school does not have.";
            if (string.IsNullOrWhiteSpace(t.ClassLevel)) return $"\"{t.Topic}\" needs a class level (for example S1).";
            if (t.Term is < 0 or > 4) return $"\"{t.Topic}\": the term is 1, 2 or 3 (or blank for any).";
        }
        return null;
    }

    public static CurriculumDto Tidy(CurriculumDto c) => new()
    {
        Topics = c.Topics.Select(t => t with
        {
            Id = string.IsNullOrWhiteSpace(t.Id) ? Guid.NewGuid().ToString("N")[..12] : t.Id.Trim(),
            ClassLevel = t.ClassLevel.Trim(),
            Topic = t.Topic.Trim(),
            Periods = t.Periods is > 0 and <= 200 ? t.Periods : null
        }).ToList()
    };
}
