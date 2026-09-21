using System.Globalization;
using System.Text;

namespace QMgr.Domain.Identity;

/// <summary>
/// The order a combined name column is written in. There is no universal answer and no reliable way
/// to detect it from a single value: "Abaho Jude" is a surname followed by a given name in Uganda and
/// a given name followed by a surname in Ireland, and nothing in the string says which. So the
/// importer ASKS, shows what the choice does to the first rows, and never guesses silently.
/// </summary>
public enum NameOrder
{
    /// <summary>"Grace Nakato" — the given name first. Common in Western and East-Asian-romanised lists.</summary>
    GivenFirst = 0,

    /// <summary>"Nakato Grace" — the family name first. The usual convention on a Ugandan school roll.</summary>
    FamilyFirst = 1,
}

/// <summary>What a combined name was read as, alongside the string it was read from.</summary>
public readonly record struct PersonNameParts(string GivenName, string FamilyName, string Original)
{
    /// <summary>True when the value held only one word, so one half of the name is missing.</summary>
    public bool SingleWord => GivenName.Length > 0 && FamilyName.Length == 0;

    public bool IsEmpty => GivenName.Length == 0 && FamilyName.Length == 0;
}

/// <summary>
/// THE ONE HOME for turning a person's name between one column and two. The importer, any future
/// integration and anything that has to display a name all call this rather than writing a second
/// `Split(' ')` beside the code that needs one — the SSoT failure this codebase keeps rediscovering
/// (see RegistrationIdentity, and the two disagreeing column-alias maps that lived in one JS file).
///
/// WHAT IT DELIBERATELY DOES NOT DO: guess the order, guess whether a person has a family name at
/// all, or assume a name has exactly two parts. Those are the classic wrong assumptions about names
/// (W3C, "Personal names around the world"), and every one of them is wrong somewhere Q-Mgr is used.
/// The split is lossy by nature, which is why <see cref="PersonNameParts.Original"/> carries the
/// string it came from and why a student's name is stored whole and never split at all.
/// </summary>
public static class PersonName
{
    /// <summary>
    /// Particles belong to the family name, never to the given name: "van der Berg", "de la Cruz",
    /// "bin Rashid". Matched lower-case, so "Van" at the start of a Dutch surname still attaches.
    /// </summary>
    private static readonly HashSet<string> Particles = new(StringComparer.OrdinalIgnoreCase)
    {
        "van", "von", "der", "den", "de", "del", "della", "di", "da", "dos", "das", "du",
        "la", "le", "les", "lo", "bin", "binti", "bint", "ibn", "al", "el", "ter", "ten", "op", "st",
    };

