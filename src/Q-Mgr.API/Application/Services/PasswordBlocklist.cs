using System.Text;

namespace QMgr.API.Application.Services;

/// <summary>
/// The password blocklist every password set on this platform is compared against (duty rota plan
/// §12.2 and §13.16). NIST SP 800-63B-4 §3.1.1.2: verifiers "SHALL compare the prospective secret
/// against a blocklist" of commonly used, expected and compromised values, including
/// "context-specific words, such as the name of the service, the username". OWASP ASVS V2.5.4 rules
/// out shared or default accounts, which is what a school-wide "staff" password would be.
/// <para>
/// <b>The rule for a common or context word:</b> the password is refused when, read with look-alike
/// characters undone ("St@ff" reads "staff"), it contains the word AND it is essentially that word —
/// at most three letters besides it, the rest digits and symbols. So "staff", "Staff2026!" and
/// "St@ff#1" are refused, while a passphrase that merely contains a common word ("staff room kettle
/// by the window") is not. The cost of a false refusal is choosing another password; the cost of a
/// false pass is a guessable account that reaches children's records.
/// </para>
/// <para>
/// One home, like <c>RegistrationIdentity</c>: every place that sets a password goes through
/// <see cref="PasswordValidationService"/>, which calls this. Do not write a second list.
/// </para>
/// </summary>
public static class PasswordBlocklist
{
    /// <summary>The service's own names. Never part of a password (NIST: the name of the service).</summary>
    private static readonly string[] ServiceWords = { "qmgr", "qmanager", "sacc", "frontoffice" };

    /// <summary>
    /// Words a school's password is most often built from. "staff" first: it is the shared default the
    /// plan was asked to use and declined.
    /// </summary>
    private static readonly string[] ContextWords =
    {
        "staff", "teacher", "teachers", "school", "student", "students", "pupil", "classroom", "headteacher",
        "principal", "academic", "college", "academy", "education", "uganda", "kampala", "welcome", "changeme",
        "password", "passwd", "letmein", "default", "temporary", "admin", "administrator", "login", "secret",
        "monday", "january", "summer", "winter", "term"
    };

    /// <summary>
    /// The passwords that head every breach corpus (SecLists, NCSC's 100k list). Matched under the same
    /// "essentially this word" rule, so a capital, a year or a "!" does not get one past.
    /// </summary>
    private static readonly string[] Common =
    {
        "password", "qwerty", "qwertyuiop", "asdfgh", "asdfghjkl", "zxcvbnm", "qazwsx", "iloveyou", "monkey",
        "dragon", "letmein", "trustno", "baseball", "football", "soccer", "master", "sunshine", "princess",
        "shadow", "superman", "batman", "michael", "jennifer", "jordan", "hunter", "ashley", "bailey", "charlie",
        "jesus", "ninja", "mustang", "starwars", "freedom", "whatever", "google", "hello", "loveme", "flower",
        "lovely", "cheese", "computer", "internet", "samsung", "iphone", "blessed", "blessing", "godisgood",
        "chelsea", "arsenal", "liverpool", "manchester", "christ", "faith", "grace", "mother", "father",
        "family", "friend", "money", "killer", "pokemon", "abcdef", "abc"
    };

    /// <summary>Null when the password is acceptable, otherwise a message naming the rule broken.</summary>
    public static string? Check(string password, string? username, string? email, string? organizationName, IEnumerable<string>? alsoRefuse)
    {
        if (string.IsNullOrEmpty(password)) return null; // the length rule reports an empty password

        foreach (var value in alsoRefuse ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrEmpty(value) && string.Equals(password.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                return "Choose a password different from the temporary one you were given.";
        }

        var folded = Fold(password);
        var letters = password.Count(char.IsLetter);

        if (IsRunOrRepeat(password))
            return "This password is too easy to guess. Choose something less predictable.";

        foreach (var word in Common)
            if (IsEssentially(folded, letters, word))
                return "This password is too common or too easy to guess. Choose something less predictable.";

        foreach (var word in ContextWords)
            if (IsEssentially(folded, letters, word))
                return $"Passwords built on common words such as \"{word}\" are refused. Choose something less predictable.";

        foreach (var word in ServiceWords)
            if (folded.Contains(word))
                return "A password must not contain the name of this service.";

        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            var whole = Fold(organizationName);
            if (whole.Length >= 4 && folded.Contains(whole))
                return "A password must not be built on your organization's name.";
            foreach (var part in organizationName.Split(' ', '-', '_', '.', ',', '\'', '&', '(', ')', '/'))
            {
                var p = Fold(part);
                if (p.Length >= 5 && !IsGenericOrganizationWord(p) && folded.Contains(p))
                    return "A password must not be built on your organization's name.";
            }
        }

        var user = Fold(username ?? string.Empty);
        if (user.Length >= 3 && folded.Contains(user))
            return "A password must not contain your username.";

        var local = Fold((email ?? string.Empty).Split('@')[0]);
        if (local.Length >= 3 && folded.Contains(local))
            return "A password must not contain your email address.";

        return null;
    }

    /// <summary>Contains the word, and has at most three letters besides it.</summary>
    private static bool IsEssentially(string folded, int letterCount, string word)
        => folded.Contains(word) && letterCount - word.Length <= 3;

    /// <summary>Lower-cased, look-alike digits and symbols read as the letters they stand for, everything else non-alphanumeric removed.</summary>
    public static string Fold(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var raw in value.ToLowerInvariant())
        {
            var c = raw switch
            {
                '@' or '4' => 'a',
                '3' => 'e',
                '1' or '!' or '|' => 'i',
                '0' => 'o',
                '$' or '5' => 's',
                '7' or '+' => 't',
                _ => raw
            };
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"aaaaaaaa", "abababab", "12345678", "abcdefgh".</summary>
    private static bool IsRunOrRepeat(string password)
    {
        var p = password.ToLowerInvariant();
        if (p.Distinct().Count() <= 2) return true;
        var ascending = true;
        for (var i = 1; i < p.Length && ascending; i++)
            ascending = p[i] == p[i - 1] + 1;
        return ascending && p.Length >= 4;
    }

    private static bool IsGenericOrganizationWord(string p) => p is "school" or "schools" or "college" or "academy" or "primary"
        or "secondary" or "senior" or "junior" or "international" or "limited" or "company" or "trust" or "group";
}
