using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>The Dereth chat window's message colours and small parts, from the chat mockup (direction B): one colour per kind of line.</summary>
public static class DerethChatPalette
{
    public static readonly Color Say = Color.Parse("#EFE3C4");
    public static readonly Color Emote = Color.Parse("#BDB39A");
    public static readonly Color Tell = Color.Parse("#E8C66A");
    public static readonly Color Fellowship = Color.Parse("#86D394");
    public static readonly Color Allegiance = Color.Parse("#7DB8EA");
    public static readonly Color Channel = Color.Parse("#5FC9C4");
    public static readonly Color System = Color.Parse("#8E979E");
    public static readonly Color Plugin = Color.Parse("#A6CF6E");
    public static readonly Color Combat = Color.Parse("#D9906E");
    /// <summary>The time at the start of a line: quieter than any message colour.</summary>
    public static readonly Color Timestamp = Color.Parse("#5D6870");
    /// <summary>The chip's dark fill.</summary>
    public static readonly Color ChipFill = Color.Parse("#0B1117");
    /// <summary>The jump pill's fill, and the talk-target menu's hovered row.</summary>
    public static readonly Color PillFill = Color.Parse("#0E1923");
    /// <summary>The dark outline of a selected tab and of the chip: the theme's gold, shaded.</summary>
    public static readonly Color GoldDark = Color.Parse("#7A5E30");
    public static readonly IBrush GoldDarkBrush = DerethPalette.Brush(GoldDark);
}

/// <summary>
/// The parts the chat window adds to the Dereth theme: the tab, the target chip, the jump pill and the log's text. Each is a plain
/// control, so the window arranges them and the theme styles them.
/// </summary>
public static class DerethChatParts
{
    /// <summary>The chip's caret: the mockup's small up-pointing triangle, in gold.</summary>
    private const string CaretData = "M1.5 6.5L5 2.5L8.5 6.5Z";

    /// <summary>A tab label on the tab strip. The selected tab shows the dark fill and the gold outline, as the mockup does.</summary>
    public static Border Tab(string text, bool selected)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = DerethPalette.Title,
            FontSize = 11,
            Foreground = selected ? DerethPalette.CreamBrush : DerethPalette.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        return new Border
        {
            Height = selected ? 19 : 17,
            Padding = new Thickness(7, 0),
            Margin = new Thickness(0, 0, 1, 0),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            BorderBrush = selected ? DerethChatPalette.GoldDarkBrush : Brushes.Transparent,
            Background = selected ? DerethPalette.Brush(Color.FromArgb(0xD9, 0x03, 0x06, 0x09)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = label
        };
    }

    /// <summary>The target chip: the target's label in the target's colour, a gold caret, on the dark fill with a gold outline.</summary>
    public static Border Chip(string label, Color colour)
    {
        var text = new TextBlock
        {
            Text = label,
            FontFamily = DerethPalette.Title,
            FontSize = 11,
            Foreground = DerethPalette.Brush(colour),
            VerticalAlignment = VerticalAlignment.Center
        };
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
