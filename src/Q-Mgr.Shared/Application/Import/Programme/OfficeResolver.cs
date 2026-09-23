using System.Text.RegularExpressions;
using QMgr.Application.DTOs;

namespace QMgr.Application.Import.Programme;

/// <summary>A department as the office resolver sees it.</summary>
public sealed record OfficeDepartment(Guid Id, string Name, string? Code, Guid? HeadUserId);

/// <summary>What an owner written as an office ("Biology HOD", "Academic Office") resolved to.</summary>
public sealed record OfficeResolution
{
    public string Text { get; init; } = string.Empty;
    public List<Guid> DepartmentIds { get; init; } = new();
    public List<Guid> UserIds { get; init; } = new();
    /// <summary>Every part of the text resolved to something.</summary>
    public bool Resolved { get; init; }
    /// <summary>The parts that did not resolve — kept as text, never guessed.</summary>
    public List<string> Unresolved { get; init; } = new();
}

/// <summary>
/// An owner the way a school writes one — an OFFICE, not a person: "Biology HOD", "Head of FNT Department",
/// "Academic Office", "Chaplaincy", "Administration, Senior Ladies &amp; Matrons" (plan §6). Each part is
/// resolved to a department (and, for "HOD" / "Head of", its head) once; a learned alias answers before any
/// guessing; anything left stays as text on the event, which is honest rather than wrong.
/// </summary>
public static class OfficeResolver
{
    private static readonly Regex Splitter = new(@"\s*(?:,|/|&|\band\b|\+|;)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadOf = new(@"^(?:the\s+)?head\s+of\s+(?:the\s+)?(?<d>.+?)(?:\s+(?:department|dept\.?))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Hod = new(@"^(?<d>.+?)\s+(?:hod|h\.o\.d\.?|head)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> OfficeWords = new(StringComparer.OrdinalIgnoreCase) { "OFFICE", "DEPARTMENT", "DEPT", "SECTION", "UNIT", "COMMITTEE" };

    /// <summary>The key a learned office alias is stored under.</summary>
    public static string AliasKey(string? text) => ProgrammeText.TitleKey(text);

    public static OfficeResolution Resolve(string? text, IReadOnlyList<OfficeDepartment> departments, ImportAliasesDto? aliases = null)
    {
        var written = (text ?? string.Empty).Trim();
        if (written.Length == 0) return new OfficeResolution { Resolved = true };

        if (aliases?.Offices != null && aliases.Offices.TryGetValue(AliasKey(written), out var whole))
            return new OfficeResolution { Text = written, DepartmentIds = whole.DepartmentIds.ToList(), UserIds = whole.UserIds.ToList(), Resolved = true };

        var deptIds = new List<Guid>();
        var userIds = new List<Guid>();
        var unresolved = new List<string>();
        foreach (var part in Splitter.Split(written).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (aliases?.Offices != null && aliases.Offices.TryGetValue(AliasKey(part), out var alias))
            {
                deptIds.AddRange(alias.DepartmentIds);
                userIds.AddRange(alias.UserIds);
                continue;
            }

            var wantsHead = false;
            var name = part;
            var headOf = HeadOf.Match(part);
            if (headOf.Success) { wantsHead = true; name = headOf.Groups["d"].Value; }
            else
            {
                var hod = Hod.Match(part);
                if (hod.Success) { wantsHead = true; name = hod.Groups["d"].Value; }
            }

            var dept = FindDepartment(name, departments);
            if (dept == null) { unresolved.Add(part); continue; }
            deptIds.Add(dept.Id);
            if (wantsHead && dept.HeadUserId is { } head) userIds.Add(head);
        }

        return new OfficeResolution
        {
            Text = written,
            DepartmentIds = deptIds.Distinct().ToList(),
            UserIds = userIds.Distinct().ToList(),
            Resolved = unresolved.Count == 0,
            Unresolved = unresolved
        };
    }

    private static OfficeDepartment? FindDepartment(string name, IReadOnlyList<OfficeDepartment> departments)
    {
        var words = ProgrammeText.Words(name).Where(w => !OfficeWords.Contains(w)).ToList();
        if (words.Count == 0) return null;
        var key = string.Join(' ', words);

        // Exact name or code, then the department's own words all present ("Chaplaincy" ↔ "Chaplaincy Department").
        var exact = departments.Where(d =>
            string.Join(' ', ProgrammeText.Words(d.Name).Where(w => !OfficeWords.Contains(w))) == key
            || (!string.IsNullOrWhiteSpace(d.Code) && ProgrammeText.TitleKey(d.Code) == key)).ToList();
        if (exact.Count == 1) return exact[0];

        var contained = departments.Where(d =>
        {
            var dw = ProgrammeText.Words(d.Name).Where(w => !OfficeWords.Contains(w)).ToList();
            return dw.Count > 0 && dw.All(words.Contains) && words.All(dw.Contains);
        }).ToList();
        return contained.Count == 1 ? contained[0] : null;
    }
}
