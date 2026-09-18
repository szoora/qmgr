using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Web.Components.Shared.UI;

namespace QMgr.Web.Services;

/// <summary>
/// "Publish to Library" for every A4 print route — one home for a flow that was copied into seven
/// pages and had already drifted (three wordings of the success toast, two of the seven writing no
/// activity row; see <c>docs/plans/SECURE_DOCUMENT_SHARING.md</c> §13 "Already drifted" item 3).
/// Each page keeps its own document name, summary and provenance string — those describe a
/// particular report and must not be homogenised — and keeps its own <c>isPublishing</c> flag,
/// because that drives its own button.
/// </summary>
public static class ReportPublishing
{
    /// <summary>
    /// The one success wording. A published document is a snapshot (plan §4), and the share link is
    /// a separate deliberate act taken from the Library — saying both here is what stops a reader
    /// assuming either that the PDF tracks the record or that publishing has already shared it.
    /// </summary>
    public const string SnapshotDetail =
        "The report is a snapshot as of now. Create a share link from the Library when you are ready.";

    /// <summary>
    /// Whether to render the Publish control at all. Three gates, because the endpoint has three:
    /// <c>library.publish</c> and <c>content.create</c> (<c>ContentController.UploadMediaContent</c>
    /// checks both) and <c>[RequireModule(EngagementCommunications)]</c> on the controller. Without
    /// the module check the button showed on a tenant that cannot use it, and the user paid for a
    /// CDN fetch and a full-page raster before being told — CLAUDE.md's rule is that an absent
    /// button reads as an absent permission, where a refused one reads as a bug.
    /// </summary>
    /// <remarks>
    /// Fails OPEN when the module list could not be fetched, the same convention as the sidebar's
    /// <c>HasModule</c> and every page-level module guard: an empty set then means "we don't know",
    /// not "you own nothing", and hiding a paid feature during an API blip is worse than offering
    /// one the API would refuse. SuperAdmin is never module-gated (the API's own filter bypasses it
    /// for every tenant), and <see cref="IPermissionService"/> already answers true for it on both
    /// permission checks.
    /// </remarks>
    public static async Task<bool> CanPublishAsync(
        IPermissionService permissions, IModuleStateService moduleState, IModuleApiService moduleApi)
    {
        if (!await permissions.HasPermissionAsync(Permissions.LibraryPublish)) return false;
        if (!await permissions.HasPermissionAsync(Permissions.ContentCreate)) return false;

        await moduleState.LoadAsync(moduleApi);
        if (!moduleState.LoadSucceeded) return true;
        if (await permissions.IsSuperAdminAsync()) return true;

        return moduleState.ActiveModuleCodes.Contains(ModuleCodes.EngagementCommunications);
    }

    /// <summary>
    /// Renders the sheet to PDF in the browser (<c>reportPublish.js</c>) and uploads it into the
    /// Document Library as a shareable snapshot, then navigates to the new document. Never throws:
    /// a failure is reported to the caller as a toast, the same as it was in each page.
    /// </summary>
    public static async Task PublishAsync(
        ReportPublishRequest request,
        IJSRuntime js,
        IDocumentShareApiService docShareApi,
        IToastService toast,
        NavigationManager navigation)
    {
        try
        {
            var bytes = await js.InvokeAsync<byte[]>("reportPublish.render", request.SheetElementId, request.FileName);
            if (bytes == null || bytes.Length == 0)
                throw new InvalidOperationException($"The {request.SubjectNoun} rendered to an empty file.");

            using var stream = new MemoryStream(bytes);
            var doc = await docShareApi.UploadDocumentAsync(
                request.OrganizationId, stream, request.FileName,
                request.DocumentName,
                summary: request.Summary,
                publishedFrom: request.PublishedFrom,
                shareable: true);

            if (request.Audit != null)
            {
                // The activity log carries the publish (plan §11); the server never saw the PDF itself.
                // Swallowed on purpose: the document exists either way, and a side effect that runs
                // after the write has landed must not be able to report the write as failed.
                try { await request.Audit(doc); }
                catch { /* the page's own callback reports anything a reader needs to know */ }
            }

            var detail = string.IsNullOrWhiteSpace(request.ExtraDetail)
                ? SnapshotDetail
                : $"{SnapshotDetail} {request.ExtraDetail}";
            toast.Notify(ToastSeverity.Success, "Published to the Document Library", detail);
            navigation.NavigateTo($"/content/documents?document={doc.Id}");
        }
        catch (Exception ex)
        {
            toast.Notify(ToastSeverity.Error, "Could not publish", ex.Message);
        }
    }
}

/// <summary>
/// What one report contributes to a publish. Everything here is per-report: the name, summary and
/// provenance string are what a later reader of the Library row has to work out what they are
/// holding, so they stay with the page that knows.
/// </summary>
public sealed record ReportPublishRequest
{
    /// <summary>The id of the element <c>reportPublish.render</c> rasterises (the QPrintSheet).</summary>
    public required string SheetElementId { get; init; }
    public required Guid OrganizationId { get; init; }
    public required string FileName { get; init; }
    public required string DocumentName { get; init; }
    public required string Summary { get; init; }

    /// <summary>What this snapshot was generated from, kept on the row — see plan §4.</summary>
    public required string PublishedFrom { get; init; }

    /// <summary>
    /// The word for this document in the "rendered to an empty file" message — a pack and a
    /// timetable are not reports, and the message is read by the person who pressed the button.
    /// </summary>
    public string SubjectNoun { get; init; } = "report";

    /// <summary>
    /// One extra sentence after <see cref="ReportPublishing.SnapshotDetail"/>, for a report that
    /// genuinely has more to say about its own state. Not a second wording of the snapshot rule.
    /// </summary>
    public string? ExtraDetail { get; init; }

    /// <summary>
    /// The page's own record of the publish, run with the new document — an activity export row, a
    /// back-reference on the record the document came from, or both.
    /// </summary>
    public Func<MediaContentDto, Task>? Audit { get; init; }
}
