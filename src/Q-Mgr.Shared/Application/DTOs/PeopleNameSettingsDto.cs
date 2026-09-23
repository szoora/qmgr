using System.Text.Json.Serialization;
using QMgr.Domain.Identity;

namespace QMgr.Application.DTOs;

/// <summary>
/// HOW THIS ORGANISATION WRITES A PERSON'S NAME (2026-09-23). Stored in
/// <c>Organization.Settings["People"]</c> and read on the API only through <c>PersonNames</c>.
///
/// <para>Asked for by a Ugandan school that files staff by surname: "why is the name display starting
/// with first name? … where is the ui to control name display order?" There was none. The import
/// wizard asked the ORDER question, but only to split a combined column; display was hard-coded
/// given-first in about seventy places.</para>
///
/// <para><b>Staff and user accounts only.</b> A student keeps one whole name that is never split (a
/// standing decision in the roster), so there is no order to choose for them.</para>
///
/// <para><b>The default is the order every existing school already sees</b> — given name first — so
/// nothing moves until a school chooses (user decision D4, 2026-09-23).</para>
/// </summary>
public record PeopleNameSettingsDto
{
    /// <summary>How a name is written wherever it is shown: lists, the header, registers, reports, exports, emails.</summary>
    public NameOrder DisplayOrder { get; init; } = NameOrder.GivenFirst;

    /// <summary>
    /// Which name a list of people is sorted by. Null means "the same as <see cref="DisplayOrder"/>",
    /// which is what a school gets until it chooses otherwise. A separate choice (user decision D5)
    /// because some schools show "Agatha Ayebare" and still file by surname.
    /// </summary>
    public NameOrder? SortOrder { get; init; }

    [JsonIgnore]
    public NameOrder EffectiveSortOrder => SortOrder ?? DisplayOrder;
}
