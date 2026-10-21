using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// The Dereth chat window's message colours, retail's own (ChatInterface::BuildChatColorLookupTable): one per chat type, and one per kind of
/// line for a line without a type and for the talk targets' colours.
/// </summary>
public static class DerethChatPalette
{
    public static readonly Color Say = Color.Parse("#FFFFFF");
    public static readonly Color Emote = Color.Parse("#D2D2C8");
    public static readonly Color Tell = Color.Parse("#FFFF3F");
    public static readonly Color TellOut = Color.Parse("#D2D264");
    public static readonly Color Fellowship = Tell;
    public static readonly Color Allegiance = Color.Parse("#EE921E");
    public static readonly Color Channel = Color.Parse("#B4DCF0");
    public static readonly Color System = Color.Parse("#FF7FFF");
    /// <summary>Retail's colour for a type it gives none of its own, such as a broadcast.</summary>
    public static readonly Color Plugin = Color.Parse("#80FF7F");
    public static readonly Color Combat = Color.Parse("#FF3F3F");
    /// <summary>A plugin's link in a line (GoArrow's coordinates), as retail draws it; a sender's name keeps the line's colour.</summary>
    public static readonly Color Link = Color.Parse("#00B200");

    /// <summary>The colour retail draws a line of this chat type (eChatTypes) in.</summary>
    public static Color OfType(uint type) => type switch
    {
        2 => Say,
        12 => Emote,
        3 or 10 or 19 or 31 => Tell,
        4 or 11 => TellOut,
        8 or 9 => Color.Parse("#FF9696"),
        18 or 33 => Allegiance,
        14 or 27 or 28 or 29 or 30 or 32 => Channel,
        6 or 15 or 21 => Combat,
        22 => Color.Parse("#F57572"),
        7 or 17 => Color.Parse("#3FBFFF"),
        13 => Color.Parse("#3FDCDC"),
        5 => System,
        26 => Color.Parse("#FF0000"),
        _ => Plugin
    };
    /// <summary>The time at the start of a line: quieter than any message colour.</summary>
    public static readonly Color Timestamp = Color.Parse("#8A8370");
    /// <summary>The chip's dark fill.</summary>
    public static readonly Color ChipFill = Color.Parse("#0A0906");
    /// <summary>The jump pill's fill, and the talk-target menu's hovered row.</summary>
    public static readonly Color PillFill = Color.Parse("#2A2219");
    /// <summary>The dark outline of a selected tab and of the chip: the theme's gold, shaded. Also the fill of a tab's unread count.</summary>
    public static readonly Color GoldDark = Color.Parse("#8A6326");
    public static readonly IBrush GoldDarkBrush = DerethPalette.Brush(GoldDark);
    /// <summary>The fill of a tab's unread count when a new tell from someone is in it, and the cream text on it.</summary>
    public static readonly Color TellFill = Color.Parse("#8A6E22");
    public static readonly Color TellText = Color.Parse("#FFF3CF");
}

/// <summary>
/// The parts the chat window adds to the Dereth theme: the tab, the target chip, the jump pill and the log's text. Each is a plain
/// control, so the window arranges them and the theme styles them.
/// </summary>
public static class DerethChatParts
{
    /// <summary>The chip's caret: the mockup's small up-pointing triangle, in gold.</summary>
    private const string CaretData = "M1.5 6.5L5 2.5L8.5 6.5Z";

    /// <summary>
    /// A tab label on the tab strip. The selected tab shows the dark fill and the gold outline, as the mockup does. A tab with lines it has
    /// not shown carries its count in a badge after the name; a new tell from someone makes the badge the tell colour.
    /// </summary>
    public static Border Tab(string text, bool selected, int missed = 0, bool tell = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = DerethPalette.Title,
            FontSize = 11,
            Foreground = selected ? DerethPalette.CreamBrush : DerethPalette.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { label } };
        if (missed > 0) content.Children.Add(Badge(missed, tell));
        return new Border
        {
            Height = selected ? 19 : 17,
            Padding = new Thickness(7, 0),
            Margin = new Thickness(0, 0, 1, 0),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            BorderBrush = selected ? DerethChatPalette.GoldDarkBrush : Brushes.Transparent,
            Background = selected ? DerethPalette.Brush(Color.FromArgb(0xD9, 0x2A, 0x22, 0x19)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content
        };
    }

