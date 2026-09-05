using QMgr.Domain.Enums;

namespace QMgr.Web.Services;

/// <summary>
/// How a row outcome is written on screen. Both import logs (roster and welfare) render the same
/// enum, and both were printing its bare member name — "DuplicateInFile" is a C# identifier, not
/// something to show a matron. One home for the wording so the two logs cannot drift apart.
/// </summary>
public static class ImportOutcomeDisplay
{
    public static string Label(RosterImportRowOutcome outcome) => outcome switch
    {
        RosterImportRowOutcome.Created => "Created",
        RosterImportRowOutcome.Updated => "Updated",
        RosterImportRowOutcome.DuplicateInFile => "Repeated in file",
        RosterImportRowOutcome.AlreadyExists => "Already on record",
        RosterImportRowOutcome.Failed => "Failed",
        _ => outcome.ToString()
    };

    /// <summary>The CSS modifier suffix — kept next to the label so a new member needs one edit.</summary>
    public static string CssModifier(RosterImportRowOutcome outcome) => outcome.ToString().ToLowerInvariant();
}
