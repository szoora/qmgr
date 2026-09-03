namespace QMgr.Web.Resources;

/// <summary>
/// Marker type for the shared resource file. Inject <c>IStringLocalizer&lt;SharedResources&gt;</c>
/// anywhere a user-visible string is needed and look it up by its English text, which is also the
/// resource key, so an untranslated string degrades to readable English rather than to a key name.
/// <para>
/// Scope note: the customer-facing screens (kiosk, customer display, queue board, join-the-queue,
/// ticket status and feedback) are the ones a member of the public actually reads, so those are
/// translated. The staff-facing admin application remains in English; it follows the same pattern
/// whenever it is worth translating, and needs no further plumbing to do so.
/// </para>
/// </summary>
public sealed class SharedResources
{
}
