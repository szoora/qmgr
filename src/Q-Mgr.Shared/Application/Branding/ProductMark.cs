using System.Globalization;
using System.Text;

namespace QMgr.Application.Branding;

/// <summary>
/// THE MARK, AS GEOMETRY (2026-09-24, decision B6): an S made of dashboard panels. Eleven lit tiles on
/// a three-by-five grid spell SACC's initial; the four dim ones say "a board of many things".
///
/// <para>ONE HOME for the drawing. The Web serves every <c>/brand/*.svg</c> from this class
/// (<c>Program.cs</c>), the API's <c>branding/product-logo</c> — what the mobile app fetches — emits the
/// same, and the mobile app's rasters are generated from <c>/brand/app-icon.svg</c>. There are no SVG
/// files to fall out of step with each other: the old queue-loop mark existed in five hand-written
/// copies, one of them still in the blue the product dropped on 2026-08-19.</para>
///
/// <para>Flat, as every decision since 2026-08-19 requires: one wine ground, white tiles, no gradient,
/// no glow.</para>
/// </summary>
public static class ProductMark
{
    /// <summary>The wine, dark theme — <c>--qm-primary</c> in <c>qm-theme.css</c>.</summary>
    public const string WineDark = "#8c2f52";

    /// <summary>The wine, light theme.</summary>
    public const string WineLight = "#7a2847";

    // A 512 box: 3 columns x 5 rows of 56px tiles with 12px gutters, centred.
    private const int Tile = 56, Gap = 12, Left = 160, Top = 92, Radius = 8;

    /// <summary>Which tiles are lit, row by row. Reading the rows top to bottom draws the S.</summary>
    private static readonly bool[][] Lit =
    {
        new[] { true,  true,  true  },
        new[] { true,  false, false },
        new[] { true,  true,  true  },
        new[] { false, false, true  },
        new[] { true,  true,  true  },
    };

    /// <summary>The app-icon tile: wine ground with rounded corners, white S, dim unlit tiles.</summary>
    public static string Tile512(string ground = WineLight) => Svg(ground, rounded: true, ink: "#ffffff", dim: "rgba(255,255,255,0.20)");

    /// <summary>For a light surface: a white tile with a hairline, wine S.</summary>
    public static string TileLight() => Svg("#ffffff", rounded: true, ink: WineLight, dim: "rgba(122,40,71,0.14)", ring: "rgba(122,40,71,0.18)");

    /// <summary>Maskable: full bleed (the launcher applies its own shape); the S already sits in the inner 80%.</summary>
    public static string Maskable() => Svg(WineLight, rounded: false, ink: "#ffffff", dim: "rgba(255,255,255,0.20)");

    /// <summary>No ground, one colour, lit tiles only — for a mono printer, or a mark over a coloured bar.</summary>
    public static string Mono(string ink = "#000000") => Svg(null, rounded: false, ink: ink, dim: null);

    /// <summary>
    /// The mark drawn for 16–32px: the tiles would blur together, so the S is one solid path on the tile.
    /// </summary>
    public static string Favicon(string ground = WineLight) =>
        $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="6" fill="{ground}"/><path d="M10 6h13v4H14v4h9v12H9v-4h10v-4h-9z" fill="#ffffff"/></svg>""";

    private static string Svg(string? ground, bool rounded, string ink, string? dim, string? ring = null)
    {
        var sb = new StringBuilder();
        sb.Append("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label=""");
        sb.Append('"').Append(ProductBrand.Name).Append("\">");
        if (ground != null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<rect width="512" height="512" {(rounded ? "rx=\"96\" " : "")}fill="{ground}"/>""");
            if (ring != null)
                sb.Append(CultureInfo.InvariantCulture, $"""<rect x="4" y="4" width="504" height="504" rx="92" fill="none" stroke="{ring}" stroke-width="8"/>""");
        }

        for (var r = 0; r < Lit.Length; r++)
        for (var c = 0; c < Lit[r].Length; c++)
        {
            var fill = Lit[r][c] ? ink : dim;
            if (fill == null) continue;
            var x = Left + c * (Tile + Gap);
            var y = Top + r * (Tile + Gap);
            sb.Append(CultureInfo.InvariantCulture, $"""<rect x="{x}" y="{y}" width="{Tile}" height="{Tile}" rx="{Radius}" fill="{fill}"/>""");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }
}
