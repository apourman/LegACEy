using System;
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

    /// <summary>An icon at native size on screen, centred: see <see cref="ItemCellSize"/>. Missing art shows a question mark.</summary>
    public static Grid Icon(WriteableBitmap? bitmap)
    {
        var layers = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (bitmap != null)
            layers.Children.Add(new DerethItemIcon(bitmap));
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

/// <summary>
/// The size of an item icon, its slot and the slot pitch, in DIPs, for a window drawn at a scale. Icons draw at a whole multiple
/// of their 32 px art on screen, the nearest to the scale and at least one, and the slot grows or shrinks round them, so item grids
/// keep retail's sharp icons while the rest of the window scales. At scale 1 they are 32, 34 and 35.
/// </summary>
public readonly record struct ItemCellSize(double Icon, double Cell, double Pitch)
{
    private const int ArtPixels = 32;

    /// <summary>How many screen pixels each art pixel takes at a scale: the scale rounded to a whole number, at least one.</summary>
    public static double Multiple(double scale) => Math.Max(1, Math.Round(scale));

    public static ItemCellSize At(double scale)
    {
        var icon = ArtPixels * Multiple(scale) / scale;
        return new ItemCellSize(icon, icon + 2, icon + 3);
    }

    /// <summary>The size for the window <paramref name="control"/> is in, or for scale 1 while it is in none.</summary>
    public static ItemCellSize Of(Layoutable control) => At(LayoutHelper.GetLayoutScale(control));
}

/// <summary>
/// A horizontal row as tall as an item slot at the window's scale. The toolbar and the shortcut bars use it, so a bar of slots and a
/// bar of buttons are the same height at any UI size.
/// </summary>
public sealed class DerethItemRow : StackPanel
{
    public DerethItemRow() => Orientation = Orientation.Horizontal;

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = ItemCellSize.Of(this).Cell;
        return base.MeasureOverride(availableSize.WithHeight(height)).WithHeight(height);
    }
}

/// <summary>
/// Draws a bitmap at a whole multiple of its pixels on screen, whatever the window's scale, so a 32 px icon stays as sharp as
/// retail's. Scaled to a fraction, the art would be resampled and blur.
/// </summary>
public sealed class DerethItemIcon : Control
{
    private readonly WriteableBitmap _bitmap;

    // An ancestor's bitmap smoothing overrides a child's, so this relies on the panel root turning it off, as AvaloniaPanel.ApplyTheme does.
    public DerethItemIcon(WriteableBitmap bitmap) => _bitmap = bitmap;

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = ItemCellSize.Of(this).Icon;
        return new Size(side, side);
    }

    public override void Render(DrawingContext context) => context.DrawImage(_bitmap, new Rect(Bounds.Size));
}
