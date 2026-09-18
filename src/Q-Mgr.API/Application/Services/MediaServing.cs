namespace QMgr.API.Application.Services;

/// <summary>
/// How a Library row (<c>MediaContent</c>) is served, in one place.
///
/// <para><b>The invariant, stated once (plan §1, corrected in Revision 3 on 2026-09-18):</b> a
/// document's serving mode is decided by <c>IsShareable</c> alone. If it is shareable, its raw
/// <c>/uploads/media/{file}</c> path is gated — <i>whatever else is true of it</i>. Playlist
/// membership makes a document playable on signage; it does not make it public.</para>
///
/// <para><b>Why this exists as a function at all.</b> Until 2026-09-18 the same predicate was
/// written twice — once in <c>UploadAuthorizer</c> as the security decision, once in
/// <c>ContentController</c> as the DTO's convenience flag — as exact De Morgan inverses. They
/// agreed, but only one was load-bearing, and a reviewer editing the convenience copy had no signal
/// that it was half of a pair. Worse, both read playlist membership, so a share-only document added
/// to any playlist became anonymously fetchable with a day of cache: the plan promised in §1 that a
/// document could be both "without leaking from one into the other", §5 defined only two exclusive
/// categories, and the e2e asserted each separately ("on <i>no</i> playlist") and never the
/// combination.</para>
///
/// <para><b>The combination is now refused at the point of writing</b> — a shareable document cannot
/// be added to a playlist, and a document on a playlist cannot be made shareable (see
/// <c>ContentController.AddPlaylistItem</c> and <c>DocumentSharesController.UpdatePublishing</c>).
/// That refusal is what keeps this rule from silently breaking signage: gating a document that a
/// public display fetches anonymously would stop it rendering on the wall with no error anywhere.
/// Both halves are needed; neither alone is safe.</para>
/// </summary>
public static class MediaServing
{
    /// <summary>
    /// True when the file may be served to anyone, with no token and no signed-in user — the signage
    /// case. A non-shareable Library row is public by intent: somebody chose to put it on a wall.
    /// </summary>
    public static bool IsPublic(bool isShareable) => !isShareable;

    /// <summary>
    /// True when the file is served only through the share gate, a signed <c>?t=</c> link, or a
    /// bearer token whose holder may read the owning row. The inverse of <see cref="IsPublic"/>,
    /// named separately because both readings appear in the code and neither should be re-derived.
    /// </summary>
    public static bool IsGated(bool isShareable) => isShareable;

    /// <summary>
    /// The refusal used by both write paths that would otherwise create the contradictory state.
    /// One wording, so the two endpoints cannot drift apart.
    /// </summary>
    public const string CombinationRefusedTitle = "A shared document cannot also be on signage";

    /// <summary>
    /// Said in the second person because both call sites surface it straight to the person who
    /// asked. It names the way out rather than only the refusal.
    /// </summary>
    public const string CombinationRefusedDetail =
        "A document behind a share link is served only through that link, so it cannot also play on a " +
        "public screen. Turn sharing off to put it on a playlist, or take it off every playlist to share it.";
}
