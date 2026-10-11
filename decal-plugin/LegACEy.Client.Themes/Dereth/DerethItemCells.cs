using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LegACEy.Client.Themes;

/// <summary>The pieces of an item cell that the Vault and the inventory draw alike: the icon, the stack count and a label.</summary>
public static class DerethItemCells
{
    private static readonly IBrush ShadowBrush = DerethPalette.Brush(Colors.Black);

    /// <summary>An icon at native size, centred. Missing art shows a question mark.</summary>
    public static Grid Icon(WriteableBitmap? bitmap)
    {
        var layers = new Grid
        {
            Width = 32, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        if (bitmap != null)
            layers.Children.Add(new Image { Source = bitmap, Width = 32, Height = 32, Stretch = Stretch.None });
        if (layers.Children.Count == 0)
        {
            var fallback = Label("?", DerethPalette.MutedBrush, 12);
            fallback.HorizontalAlignment = HorizontalAlignment.Center;
            layers.Children.Add(fallback);
        }
        return layers;
    }

    /// <summary>A stack count in the cell's corner, with a one-pixel dark shadow.</summary>
    public static Control StackCount(int count)
    {
        var panel = new Panel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 1) };
        var shadow = Label(count.ToString(), ShadowBrush, 10);
        shadow.FontWeight = FontWeight.SemiBold;
        shadow.Margin = new Thickness(1, 1, 0, 0);
        var text = Label(count.ToString(), DerethPalette.TextBrush, 10);
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, 0, 1, 1);
        panel.Children.Add(shadow);
        panel.Children.Add(text);
        return panel;
    }

    /// <summary>A line of text in the Dereth body font, centred vertically.</summary>
    public static TextBlock Label(string text, IBrush brush, double size) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontFamily = DerethPalette.Body,
        VerticalAlignment = VerticalAlignment.Center
    };
}
