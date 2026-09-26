using System.ComponentModel.DataAnnotations;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// The Import inbox (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E11, 2026-09-26). A document is read ONCE, in the
// browser; each table is routed to the section that owns it; nothing is written until that section's owner
// approves it; approving runs that importer's own commit, so every rule each importer already enforces still holds.
// =====================================================================================================

/// <summary>The sections a document's tables are routed to, and the permission that OWNS each (approves it).</summary>
public static class ImportSectionKinds
{
    /// <summary>Calendar events: the term's activities, a timed programme. calendar.manage.</summary>
    public const string Events = "events";
    /// <summary>Meetings with registers. staff.duties.manage.</summary>
    public const string Meetings = "meetings";
    /// <summary>Duty rota slots. staff.duties.manage.</summary>
    public const string Rota = "rota";
    /// <summary>A timetable grid — into a DRAFT timetable. timetable.manage.</summary>
    public const string Timetable = "timetable";
    /// <summary>A staff list. staff.structure.manage (and users.create, which the staff import itself also requires).</summary>
    public const string Staff = "staff";
    /// <summary>A class list / student roll. students.manage.</summary>
    public const string Students = "students";

    public static readonly IReadOnlyList<string> All = new[] { Events, Meetings, Rota, Timetable, Staff, Students };

    /// <summary>The programme importer's own sections: approving one commits that part of the staged request.</summary>
    public static bool IsProgramme(string? kind) => kind is Events or Meetings or Rota;

    /// <summary>A section handed to another importer, where the approver finishes the column mapping and imports.</summary>
    public static bool IsHandoff(string? kind) => kind is Timetable or Staff or Students;

    public static string Label(string? kind) => kind switch
    {
        Events => "Calendar events",
        Meetings => "Meetings and registers",
        Rota => "Duty rota",
        Timetable => "Timetable",
        Staff => "Staff list",
        Students => "Student roll",
        _ => "Not recognised"
    };

    /// <summary>Where the approver finishes a handed-off section.</summary>
    public static string? HandoffPage(string? kind) => kind switch
    {
        // A timetable import goes into a DRAFT the master chooses, so the table is handed over as a file and the
        // section is marked done from the inbox once it is in.
        Timetable => "/admin/timetable",
        Staff => "/admin/staff?tab=import",
        Students => "/admin/students/roster",
        _ => null
    };

    /// <summary>Whether the owning importer opens the handed-over table by itself (?inbox=…&amp;section=…).</summary>
    public static bool OpensHandoff(string? kind) => kind is Staff or Students;
}

public static class ImportSectionStates
{
    public const string Awaiting = "awaiting";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

public record ImportInboxSectionDto
{
    public string Key { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int RowCount { get; init; }
    /// <summary>Why the table was routed here ("Headings DATE · MEETING · VENUE · CONVENER").</summary>
    public string? RouteReason { get; init; }
    public string State { get; init; } = ImportSectionStates.Awaiting;
    public string? DecidedByName { get; init; }
    public DateTime? DecidedAt { get; init; }
    public string? Note { get; init; }
    /// <summary>The importer's own job that approving made — the undo handle lives there.</summary>
    public Guid? ResultJobId { get; init; }
    public bool Handoff { get; init; }
    /// <summary>May the caller approve or reject this section now?</summary>
    public bool CanDecide { get; init; }
    /// <summary>Why not, when they may not ("A second person must approve what you uploaded").</summary>
    public string? WhyNot { get; init; }
}

public record ImportInboxJobDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public DateTime CreatedAt { get; init; }
    public Guid? CreatedByUserId { get; init; }
    public string? CreatedByName { get; init; }
    public List<string> SourceFiles { get; init; } = new();
    public List<ImportInboxSectionDto> Sections { get; init; } = new();
    /// <summary>Every section decided.</summary>
    public bool Done { get; init; }
    public bool RequireSecondApprover { get; init; }
}

public record SubmitImportRequest
{
    public List<string> SourceFiles { get; set; } = new();
    public List<SubmitImportSectionDto> Sections { get; set; } = new();
}

public record SubmitImportSectionDto
{
    [Required]
    public string Kind { get; set; } = string.Empty;
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    [MaxLength(400)]
    public string? RouteReason { get; set; }
    public int RowCount { get; set; }
    /// <summary>For events, meetings and rota: the part of the programme request this section commits.</summary>
    public ProgrammeImportRequest? Programme { get; set; }
    /// <summary>For a handed-off section: the table as CSV, for the owning importer to open.</summary>
    public string? Csv { get; set; }
    [MaxLength(255)]
    public string? FileName { get; set; }
}

/// <summary>What an approver opens: the staged content of one section.</summary>
public record ImportInboxStagedDto
{
    public string Kind { get; init; } = string.Empty;
    public ProgrammeImportRequest? Programme { get; init; }
    public string? Csv { get; init; }
    public string? FileName { get; init; }
}

public record DecideImportSectionRequest
{
    [MaxLength(500)]
    public string? Note { get; set; }
    /// <summary>For a handed-off section completed in its importer: that importer's job.</summary>
    public Guid? ResultJobId { get; set; }
}

/// <summary>Organization.Settings["Imports"], written through OrganizationSettingsLock.</summary>
public record ImportSettingsDto
{
    /// <summary>Four eyes (NIST SP 800-53 AC-5): the person who uploaded a document may not approve it. Off by default.</summary>
    public bool RequireSecondApprover { get; set; }
}
