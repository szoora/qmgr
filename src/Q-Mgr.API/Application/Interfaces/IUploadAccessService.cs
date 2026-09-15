namespace QMgr.Application.Interfaces;

/// <summary>
/// Short-lived access tokens for uploaded files that are NOT public.
///
/// Uploads used to be served by the static-file middleware straight out of the API's own wwwroot,
/// ahead of authentication, so a welfare attachment or a visitor photograph was readable by anyone
/// holding its URL for ever. They are now served by <c>UploadsController</c>, which decides per
/// file: signage media stays public (it is on a wall by choice), everything else is gated.
///
/// A browser cannot send this app's JWT with an <c>&lt;img src&gt;</c> or an <c>&lt;a href&gt;</c>
/// (auth lives in localStorage, not a cookie), so a gated file's link is handed out with a signed,
/// expiring token in its query string — minted only when the caller has already passed the owning
/// record's own permission and row-scope checks. A leaked link therefore stops working by itself,
/// which is the property the old static path could never have.
/// </summary>
public interface IUploadAccessService
{
    /// <summary>The query-string parameter the token travels in.</summary>
    const string TokenQueryKey = "t";

    /// <summary>
    /// Appends an access token to an upload link. Anything that is not one of this app's own
    /// <c>/uploads/media/</c> links (an external URL, null, an already-signed link) is returned
    /// unchanged, so it is safe to call on every URL a DTO carries.
    /// </summary>
    string? Sign(string? url, TimeSpan? lifetime = null);

    /// <summary>True when <paramref name="token"/> was issued for exactly this file and has not expired.</summary>
    bool IsValid(string fileName, string? token);

    /// <summary>
    /// Removes an access token from a link before it is STORED. A client that captured a photo
    /// and sent the signed link back would otherwise persist a token that expires in an hour.
    /// </summary>
    string? Strip(string? url);
}
