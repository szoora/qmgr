using System.Text.RegularExpressions;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Application.Import.Programme;

/// <summary>What a document's words about WHO resolved to.</summary>
public sealed record AudienceTextResolution
{
    public string Text { get; init; } = string.Empty;
    public StaffAudienceDto Audience { get; init; } = new() { AllStaff = false };
    /// <summary>The parts that meant nothing on this staff list — asked about, never guessed.</summary>
    public List<string> Unresolved { get; init; } = new();
    /// <summary>What each resolved part was read as, for the reader to check ("Administration → Administrator (role)").</summary>
    public List<string> Readings { get; init; } = new();
    public bool Resolved => Unresolved.Count == 0 && !Audience.IsEmpty;
}

/// <summary>
/// A document's words about who a meeting is for — "Administration, Senior Ladies &amp; Matrons", "All staff",
/// "HODs", "Class teachers", "Teaching staff", "Mr Okello" — read into a <see cref="StaffAudienceDto"/> (plan
/// CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E9, 2026-09-26).
///
/// <para><b>Why it exists.</b> Until this, the importer read attendance from the meeting's TITLE alone: a "Staff
/// meeting" expected everyone, an "HOD meeting" the heads, and everything else — a departmental meeting, a class
/// teachers' meeting, a meeting whose attendance column said exactly who — became a calendar event with no register,
/// silently. That is how Maryhill's meetings did not reach the registers.</para>
///
/// <para>Each part is tried against, in order: a learned alias (the reader's own earlier answer); every member of
/// staff; a staff group; the department heads; the class teachers; a department ("the Science department", "Biology
/// HOD" → its head); a role by name ("Administrators" → the Administrator role); a person. A part that matches nothing
/// is returned as <see cref="AudienceTextResolution.Unresolved"/> and the page asks — never a guess. Pure: the caller
/// hands in the staff list and the options.</para>
/// </summary>
public static class AudienceTextResolver
{
    private static readonly Regex Splitter = new(@"\s*(?:,|/|&|\band\b|\+|;)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadOf = new(@"^(?:the\s+)?head\s+of\s+(?:the\s+)?(?<d>.+?)(?:\s+(?:department|dept\.?))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Hod = new(@"^(?<d>.+?)\s+(?:hod|h\.o\.d\.?|head)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Everyone = new(StringComparer.Ordinal)
    {
        "ALL", "ALLSTAFF", "STAFF", "WHOLESTAFF", "ALLSTAFFMEMBERS", "STAFFMEMBERS", "EVERYONE", "ALLTEACHINGANDNONTEACHINGSTAFF", "ENTIRESTAFF", "ALLMEMBERSOFSTAFF"
    };
    private static readonly HashSet<string> Heads = new(StringComparer.Ordinal)
    {
        "HODS", "HOD", "HEADSOFDEPARTMENT", "HEADSOFDEPARTMENTS", "ALLHODS", "HEADSOFDEPT", "DEPARTMENTHEADS"
    };
    private static readonly HashSet<string> ClassTeachers = new(StringComparer.Ordinal)
    {
        "CLASSTEACHERS", "CLASSTEACHER", "ALLCLASSTEACHERS", "CLASSTEACHERSANDASSISTANTS"
    };
    private static readonly Dictionary<string, string> GroupWords = new(StringComparer.Ordinal)
    {
        ["TEACHERS"] = StaffGroups.Teaching, ["ALLTEACHERS"] = StaffGroups.Teaching, ["TEACHINGSTAFF"] = StaffGroups.Teaching,
        ["SUPPORTSTAFF"] = StaffGroups.Support, ["NONTEACHINGSTAFF"] = StaffGroups.Support, ["ANCILLARYSTAFF"] = StaffGroups.Support
    };

    public static string Key(string? s) => StaffGroups.Key(s);

    public static AudienceTextResolution Resolve(string? text, AudienceOptionsDto options, IReadOnlyList<StaffCandidate> staff, ImportAliasesDto? aliases = null)
    {
        var written = (text ?? string.Empty).Trim();
        var audience = new StaffAudienceDto { AllStaff = false };
        var unresolved = new List<string>();
        var readings = new List<string>();
        if (written.Length == 0) return new AudienceTextResolution { Text = written, Audience = audience };

        if (TryAlias(written, aliases, audience)) return new AudienceTextResolution { Text = written, Audience = Tidy(audience), Readings = { $"\"{written}\" → as you answered before" } };

        foreach (var part in Splitter.Split(written).Select(p => p.Trim().Trim('.', '-', ':')).Where(p => p.Length > 0))
        {
            if (TryAlias(part, aliases, audience)) { readings.Add($"{part} → as you answered before"); continue; }
            var key = Key(part);
            if (Everyone.Contains(key)) { audience.AllStaff = true; readings.Add($"{part} → every member of staff"); continue; }

            var group = options.StaffGroups.FirstOrDefault(g => Key(g) == key || Key(g) == Key(part + " staff"))
                        ?? (GroupWords.TryGetValue(key, out var mapped) ? options.StaffGroups.FirstOrDefault(g => Key(g) == Key(mapped)) : null);
            if (group != null) { audience.StaffGroups.Add(group); readings.Add($"{part} → {group}"); continue; }

            if (Heads.Contains(key))
            {
                var heads = options.Departments.Where(d => d.HeadUserId.HasValue).Select(d => d.HeadUserId!.Value).Distinct().ToList();
                if (heads.Count > 0) { audience.UserIds.AddRange(heads); readings.Add($"{part} → the {heads.Count} heads of department"); continue; }
            }
            if (ClassTeachers.Contains(key) && options.ClassTeacherUserIds.Count > 0)
            {
                audience.UserIds.AddRange(options.ClassTeacherUserIds);
                readings.Add($"{part} → the {options.ClassTeacherUserIds.Count} class teachers");
                continue;
            }

            // "Biology HOD", "Head of Sciences" → that department's head; "Science department" → the department.
            var wantsHead = false;
            var name = part;
            var headOf = HeadOf.Match(part);
            if (headOf.Success) { wantsHead = true; name = headOf.Groups["d"].Value; }
            else if (Hod.Match(part) is { Success: true } hod) { wantsHead = true; name = hod.Groups["d"].Value; }
            var dept = FindDepartment(name, options.Departments);
            if (dept != null)
            {
                if (wantsHead && dept.HeadUserId is { } head) { audience.UserIds.Add(head); readings.Add($"{part} → head of {dept.Name}"); }
                else { audience.DepartmentIds.Add(dept.Id); readings.Add($"{part} → {dept.Name} (department)"); }
                continue;
            }

            var role = FindRole(part, options.Roles);
            if (role != null) { audience.RoleCodes.Add(role.Code); readings.Add($"{part} → {role.Name} (role)"); continue; }

            var person = StaffNameResolver.Resolve(part, null, staff, aliases);
            if (person.Verdict == NameMatchVerdict.Match && person.UserId is { } userId)
            {
                audience.UserIds.Add(userId);
                readings.Add($"{part} → {person.Candidates.FirstOrDefault()?.FullName ?? part}");
                continue;
            }
            unresolved.Add(part);
        }

        return new AudienceTextResolution { Text = written, Audience = Tidy(audience), Unresolved = unresolved, Readings = readings };
    }

    private static bool TryAlias(string text, ImportAliasesDto? aliases, StaffAudienceDto into)
    {
        if (aliases?.Offices == null || !aliases.Offices.TryGetValue(OfficeResolver.AliasKey(text), out var a)) return false;
        into.AllStaff |= a.AllStaff;
        into.DepartmentIds.AddRange(a.DepartmentIds ?? new());
        into.UserIds.AddRange(a.UserIds ?? new());
        into.RoleCodes.AddRange(a.RoleCodes ?? new());
        into.StaffGroups.AddRange(a.StaffGroups ?? new());
        return a.AllStaff || (a.DepartmentIds?.Count ?? 0) + (a.UserIds?.Count ?? 0) + (a.RoleCodes?.Count ?? 0) + (a.StaffGroups?.Count ?? 0) > 0;
    }

    private static StaffAudienceDto Tidy(StaffAudienceDto a) => a.AllStaff
        ? StaffAudienceDto.Everyone()
        : new StaffAudienceDto
        {
            AllStaff = false,
            StaffGroups = a.StaffGroups.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RoleCodes = a.RoleCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DepartmentIds = a.DepartmentIds.Distinct().ToList(),
            UserIds = a.UserIds.Distinct().ToList()
        };

    private static readonly HashSet<string> OfficeWords = new(StringComparer.OrdinalIgnoreCase) { "OFFICE", "DEPARTMENT", "DEPT", "SECTION", "UNIT", "COMMITTEE", "THE" };

    private static AudienceDepartmentDto? FindDepartment(string name, IReadOnlyList<AudienceDepartmentDto> departments)
    {
        var words = ProgrammeText.Words(name).Where(w => !OfficeWords.Contains(w)).ToList();
        if (words.Count == 0) return null;
        var key = string.Join(' ', words);
        var exact = departments.Where(d => string.Join(' ', ProgrammeText.Words(d.Name).Where(w => !OfficeWords.Contains(w))) == key).ToList();
        if (exact.Count == 1) return exact[0];
        // "Sciences" ↔ "Science": a trailing S is the commonest difference between how a document and a directory spell one.
        var singular = departments.Where(d => Key(string.Join(' ', ProgrammeText.Words(d.Name).Where(w => !OfficeWords.Contains(w)))).TrimEnd('S') == Key(key).TrimEnd('S')).ToList();
        return singular.Count == 1 ? singular[0] : null;
    }

    /// <summary>"Administrators", "the Administrator", "Head Teacher" → a role by its NAME (or code), plural or not.</summary>
    private static AudienceRoleDto? FindRole(string part, IReadOnlyList<AudienceRoleDto> roles)
    {
        var key = Key(Regex.Replace(part, @"^\s*the\s+", string.Empty, RegexOptions.IgnoreCase));
        if (key.Length < 3) return null;
        var matches = roles.Where(r => Key(r.Name) == key || Key(r.Code) == key
                                       || Key(r.Name) + "S" == key || Key(r.Name) == key.TrimEnd('S')
                                       // "Administration" is how a document names the administrators.
                                       || (key.EndsWith("ATION", StringComparison.Ordinal) && Key(r.Name).StartsWith(key[..^5], StringComparison.Ordinal) && key.Length > 8))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
