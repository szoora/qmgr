using QMgr.Application.DTOs;

namespace QMgr.Application.Import.Programme;

/// <summary>
/// What the programme import page needs from the server before it can resolve anything
/// (<c>GET api/v1/branches/{branchId}/calendar/import/context</c>, <c>calendar.manage</c>): the staff it can match
/// names to (with a one-way PHONE KEY, never a number), the departments offices resolve to, the aliases learned
/// from earlier imports, the school's calendar settings, the national calendar, and the terms.
/// </summary>
public sealed record ProgrammeImportContextDto
{
    public Guid OrganizationId { get; init; }
    public List<StaffCandidate> People { get; init; } = new();
    public List<OfficeDepartment> Departments { get; init; } = new();
    public ImportAliasesDto Aliases { get; init; } = new();
    public CalendarSettingsDto Settings { get; init; } = new();
    public NationalCalendarDto National { get; init; } = new();
    public List<PerformancePeriodDto> Terms { get; init; } = new();
    /// <summary>
    /// The caller may import meetings and rota slots: they hold <c>staff.duties.manage</c> AND the tenant holds the
    /// Welfare &amp; Performance module (decision D8). Without it only events are imported, and the page says so.
    /// </summary>
    public bool CanImportDuties { get; init; }
    /// <summary>Why duties cannot be imported, in words, when <see cref="CanImportDuties"/> is false.</summary>
    public string? DutiesRefusal { get; init; }
}

/// <summary>What the Development-only read-document endpoint answers: every file read, and the checks across them.</summary>
public sealed record ProgrammeReadResultDto
{
    public List<ProgrammeFileReading> Files { get; init; } = new();
    public ProgrammeCheckResult Checks { get; init; } = new();
    public List<ProgrammeFileSummaryDto> Summaries { get; init; } = new();
}

/// <summary>Counts for one file, so a suite can assert them without walking the whole reading.</summary>
public sealed record ProgrammeFileSummaryDto
{
    public string FileName { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public List<string> Kinds { get; init; } = new();
    public int TableRows { get; init; }
    public int EmptyWeeks { get; init; }
    public int Candidates { get; init; }
    public int Events { get; init; }
    public int Meetings { get; init; }
    public int RotaAssignments { get; init; }
    public int RotaPeople { get; init; }
    public int PeopleWithDates { get; init; }
    public int NotOnRota { get; init; }
    public int FromNotes { get; init; }
    public int Undated { get; init; }
    public string? Title { get; init; }
    public string? Theme { get; init; }
    public int? Year { get; init; }
    public string? Refusal { get; init; }

    public static ProgrammeFileSummaryDto Of(ProgrammeFileReading f) => new()
    {
        FileName = f.FileName,
        Format = f.Format,
        Kinds = f.Tables.Select(t => t.Kind.ToString()).ToList(),
        TableRows = f.TableRows,
        EmptyWeeks = f.EmptyWeeks,
        Candidates = f.Candidates.Count(c => !c.FromNote),
        Events = f.Candidates.Count(c => c.Kind == ProgrammeRowKind.Event && !c.FromNote),
        Meetings = f.Candidates.Count(c => c.Kind == ProgrammeRowKind.Meeting && !c.FromNote),
        RotaAssignments = f.Candidates.Count(c => c.Kind == ProgrammeRowKind.Rota),
        RotaPeople = f.RotaPeople,
        PeopleWithDates = f.Candidates.Where(c => c.Kind == ProgrammeRowKind.Rota && c.StartsOn.HasValue).Select(c => ProgrammeText.NameKey(c.PersonText)).Distinct().Count(),
        NotOnRota = f.NotOnRota.Count,
        FromNotes = f.Candidates.Count(c => c.FromNote),
        Undated = f.Candidates.Count(c => !c.StartsOn.HasValue && !c.FromNote),
        Title = f.Title,
        Theme = f.Theme,
        Year = f.Year,
        Refusal = f.Refusal
    };
}
