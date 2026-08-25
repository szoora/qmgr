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

    public string BackgroundColor { get; set; } = "#8c2f52";
    public string TextColor { get; set; } = "#ffffff";

    public List<string> Messages { get; set; } = new();
}
