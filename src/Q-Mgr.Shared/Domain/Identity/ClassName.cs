namespace QMgr.Domain.Identity;

/// <summary>
/// THE ONE HOME for comparing a class name — "S1A", "S1 A", "s1-a" and "S1/A" are one class.
///
/// <para>Classes are not a table: they live as a JSON list in <c>Branch.Settings</c> and
/// <c>Student.ClassName</c> is free text matched by name. That makes the matching rule the whole
/// safety mechanism, and CLAUDE.md already records why it matters — <i>a student invisible to their
/// own class teacher because of a stray space is a safeguarding failure, not a cosmetic one</i>.</para>
///
/// <para><b>There were three copies of that rule and every one of them was `Trim().ToLower()`</b>
/// (<c>ClassTeachersController.NormalizeClassName</c>, <c>TimetableCycle.Normalize</c>, and the
/// scope service), so a space was exactly the failure the note warns about: a roll typed "S1 A"
/// against a branch that configured "S1A" matched nothing. The first real school roster this
/// product imported raised that warning on <b>1,584 of 1,711 rows</b>.</para>
///
/// <para><b>Two forms, and they are different jobs.</b> <see cref="Display"/> is what is STORED and
/// shown — the school's own spelling, only tidied. <see cref="Key"/> is what is COMPARED, and it is
/// deliberately aggressive: everything that is not a letter or a digit is dropped. Never store a
/// Key; never compare a Display.</para>
/// </summary>
public static class ClassName
{
    /// <summary>
    /// What is stored: trimmed, with any internal run of whitespace collapsed to one space. The
    /// school's own spelling survives — "S1 A" stays "S1 A" — because a class name is a label
    /// people read on a register, not an identifier.
    /// </summary>
    public static string? Display(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(' ', parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// What is COMPARED. Letters and digits only, upper-cased: every separator a school might use
    /// between the form and the stream — space, hyphen, slash, dot, underscore — is dropped, so
    /// "S1 A", "S1-A", "S1/A" and "s1a" are one key.
    ///
    /// <para>Null for a value with no letter or digit in it at all, which is not a class name.</para>
    /// </summary>
    public static string? Key(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var n = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToUpperInvariant(c);
        }

        return n == 0 ? null : new string(buffer[..n]);
    }

    /// <summary>Whether two names are the same class, however either was written.</summary>
    public static bool AreSame(string? a, string? b)
    {
        var ka = Key(a);
        return ka != null && ka == Key(b);
    }

    /// <summary>
    /// The configured name that <paramref name="typed"/> means, or null when the branch has no such
    /// class. This is what an import uses to decide between "already configured, written
    /// differently" and "genuinely missing" — two very different answers for the reader.
    /// </summary>
    public static string? MatchIn(string? typed, IEnumerable<string?> configured)
    {
        var key = Key(typed);
        if (key == null) return null;

        foreach (var name in configured)
        {
            if (Key(name) == key) return name;
        }

        return null;
    }
}
