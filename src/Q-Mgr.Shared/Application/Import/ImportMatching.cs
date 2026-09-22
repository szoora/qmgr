using System.Text;

namespace QMgr.Application.Import;

/// <summary>
/// WHAT IS DIFFERENT, AND WHO MIGHT BE THE SAME PERSON — the one home for both questions, run by the
/// precheck that answers them before anything is sent AND by the job that carries the import out.
///
/// <para>The rule the importer already applied, written down: a value the file LEFT BLANK never
/// changes what is stored — a sheet exported from a system that does not hold national IDs must not
/// blank everybody's. So "different" means the file has something, and it is not what is on file.
/// Both sides run this, because a preview that says "nothing will change" and an import that then
/// changes three fields is the worst answer an import can give (docs/plans/BULK_IMPORT_SYSTEM.md).</para>
/// </summary>
public static class ImportMatching
{
    /// <summary>
    /// True when the file would overwrite what is stored. Blank in the file is never a change, and
    /// the comparison is ordinal on the trimmed values — the same test the roster and staff jobs make
    /// before they assign.
    /// </summary>
    public static bool Differs(string? fromFile, string? stored)
    {
        if (string.IsNullOrWhiteSpace(fromFile)) return false;
        return !string.Equals(fromFile.Trim(), (stored ?? string.Empty).Trim(), StringComparison.Ordinal);
    }

    /// <summary>Adds <paramref name="label"/> to <paramref name="into"/> when the file would overwrite the stored value.</summary>
    public static void Note(ICollection<string> into, string label, string? fromFile, string? stored)
    {
        if (Differs(fromFile, stored)) into.Add(label);
    }

    /// <summary>The struct counterpart: a parsed date, sex or employment type the file gave and the record does not match.</summary>
    public static void Note<T>(ICollection<string> into, string label, T? fromFile, T? stored) where T : struct
    {
        if (fromFile is null) return;
        if (!EqualityComparer<T?>.Default.Equals(fromFile, stored)) into.Add(label);
    }

    /// <summary>
    /// A blocking key for "is this the same person's name?". Case, punctuation and spacing are
    /// dropped, and THE WORDS ARE SORTED — so "Aine Grace" and "GRACE, AINE" are one key. A school's
    /// own two exports routinely disagree on name order (PersonName exists for that reason), and a
    /// duplicate that hides behind a flipped name is the one nobody spots by eye.
    ///
    /// <para>It is deliberately a HINT and nothing more: two children really can share a name, which
    /// is why what it feeds is a question the reader answers, never a refusal.</para>
    /// </summary>
    public static string? NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var word = new StringBuilder();
        var words = new List<string>();
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) word.Append(char.ToUpperInvariant(ch));
            else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        if (word.Length > 0) words.Add(word.ToString());
        if (words.Count == 0) return null;

        words.Sort(StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    /// <summary>True when two names are the same name however they were written round.</summary>
    public static bool SameName(string? a, string? b)
    {
        var ka = NameKey(a);
        return ka != null && ka == NameKey(b);
    }
}
