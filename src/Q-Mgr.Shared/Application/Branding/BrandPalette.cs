using System.Globalization;
using System.Text.RegularExpressions;

namespace QMgr.Application.Branding;

/// <summary>
/// Turns an organization's three chosen colours into the FULL set of <c>--qm-*</c> custom
/// properties a page actually reads. The ONE home for that, and it exists because the first cut of
/// white-labelling did not work and it was not obvious why.
///
/// THE BUG THIS FIXES. Every branded surface used to override three tokens — <c>--qm-primary</c>,
/// <c>--qm-secondary</c>, <c>--qm-accent-orange</c> — and `qm-theme.css` defines SEVEN more that
/// are hardcoded wine literals rather than derived from those three:
/// <c>--qm-primary-dark</c> (every hover state), <c>--qm-primary-light</c> (every selected chip,
/// tint and highlight), <c>--qm-primary-glow</c>, <c>--qm-primary-rgb</c> (every
/// <c>rgba(var(--qm-primary-rgb), a)</c> wash — the kiosk, the share page, the welfare timeline)
/// and the two <c>--qm-secondary-*</c>. So a school that picked green got green buttons on wine
/// hovers, with wine tints behind them. That reads as "the white-labelling does not work", and it
/// was right.
///
/// Derived rather than asked for: a tenant picks one colour and gets a coherent set. Asking them
/// for ten would be asking them to do a designer's job to make a logo upload work.
///
/// NO COLOUR LIBRARY. The arithmetic is a mix toward black and a luminance test, which is the
/// standing no-new-dependency rule and about thirty lines.
/// </summary>
public static class BrandPalette
{
    private static readonly Regex Hex = new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

    /// <summary>
    /// The style attribute for a branded wrapper, or empty when nothing valid was supplied.
    /// Empty means the element renders no attribute at all and the standard palette applies —
    /// which is the fallback every caller here wants.
    /// </summary>
    public static string StyleFor(string? primary, string? secondary, string? accent)
    {
        var overrides = new List<string>(12);

        if (Parse(primary) is { } p)
        {
            overrides.Add($"--qm-primary: {p.Hex}");
            overrides.Add($"--qm-primary-dark: {Darken(p, 0.22)}");
            // The alphas match qm-theme.css's own dark-theme values, so a branded surface has the
            // same weight of tint as an unbranded one rather than a subtly different one.
            overrides.Add($"--qm-primary-light: rgba({p.R}, {p.G}, {p.B}, 0.15)");
            overrides.Add($"--qm-primary-glow: rgba({p.R}, {p.G}, {p.B}, 0.4)");
            overrides.Add($"--qm-primary-rgb: {p.R}, {p.G}, {p.B}");
            // White on a pale brand colour is unreadable, and a school is perfectly entitled to
            // pick yellow. The text that sits ON the brand follows the brand's own lightness.
            overrides.Add($"--qm-text-on-primary: {(IsLight(p) ? "#1f1f1f" : "#ffffff")}");
        }

        if (Parse(secondary) is { } s)
        {
            overrides.Add($"--qm-secondary: {s.Hex}");
            overrides.Add($"--qm-secondary-dark: {Darken(s, 0.22)}");
            overrides.Add($"--qm-secondary-light: rgba({s.R}, {s.G}, {s.B}, 0.15)");
        }

        // The theme's "accent" slot. Named -orange for its default value rather than its role,
        // which is why a tenant's accent lands here and not on a token called --qm-accent.
        if (Parse(accent) is { } a)
            overrides.Add($"--qm-accent-orange: {a.Hex}");

        return string.Join("; ", overrides);
    }

    /// <summary>
    /// The SAME derivation as <see cref="StyleFor"/>, returned as values rather than as a CSS
    /// string, so a caller that is not a browser can use it.
    ///
    /// <para><b>Why this exists.</b> The native shell was sent three colours — primary, secondary,
    /// accent — and had to guess everything else: the hover, the tint behind a chip, the text
    /// colour that sits ON the brand. That is precisely the bug this class was written to fix on
    /// the web, where setting three tokens left seven hardcoded wine literals behind and a school
    /// that chose green got green buttons with wine hovers. Shipping three colours to the app
    /// recreated it one platform over.</para>
    ///
    /// <para>Returning it from here rather than deriving it again in the app is the point: one
    /// implementation, so the web chrome and the native chrome cannot drift. A null result means
    /// the caller supplied nothing usable and should keep its own default palette.</para>
    /// </summary>
    public static BrandColors? ColorsFor(string? primary, string? secondary, string? accent)
    {
        if (Parse(primary) is not { } p) return null;
        var s = Parse(secondary);
        var a = Parse(accent);

        return new BrandColors
        {
            Primary = p.Hex,
            PrimaryDark = Darken(p, 0.22),
            // A pale brand with a light-mixed hover is how a "branded" surface ends up invisible.
            // Mixing toward white by the same amount keeps the pair symmetrical.
            PrimaryLight = Lighten(p, 0.22),
            PrimaryRgb = $"{p.R}, {p.G}, {p.B}",
            // The one value the app cannot compute without the WCAG formula, and the one that makes
            // a yellow-branded school readable rather than white-on-white.
            TextOnPrimary = IsLight(p) ? "#1f1f1f" : "#ffffff",
            Secondary = s?.Hex,
            SecondaryDark = s is { } sv ? Darken(sv, 0.22) : null,
            Accent = a?.Hex,
            // The app paints a full-bleed ground behind its sign-in screen; a mid-tone brand is too
            // loud for that, so it gets the darkened form rather than the brand itself.
            Ground = Darken(p, 0.35)
        };
    }

    /// <summary>Mixed toward white, the mirror of <see cref="Darken"/>.</summary>
    private static string Lighten(Rgb c, double amount)
    {
        var k = Math.Clamp(amount, 0, 1);
        int Mix(int v) => (int)(v + (255 - v) * k);
        return $"#{Mix(c.R):x2}{Mix(c.G):x2}{Mix(c.B):x2}";
    }

    private readonly record struct Rgb(int R, int G, int B, string Hex);

    private static Rgb? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!Hex.IsMatch(text)) return null;

