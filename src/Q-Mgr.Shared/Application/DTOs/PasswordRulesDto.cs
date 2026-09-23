namespace QMgr.Application.DTOs;

/// <summary>
/// The password rules, in the form a page needs to STATE them.
///
/// <para>Deliberately a narrow projection of <c>PasswordPolicySettings</c> rather than that type
/// itself: this is served anonymously, and the rest of the security settings — lockout thresholds,
/// session lifetimes, history depth, expiry — are nobody's business but a platform
/// administrator's.</para>
///
/// <para><b>Why it exists at all.</b> Every password form in this product hardcoded its own number.
/// The set-password page said "At least 12 characters" whatever an administrator had configured, so
/// an admin who set 8 got a form promising 12, and an admin who set 14 got a form promising 12 and
/// a server refusing at 13 — in different words, after the person had typed it twice. A rule stated
/// in one place and enforced in another will always drift; this is the one place.</para>
/// </summary>
public record PasswordRulesDto
{
    public int MinimumLength { get; init; } = 12;
    public int MaximumLength { get; init; } = 128;
    public bool RequireUppercase { get; init; }
    public bool RequireLowercase { get; init; }
    public bool RequireDigits { get; init; }
    public bool RequireSpecialCharacters { get; init; }
    public bool PreventCommonPasswords { get; init; }
    public bool PreventUserInfoInPassword { get; init; }

    /// <summary>
    /// The placeholder for a password box — the LENGTH only, because that is the one rule somebody
    /// can act on while typing the first character.
    /// </summary>
    public string Placeholder => $"At least {MinimumLength} characters";

    /// <summary>
    /// One sentence naming every rule that is switched on, or a short reassurance when none is.
    ///
    /// <para>Composed here so the wording cannot differ between the four pages that ask for a
    /// password. It lists only what is ACTUALLY enforced: a form that recites requirements the
    /// server does not apply teaches people to ignore it.</para>
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (RequireUppercase && RequireLowercase) parts.Add("upper and lower case");
            else if (RequireUppercase) parts.Add("a capital letter");
            else if (RequireLowercase) parts.Add("a lower-case letter");
            if (RequireDigits) parts.Add("a number");
            if (RequireSpecialCharacters) parts.Add("a symbol");

            var rules = parts.Count == 0
                ? $"At least {MinimumLength} characters. Length is what matters — a memorable phrase beats a short, complicated password."
                : $"At least {MinimumLength} characters, including {Join(parts)}.";

            // Both of these refuse a password rather than shaping it, so they are stated as advice
            // rather than as another box to tick.
            if (PreventCommonPasswords || PreventUserInfoInPassword)
                rules += " Avoid your name, your school's name and passwords in common use.";

            return rules;
        }
    }

    private static string Join(List<string> parts)
        => parts.Count == 1 ? parts[0]
         : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}
