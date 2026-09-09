using Microsoft.AspNetCore.WebUtilities;

namespace QMgr.Web.Services;

/// <summary>
/// One home for "is this URL a public display screen, and which branch is it for?".
///
/// Three callers need this and until 2026-09-06 two of them (DisplayLayout, KioskLayout) each
/// carried their own byte-identical copy of the branch-resolution loop. App.razor became the
/// third caller when the display theme started being stamped server-side, and a third copy of a
/// rule that had already been duplicated once is exactly the SSoT drift CLAUDE.md flags as this
/// codebase's recurring failure. The layouts now call into this instead.
/// </summary>
public static class PublicDisplayRoute
{
    /// <summary>
    /// Paths whose pages are unauthenticated screens carrying a branch in the URL —
    /// CustomerDisplay, SignageDisplay, KioskMode and the two feedback pages.
    ///
    /// This allowlist is why App.razor does not simply look for any GUID in the path: plenty of
    /// admin routes carry one that is not a branch at all (<c>/admin/students/{studentId}/picture</c>
    /// being the obvious trap), and fetching branding for a student id would be both wrong and a
    /// wasted round-trip on every page load.
    /// </summary>
    private static readonly string[] PublicPrefixes =
    {
        "/display",
        "/queue/display",
        "/kiosk",
        "/queue/kiosk",
        "/feedback"
    };

    /// <summary>True when this path is one of the public display screens.</summary>
    public static bool IsPublicDisplay(string absolutePath)
    {
        foreach (var prefix in PublicPrefixes)
        {
            if (absolutePath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                absolutePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The branch this URL names, from the route or <c>?branch=</c>, or null.
    ///
    /// Deliberately URL-only. The layouts additionally fall back to the signed-in admin's selected
    /// branch, but that lives in localStorage and needs JS interop, which does not exist during
    /// server rendering — so the server-side caller can only ever use what the URL carries. A
    /// public display screen always carries it, which is what makes that acceptable.
    /// </summary>
    public static Guid? BranchIdFromUri(Uri uri)
    {
        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(segment, out var fromPath) && fromPath != Guid.Empty)
                return fromPath;
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.TryGetValue("branch", out var raw)
            && Guid.TryParse(raw.ToString(), out var fromQuery)
            && fromQuery != Guid.Empty)
            return fromQuery;

        return null;
    }
}