        var body = text[1..];
        if (body.Length == 3) body = string.Concat(body.Select(c => new string(c, 2)));

        return new Rgb(
            int.Parse(body[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(body.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(body.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            "#" + body.ToLowerInvariant());
    }

    /// <summary>Mixed toward black. The same relationship the shipped wine has with its own -dark.</summary>
    private static string Darken(Rgb c, double amount)
    {
        var k = 1 - Math.Clamp(amount, 0, 1);
        return $"#{(int)(c.R * k):x2}{(int)(c.G * k):x2}{(int)(c.B * k):x2}";
    }

    /// <summary>
    /// WCAG relative luminance, which is what decides whether black or white text is readable on a
    /// colour. The 0.45 threshold is a little above the formal 0.5 crossover on purpose: white on a
    /// mid-tone reads better than black does.
    /// </summary>
    private static bool IsLight(Rgb c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B) > 0.45;
    }
}

/// <summary>
/// A tenant's palette, fully derived. Every field here exists because something rendered wrong
/// without it: <see cref="PrimaryDark"/> is every hover, <see cref="PrimaryLight"/> every chip and
/// tint, <see cref="PrimaryRgb"/> every <c>rgba(var(--qm-primary-rgb), α)</c> wash, and
/// <see cref="TextOnPrimary"/> is what stops white text on a yellow brand.
///
/// <para>Hex strings rather than numbers, because every consumer — CSS, MAUI, Android XML — parses
/// hex, and a platform-specific numeric form would have to be converted back anyway.</para>
/// </summary>
public record BrandColors
{
    public string Primary { get; init; } = string.Empty;
    public string PrimaryDark { get; init; } = string.Empty;
    public string PrimaryLight { get; init; } = string.Empty;

    /// <summary>"122, 40, 71" — the triple, for building an rgba() at any alpha.</summary>
    public string PrimaryRgb { get; init; } = string.Empty;

    /// <summary>Black or white, chosen by WCAG relative luminance against the brand.</summary>
    public string TextOnPrimary { get; init; } = string.Empty;

    public string? Secondary { get; init; }
    public string? SecondaryDark { get; init; }
    public string? Accent { get; init; }

    /// <summary>The full-bleed background behind the app's sign-in screen.</summary>
    public string Ground { get; init; } = string.Empty;
}
