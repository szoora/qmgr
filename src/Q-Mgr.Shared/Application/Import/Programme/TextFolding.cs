using System.Text;
using System.Text.RegularExpressions;

namespace QMgr.Application.Import.Programme;

/// <summary>Roman numerals as schools use them for terms and weeks ("Term III", "Week XII").</summary>
public static class RomanNumerals
{
    private static readonly Regex RomanRx = new(@"^(?=[MDCLXVI])M{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The value of a Roman numeral, or null when the text is not one.</summary>
    public static int? Parse(string? text)
    {
        var s = (text ?? string.Empty).Trim().TrimEnd('.').ToUpperInvariant();
        if (s.Length == 0 || !RomanRx.IsMatch(s)) return null;
        int total = 0, prev = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            var v = s[i] switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0 };
            total += v < prev ? -v : v;
            prev = Math.Max(prev, v);
        }
        return total;
    }

    public static string ToRoman(int value)
    {
        if (value <= 0 || value > 3999) return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var map = new (int, string)[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var sb = new StringBuilder();
        foreach (var (n, r) in map) while (value >= n) { sb.Append(r); value -= n; }
        return sb.ToString();
    }
}

/// <summary>
/// Titles in front of a name — "Mr", "Mrs", "Sr", "Fr", "Rev", "Dr", "Hon", "Br", "Sister", "Father" — with or
/// without the full stop. They are dropped before names are compared, never before a name is shown.
/// </summary>
public static class Honorifics
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "miss", "mx", "sr", "fr", "rev", "revd", "dr", "prof", "hon", "br", "bro", "sister", "father",
        "brother", "madam", "mdm", "sir", "eng", "pastor", "canon", "msgr", "mgr", "rt", "very"
    };

    public static bool IsHonorific(string? word) => word != null && Words.Contains(word.Trim().TrimEnd('.'));

    /// <summary>The name with every leading title removed: "Fr. Pius Shabamukama" → "Pius Shabamukama".</summary>
    public static string Strip(string? name)
    {
        var words = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && IsHonorific(words[0])) words.RemoveAt(0);
        return string.Join(' ', words);
    }
}

/// <summary>Jaro–Winkler similarity, 0..1 — how alike two words are, with extra weight on a shared start.</summary>
public static class JaroWinkler
{
    public static double Similarity(string? a, string? b)
    {
        a = (a ?? string.Empty).ToUpperInvariant();
        b = (b ?? string.Empty).ToUpperInvariant();
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;

        var window = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(b.Length - 1, i + window);
            for (var j = from; j <= to; j++)
            {
                if (bMatched[j] || a[i] != b[j]) continue;
                aMatched[i] = bMatched[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0;

        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (!bMatched[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }
        var m = (double)matches;
        var jaro = (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3.0;

        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(a.Length, b.Length)); i++)
        {
            if (a[i] != b[i]) break;
            prefix++;
        }
        return jaro + prefix * 0.1 * (1 - jaro);
    }
}

/// <summary>
/// The folding every comparison in the programme import uses: case, punctuation, Roman numerals and
/// honorifics are all ways of writing the same thing, so "Beginning of Term, 3 Staff Meeting" and
/// "Beginning of Term III Staff Meeting" fold to one key.
/// </summary>
public static class ProgrammeText
{
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase) { "the", "of", "for", "and", "a", "an", "&" };

    /// <summary>Upper-case words, letters and digits only, Roman numerals as numbers, filler words dropped.</summary>
    public static List<string> Words(string? text)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            var w = sb.ToString();
            sb.Clear();
            if (Filler.Contains(w)) return;
            // "III" → "3", but not a lone "I" or "V" that might be a word or an initial.
            if (w.Length is >= 2 and <= 5 && w.All(c => c is 'I' or 'V' or 'X') && RomanNumerals.Parse(w) is { } n) w = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            words.Add(w);
        }
        foreach (var ch in text ?? string.Empty)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
            else Flush();
        }
        Flush();
        return words;
    }

    /// <summary>A title key: the folded words in order, joined. Two titles that differ only in how they were typed share it.</summary>
    public static string TitleKey(string? title) => string.Join(' ', Words(title));

    /// <summary>A person-name key: honorifics dropped, words SORTED (order-free, the ImportMatching.NameKey rule).</summary>
    public static string NameKey(string? name)
    {
        var words = Words(Honorifics.Strip(StripTitles(name))).Where(w => !Honorifics.IsHonorific(w)).ToList();
        words.Sort(StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    /// <summary>The name without honorifics anywhere in it ("MRS. KARUGABA GRACE" → "KARUGABA GRACE").</summary>
    public static string StripTitles(string? name)
    {
        var words = (name ?? string.Empty).Replace(".", ". ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Honorifics.IsHonorific(w)).ToList();
        return string.Join(' ', words).Replace(" .", ".").Trim();
    }

    /// <summary>A short stable slug for a source key: letters and digits only, lower case, at most <paramref name="max"/> characters.</summary>
    public static string Slug(string? text, int max = 80)
    {
        var s = string.Join('-', Words(text)).ToLowerInvariant();
        return s.Length <= max ? s : s[..max];
    }
}
