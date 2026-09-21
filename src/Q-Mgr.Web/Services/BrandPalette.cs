using System.Globalization;
using System.Text.RegularExpressions;

namespace QMgr.Web.Services;

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