    /// <summary>
    /// Honorifics a spreadsheet carries in the same cell. Stripped only when something is left after
    /// them, so a person actually called "Hon" keeps their name.
    /// </summary>
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mr.", "mrs", "mrs.", "ms", "ms.", "miss", "dr", "dr.", "prof", "prof.", "professor",
        "rev", "rev.", "fr", "fr.", "sr.", "hon", "hon.", "eng", "eng.", "madam", "sir",
    };

    /// <summary>
    /// Generational suffixes. They stay with the family name because this system stores two fields
    /// and "Daniel Okello Jr" must still read correctly; they are never treated as the surname.
    /// </summary>
    private static readonly HashSet<string> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "jr", "jr.", "snr", "sr", "senior", "junior", "ii", "iii", "iv",
    };

    /// <summary>
    /// Splits a combined name into a given and a family part.
    ///
    /// A COMMA ALWAYS WINS and always means "Family, Given" — that convention is unambiguous
    /// wherever it is written, so it overrides <paramref name="order"/> rather than being read
    /// through it. Everything else follows the order the reader chose.
    /// </summary>
    public static PersonNameParts Split(string? full, NameOrder order)
    {
        var original = full ?? string.Empty;
        var cleaned = Clean(original);
        if (cleaned.Length == 0) return new PersonNameParts(string.Empty, string.Empty, original);

        if (cleaned.Contains(','))
        {
            var halves = cleaned.Split(',', 2);
            var beforeComma = Clean(halves[0]);
            var afterComma = Clean(halves.Length > 1 ? halves[1] : string.Empty);
            return afterComma.Length == 0
                ? new PersonNameParts(beforeComma, string.Empty, original)          // "Nakato," is one word
                : new PersonNameParts(afterComma, beforeComma, original);
        }

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && Titles.Contains(words[0])) words.RemoveAt(0);
        if (words.Count == 0) return new PersonNameParts(string.Empty, string.Empty, original);
        if (words.Count == 1) return new PersonNameParts(words[0], string.Empty, original);

        // A trailing suffix is part of the family name, never the whole of it.
        string? suffix = null;
        if (words.Count > 2 && Suffixes.Contains(words[^1]))
        {
            suffix = words[^1];
            words.RemoveAt(words.Count - 1);
        }

        List<string> family, given;
        if (order == NameOrder.FamilyFirst)
        {
            // The family name is the first word plus any particles that run on from it: "Van Damme Jean".
            var take = 1;
            while (take < words.Count - 1 && Particles.Contains(words[take - 1])) take++;
            family = words.Take(take).ToList();
            given = words.Skip(take).ToList();
        }
        else
        {
            // The family name is the LAST word, with any particles that precede it: "Jean van der Berg".
            var start = words.Count - 1;
            while (start > 1 && Particles.Contains(words[start - 1])) start--;
            family = words.Skip(start).ToList();
            given = words.Take(start).ToList();
        }

        if (suffix is not null) family.Add(suffix);
        return new PersonNameParts(string.Join(' ', given), string.Join(' ', family), original);
    }

    /// <summary>
    /// THE ACCOUNT NAME a person signs in with when they have no email address to take one from —
    /// which on a Ugandan school roll is most of the staff. The email's local part is still
    /// preferred where there is one, because that is what everybody already has; otherwise it is the
    /// first name's initial and the family name ("m.kato"), falling back to the employee number.
    ///
    /// <para>The result is a SUGGESTION: the username index is global, so every caller still has to
    /// make it unique with a numeric suffix. It lives here so the staff import and the staff dialog
    /// cannot disagree about what somebody's username looks like.</para>
    /// </summary>
    public static string SuggestUsername(string? given, string? family, string? email = null, string? employeeNumber = null)
    {
        static string Clean(string value) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());

        if (!string.IsNullOrWhiteSpace(email))
        {
            var at = email.IndexOf('@');
            var local = Clean((at > 0 ? email[..at] : email).Trim().ToLowerInvariant());
            if (local.Length >= 3) return local;
        }

        var first = PersonName.Clean(given ?? string.Empty).ToLowerInvariant();
        var last = PersonName.Clean(family ?? string.Empty).ToLowerInvariant();
        var candidate = first.Length > 0 && last.Length > 0
            ? Clean($"{first[0]}.{last.Split(' ')[^1]}")
            : Clean(last.Length > 0 ? last : first);

        if (candidate.Length >= 3) return candidate;

        var number = Clean((employeeNumber ?? string.Empty).Trim().ToLowerInvariant());
        if (number.Length >= 3) return $"staff.{number}";

        return $"user{Guid.NewGuid():N}"[..12];
    }

    /// <summary>The two halves back into one name, in the order asked for. Used by previews and logs.</summary>

    public static string Join(string? given, string? family, NameOrder order = NameOrder.GivenFirst)
    {
        var g = Clean(given ?? string.Empty);
        var f = Clean(family ?? string.Empty);
        if (g.Length == 0) return f;
        if (f.Length == 0) return g;
        return order == NameOrder.FamilyFirst ? $"{f} {g}" : $"{g} {f}";
    }

    /// <summary>
    /// Trims, collapses runs of whitespace, and removes the zero-width and non-breaking characters a
    /// copy-and-paste out of a web page leaves behind — they are invisible on screen and make two
    /// identical-looking names compare as different, which is how a duplicate import happens.
    /// </summary>
    public static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value)
        {
            if (ch is '​' or '‌' or '‍' or '﻿') continue;         // zero-width
            var c = ch is ' ' or '\t' or '\n' or '\r' ? ' ' : ch;                 // nbsp and breaks
            if (char.IsControl(c)) continue;
            if (c == ' ')
            {
                if (sb.Length == 0) continue;
                space = true;
                continue;
            }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// "ABAASA BARBRA" → "Abaasa Barbra". School exports are very often shouted, and a name in
    /// capitals is unreadable next to one that is not — but a name that is ALREADY mixed case is
    /// left exactly alone, because "McDonald", "O'Brien" and "van der Berg" are somebody's own
    /// spelling and this must never overwrite them. Only an all-capitals value is touched.
    /// </summary>
    public static string FixShouting(string value)
    {
        var cleaned = Clean(value);
        if (cleaned.Length == 0) return cleaned;
        if (cleaned.Any(char.IsLower)) return cleaned;

        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TitleWord);
        return string.Join(' ', parts);
    }

    private static string TitleWord(string word)
    {
        if (word.Length == 0) return word;
        if (Particles.Contains(word)) return word.ToLowerInvariant();

        // Hyphens and apostrophes start a new capital: "Anne-Marie", "O'Brien", "N'Doye".
        var sb = new StringBuilder(word.Length);
        var upperNext = true;
        foreach (var ch in word)
        {
            if (upperNext)
            {
                sb.Append(char.ToUpper(ch, CultureInfo.InvariantCulture));
                upperNext = false;
            }
            else sb.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
            if (ch is '-' or '\'' or '’') upperNext = true;
        }

        var result = sb.ToString();
        // "McDonald" and "MacLeod" keep their inner capital; "Mcdonald" would be a change nobody asked
        // for, but restoring it is the more usual spelling and matches what every HR system does.
        if (result.Length > 3 && result.StartsWith("Mc", StringComparison.Ordinal))
            result = "Mc" + char.ToUpper(result[2], CultureInfo.InvariantCulture) + result[3..];
        return result;
    }
}
