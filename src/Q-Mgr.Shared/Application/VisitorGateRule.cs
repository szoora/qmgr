using QMgr.Application.DTOs;
using QMgr.Domain.Identity;

namespace QMgr.Application;

/// <summary>
/// THE ONE HOME FOR WHICH GATE A VISIT RECORDS (plan TERM_PROGRAMME_CALENDAR_AND_GATES §10, 2026-09-23).
///
/// <para>The API resolves a check-in's and a check-out's gate through <see cref="Resolve"/>, and the Web asks the
/// same function before it posts — so the form refuses exactly what the server refuses, in the same words.</para>
///
/// <list type="bullet">
/// <item>No active gate: nothing is recorded and whatever was sent is ignored. A branch that never set up gates
/// must not start refusing check-ins the day this shipped.</item>
/// <item>One active gate: that gate, filled in. There is no choice to ask about — but a DIFFERENT gate sent by a
/// client is refused rather than silently replaced, because a client that thinks there is another gate is wrong
/// about the branch.</item>
/// <item>Two or more: required, and it must be one of the ACTIVE gates. A retired gate stays readable on old
/// visits and is never offered or accepted on a new one.</item>
/// </list>
///
/// <para>Names are compared by <see cref="Key"/> — letters and digits, upper-cased, the <c>ClassName.Key</c>
/// fold — so "Main gate", "MAIN-GATE" and "main gate " are one gate. What is STORED on the visit is the gate's
/// own display name as the list holds it that day; a later rename changes the list, never the history.</para>
/// </summary>
public static class VisitorGateRule
{
    /// <summary>The request field a refusal is filed under.</summary>
    public const string Field = "Gate";

    /// <summary>The folded comparison key. Null for a name with no letter or digit in it.</summary>
    public static string? Key(string? name) => ClassName.Key(name);

    /// <summary>The gates on offer today, in the list's own order.</summary>
    public static List<VocabularyItemDto> Active(IEnumerable<VocabularyItemDto>? gates) =>
        (gates ?? Enumerable.Empty<VocabularyItemDto>())
            .Where(g => g.IsActive && Key(g.Name) != null)
            .OrderBy(g => g.SortOrder)
            .ToList();

    /// <summary>
    /// The gate to record, or the sentence that refuses the request. <paramref name="leaving"/> changes only the
    /// wording of the "choose one" refusal.
    /// </summary>
    public static (string? Gate, string? Error) Resolve(IEnumerable<VocabularyItemDto>? gates, string? requested, bool leaving = false)
    {
        var all = (gates ?? Enumerable.Empty<VocabularyItemDto>()).ToList();
        var active = Active(all);
        if (active.Count == 0) return (null, null);

        var key = Key(requested);
        if (key == null)
        {
            return active.Count == 1
                ? (active[0].Name, null)
                : (null, leaving ? "Choose the gate the visitor is leaving by." : "Choose the gate the visitor came in by.");
        }

        var match = active.FirstOrDefault(g => Key(g.Name) == key);
        if (match != null) return (match.Name, null);

        var retired = all.FirstOrDefault(g => !g.IsActive && Key(g.Name) == key);
        return retired != null
            ? (null, $"“{retired.Name}” is no longer in use. Choose one of this branch's gates.")
            : (null, $"“{requested!.Trim()}” is not one of this branch's gates.");
    }

    /// <summary>Just the refusal, for a form deciding whether it may post.</summary>
    public static string? Problem(IEnumerable<VocabularyItemDto>? gates, string? requested, bool leaving = false) =>
        Resolve(gates, requested, leaving).Error;
}
