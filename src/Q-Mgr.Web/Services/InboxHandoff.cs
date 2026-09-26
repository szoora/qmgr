using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace QMgr.Web.Services;

/// <summary>
/// A table the Import inbox handed to an importer page (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E11): the page was
/// opened as <c>…?inbox={job}&amp;section={key}</c>, so it loads the staged CSV into its own import panel and, when that
/// import finishes, marks the section done. One home for reading those two query values, so the staff list and the roll
/// cannot come to disagree about them.
/// </summary>
public sealed record InboxHandoff(Guid JobId, string Section, string Csv, string FileName)
{
    /// <summary>The handed-over table, or null when the page was opened normally (or the section is not waiting any more).</summary>
    public static async Task<InboxHandoff?> FromQueryAsync(NavigationManager navigation, IImportInboxApiService inbox, Guid branchId)
    {
        if (branchId == Guid.Empty) return null;
        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        if (!query.TryGetValue("inbox", out var job) || !Guid.TryParse(job.ToString(), out var jobId)) return null;
        if (!query.TryGetValue("section", out var section) || string.IsNullOrWhiteSpace(section.ToString())) return null;
        try
        {
            var staged = await inbox.GetStagedAsync(branchId, jobId, section.ToString());
            return string.IsNullOrWhiteSpace(staged.Csv) ? null : new InboxHandoff(jobId, section.ToString(), staged.Csv!, staged.FileName ?? "table.csv");
        }
        catch
        {
            return null; // not waiting for this person: the page opens as it always does
        }
    }

    /// <summary>Marks the section approved once the importer has run. Never throws: the import itself has already happened.</summary>
    public async Task<bool> CompleteAsync(IImportInboxApiService inbox, Guid branchId)
    {
        try
        {
            await inbox.CompleteAsync(branchId, JobId, Section, null);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
