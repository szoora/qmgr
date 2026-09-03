using System.Text;
using System.Text.RegularExpressions;

namespace QMgr.Domain.Identity;

/// <summary>
/// Normalization used to decide whether two sign-ups are the same customer.
/// <para>
/// Registration previously compared email addresses exactly, which is trivially defeated:
/// <c>j.o.h.n+2@gmail.com</c> and <c>john@gmail.com</c> are one inbox but two different strings, so
/// one person could open unlimited free trials from a single mailbox. Everything here exists to
/// reduce a supplied value to the form two records would share if they belong to the same person or
/// business, so a unique index and a similarity score have something meaningful to compare.
/// </para>
/// <para>
/// Lives in Q-Mgr.Shared so the Blazor app can show the customer the same normalized value the API
/// will match on, rather than the two silently disagreeing.
/// </para>
/// </summary>
public static partial class RegistrationIdentity
{
    /// <summary>
    /// Mail providers that ignore dots and treat everything after a plus as the same inbox. Folding
    /// these is the single highest-value normalization: they are free, unlimited, and the alias
    /// trick is the first thing anyone farming trials reaches for.
    /// </summary>
    private static readonly HashSet<string> DotAndPlusAliasingDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com"
    };

    /// <summary>Providers that honour dots but still treat a plus suffix as the same inbox.</summary>
    private static readonly HashSet<string> PlusAliasingDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook.com", "hotmail.com", "live.com", "msn.com",
        "yahoo.com", "ymail.com", "protonmail.com", "proton.me",
        "icloud.com", "me.com", "fastmail.com", "zoho.com"
    };

    /// <summary>
    /// Words that carry no identifying signal in a business name. "Kampala Pharmacy" and
    /// "Kampala Pharmacy Ltd." are the same applicant; the suffix only adds noise to a comparison.
    /// </summary>
    private static readonly HashSet<string> NameNoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ltd", "limited", "plc", "inc", "incorporated", "llc", "co", "company",
        "corp", "corporation", "enterprise", "enterprises", "holdings", "group",
        "services", "service", "solutions", "sacco", "and", "the", "of"
    };

    /// <summary>
    /// Canonical form of an email address for duplicate detection. Lower-cases, folds provider
    /// aliasing, and leaves anything unrecognised alone rather than guessing.
    /// </summary>
    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var trimmed = email.Trim().ToLowerInvariant();
        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1) return trimmed;

        var local = trimmed[..at];
        var domain = trimmed[(at + 1)..];

        // Treat the alias domains as their canonical spelling so the two Gmail hostnames collapse.
        if (string.Equals(domain, "googlemail.com", StringComparison.OrdinalIgnoreCase))
        {
            domain = "gmail.com";
        }

        var plus = local.IndexOf('+');
        if (plus >= 0 && (DotAndPlusAliasingDomains.Contains(domain) || PlusAliasingDomains.Contains(domain)))
        {
            local = local[..plus];
        }

        if (DotAndPlusAliasingDomains.Contains(domain))
        {
            local = local.Replace(".", string.Empty);
        }

        // A local part that folds away entirely (e.g. "+tag@gmail.com") is not usable as an
        // identity; keep the original rather than collapsing every such address onto one key.
        return string.IsNullOrEmpty(local) ? trimmed : $"{local}@{domain}";
    }

    /// <summary>The domain part of an address, lower-cased, or null when it has none.</summary>
    public static string? EmailDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.Trim().LastIndexOf('@');
        return at <= 0 || at == email.Trim().Length - 1 ? null : email.Trim()[(at + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// Digits-only phone form, with a Uganda-aware trunk-prefix fold so "0753404044",
    /// "+256753404044" and "256753404044" compare equal. Deliberately not a full E.164 parser:
    /// this only has to survive the formatting variance a real person types.
    /// </summary>
    public static string? NormalizePhone(string? phone, string defaultCountryCode = "256")
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var digits = NonDigitsRegex().Replace(phone.Trim(), string.Empty);
        if (digits.Length == 0) return null;

        // Local form: a single leading zero replaced by the country code.
        if (digits.StartsWith('0') && !digits.StartsWith("00"))
        {
            digits = defaultCountryCode + digits.TrimStart('0');
        }
        else if (digits.StartsWith("00"))
        {
            digits = digits[2..];
        }

        return digits.Length == 0 ? null : digits;
    }

    /// <summary>
    /// Canonical business name: lower-cased, punctuation removed, legal and filler words dropped,
    /// remaining words sorted so word order cannot disguise a repeat. "Kampala Pharmacy Ltd." and
    /// "The Pharmacy, Kampala" both reduce to "kampala pharmacy".
    /// </summary>
    public static string? NormalizeOrganizationName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var cleaned = NonAlphanumericRegex().Replace(name.Trim().ToLowerInvariant(), " ");
        var words = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => !NameNoiseWords.Contains(w))
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToArray();

        // Everything was noise ("The Company Ltd"). Fall back to the cleaned original so the record
        // still gets a key rather than colliding with every other all-noise name.
        if (words.Length == 0)
        {
            var fallback = cleaned.Replace(" ", string.Empty);
            return string.IsNullOrEmpty(fallback) ? null : fallback;
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Coarse bucket key for a name, so a near-match search only ever compares a handful of rows
    /// instead of scanning the table. Built from the first three characters of the alphabetically
    /// first significant word plus a length band, which keeps genuine variants together while
    /// staying cheap to index.
    /// <para>
    /// This is the "narrow in SQL, score in memory" half of duplicate detection. It is intentionally
    /// crude: a bucket that is slightly too wide costs a few extra in-process comparisons, whereas a
    /// database extension for fuzzy matching was ruled out.
    /// </para>
    /// </summary>
    public static string? BuildNameBlockingKey(string? name)
    {
        var normalized = NormalizeOrganizationName(name);
        if (string.IsNullOrEmpty(normalized)) return null;

        var first = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? normalized;
        var head = first.Length <= 3 ? first : first[..3];
        var lengthBand = normalized.Replace(" ", string.Empty).Length / 4;
        return $"{head}:{lengthBand}";
    }

    /// <summary>
    /// Similarity between two normalized names, from 0 to 1. Combines how many words the two share
    /// with how close the remaining characters are, so both "kampala pharmacy" versus
    /// "kampala pharmacy clinic" and "kampala pharmacy" versus "kampala pharmacyy" score high.
    /// </summary>
    public static double NameSimilarity(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;
        if (string.Equals(left, right, StringComparison.Ordinal)) return 1;

        var leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightWords = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (leftWords.Count == 0 || rightWords.Count == 0) return 0;

        var shared = leftWords.Intersect(rightWords, StringComparer.Ordinal).Count();
        var tokenScore = (double)shared / Math.Max(leftWords.Count, rightWords.Count);

        var a = left.Replace(" ", string.Empty);
        var b = right.Replace(" ", string.Empty);
        var distance = LevenshteinDistance(a, b);
        var charScore = 1.0 - (double)distance / Math.Max(a.Length, b.Length);

        // Weighted toward token overlap: a shared distinctive word says more about two business
        // names being the same applicant than the raw edit distance between them does.
        return Math.Clamp((tokenScore * 0.6) + (charScore * 0.4), 0, 1);
    }

    /// <summary>
    /// Standard edit distance, two-row variant so memory stays proportional to the shorter string.
    /// Hand-rolled rather than taken from a package, because this project does not add server
    /// dependencies and the algorithm is short enough to read.
    /// </summary>
    public static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>Stable hash of a value, for storing an IP or fingerprint without keeping the raw one.</summary>
    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    [GeneratedRegex(@"[^\d]")]
    private static partial Regex NonDigitsRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();
}