    /// <summary>The tab strip's plus: a new tab's dialog opens from it.</summary>
    public static Border AddTab() => new()
    {
        Height = 17,
        Padding = new Thickness(5, 0),
        Margin = new Thickness(0, 0, 1, 0),
        VerticalAlignment = VerticalAlignment.Bottom,
        Cursor = new Cursor(StandardCursorType.Hand),
        Child = new TextBlock { Text = "+", FontFamily = DerethPalette.Title, FontSize = 11, Foreground = DerethPalette.GoldBrush, VerticalAlignment = VerticalAlignment.Center }
    };

    /// <summary>The text size button at the end of the tab strip, beside the add button.</summary>
    public static Border TextSizeTab() => new()
    {
        Height = 17,
        Padding = new Thickness(4, 0),
        Margin = new Thickness(0, 0, 1, 0),
        VerticalAlignment = VerticalAlignment.Bottom,
        Background = Brushes.Transparent,
        Cursor = new Cursor(StandardCursorType.Hand),
        Child = new TextBlock { Text = "Aa", FontFamily = DerethPalette.Title, FontSize = 10, Foreground = DerethPalette.GoldBrush, VerticalAlignment = VerticalAlignment.Center }
    };

    /// <summary>
    /// A torn-off window's close medallion, at the end of its tab strip: the frame's close X in a small button. A click merges the window's
    /// tabs back into the main window.
    /// </summary>
    public static DerethButton CloseMedallion() => new()
    {
        Width = 16,
        Height = 16,
        Padding = new Thickness(0),
        Margin = new Thickness(3, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Content = new DerethSprite(DerethSpriteArt.Close) { Width = 10, Height = 10 }
    };

    /// <summary>The unread count on a tab: gold, or the brighter tell gold when a new tell from someone is in it.</summary>
    private static Border Badge(int count, bool tell) => new()
    {
        MinWidth = 9,
        Height = 12,
        Padding = new Thickness(3, 0),
        CornerRadius = new CornerRadius(6),
        VerticalAlignment = VerticalAlignment.Center,
        Background = DerethPalette.Brush(tell ? DerethChatPalette.TellFill : DerethChatPalette.GoldDark),
        Child = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture),
            FontFamily = DerethPalette.Body,
            FontSize = 8,
            FontWeight = FontWeight.Bold,
            Foreground = tell ? DerethPalette.Brush(DerethChatPalette.TellText) : DerethPalette.CreamBrush,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>The target chip: the target's label in the target's colour, a gold caret, on the dark fill with a gold outline.</summary>
    public static Border Chip(string label, Color colour)
    {
        var text = DerethPalette.CentreInk(new TextBlock
        {
            Text = label,
            FontFamily = DerethPalette.Title,
            FontSize = 11,
            Foreground = DerethPalette.Brush(colour),
            VerticalAlignment = VerticalAlignment.Center
        });
        var caret = new Path
        {
            Data = Geometry.Parse(CaretData),
            Fill = DerethPalette.GoldBrush,
            Stretch = Stretch.Fill,
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        return new Border
        {
            Height = 19,
            Padding = new Thickness(7, 0, 5, 0),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = DerethChatPalette.GoldDarkBrush,
            Background = DerethPalette.Brush(DerethChatPalette.ChipFill),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { text, caret } }
        };
    }

    /// <summary>The jump pill: how many lines arrived while the log was scrolled up, in the given label. A click jumps back to the newest line.</summary>
    public static Border JumpPill(TextBlock label) => new()
    {
        Padding = new Thickness(9, 2),
        CornerRadius = new CornerRadius(9),
        Background = DerethPalette.Brush(DerethChatPalette.PillFill),
        BorderBrush = DerethPalette.TealBrush,
        BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Cursor = new Cursor(StandardCursorType.Hand),
        Child = label
    };

    /// <summary>A line's text run: the Dereth body font at 11 DIP. The line's text is one selectable block of these runs.</summary>
    public static Run TextRun(string text, IBrush brush, bool italic = false) => new()
    {
        Text = text,
        FontFamily = DerethPalette.Body,
        FontSize = 11,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        Foreground = brush
    };

    /// <summary>A line's time, in the quiet timestamp colour and the mockup's 9.5 DIP mono figures, as a run.</summary>
    public static Run TimestampRun(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("ui-monospace, Cascadia Mono, Consolas, monospace"),
        FontSize = 9.5,
        Foreground = DerethPalette.Brush(DerethChatPalette.Timestamp)
    };
}
