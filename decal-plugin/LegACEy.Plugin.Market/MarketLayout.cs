namespace LegACEy.Plugin.Market;

/// <summary>How the Market window is laid out: the side bar, the tab row or the icon rail, and what the top bar shows.</summary>
public enum MarketLayoutMode { Narrow, Medium, Wide }

/// <summary>
/// The one rule for the layout: the window's width picks the mode. Only the window measures; the views are told the mode and switch on it.
/// </summary>
public static class MarketLayout
{
    /// <summary>From this width the window is wide: the full side bar, the Vault space in the top bar.</summary>
    public const double WideFrom = 1100;

    /// <summary>From this width the window is medium: the side bar is an icon rail. Below it, narrow: a tab row.</summary>
    public const double MediumFrom = 760;

    public static MarketLayoutMode ModeFor(double width) =>
        width >= WideFrom ? MarketLayoutMode.Wide : width >= MediumFrom ? MarketLayoutMode.Medium : MarketLayoutMode.Narrow;
}
