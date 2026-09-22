namespace QMgr.Application.DTOs;

public enum DisplayBannerPosition
{
    Bottom,
    Top
}

public enum DisplayBannerDirection
{
    RightToLeft,
    LeftToRight
}

/// <summary>
/// A branch's scrolling ticker/marquee banner, shown on both CustomerDisplay
/// and SignageDisplay when enabled. Opt-in — defaults to disabled with no
/// messages, so a branch that's never configured one gets nothing rendered.
/// </summary>
public class DisplayBannerSettingsDto
{
    public bool Enabled { get; set; } = false;
    public DisplayBannerPosition Position { get; set; } = DisplayBannerPosition.Bottom;
    public DisplayBannerDirection Direction { get; set; } = DisplayBannerDirection.RightToLeft;

    /// <summary>Seconds for one full scroll loop — lower is faster.</summary>
    public int SpeedSeconds { get; set; } = 30;

    /// <summary>
    /// NULL MEANS THE TENANT'S OWN BRAND, and that is the default. These used to be a hard-coded
    /// "#8c2f52" and "#ffffff" — Q-Mgr's own shipped wine — so every school's ticker was wine
    /// however they had branded the rest of the product. It is the same fault --qm-info had: a
    /// literal is invisible to white-labelling until somebody rebrands, and then it is the one
    /// thing on the screen that did not follow.
    ///
    /// <para>A stored hex still wins, because a banner is read across a foyer and a school may
    /// genuinely want a colour the rest of the app does not use. Null is simply the default, and
    /// the editor's first swatch stores null — the same shape as a welfare category's colour.</para>
    /// </summary>
    public string? BackgroundColor { get; set; }

    /// <summary>Null derives from the background, via --qm-text-on-primary. See above.</summary>
    public string? TextColor { get; set; }

    public List<string> Messages { get; set; } = new();
}
