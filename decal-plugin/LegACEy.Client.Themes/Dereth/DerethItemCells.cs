using System;
using System.Collections.Generic;
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

    /// <summary>An icon at the icon size, centred: see <see cref="ItemCellSize"/>. Missing art shows a question mark.</summary>
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
/// The size of an item icon, its slot and the slot pitch, in DIPs: the 32 px art, a 34 px slot round it, 35 px apart. Icons scale with
/// their window like everything in it, so a window drawn at any size, by the UI size or its own, keeps its proportions; a layout never
/// squeezes an icon into a smaller box.
/// </summary>
public readonly record struct ItemCellSize(double Icon, double Cell, double Pitch)
{
    public static ItemCellSize Standard { get; } = new(32, 34, 35);
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
        var height = ItemCellSize.Standard.Cell;
        return base.MeasureOverride(availableSize.WithHeight(height)).WithHeight(height);
    }
}

/// <summary>
/// Draws an icon's bitmap at the icon size, scaled with its window: pixel for pixel at 100% and 200%, as sharp as retail's, and
/// resampled smoothly at any other size.
/// </summary>
public sealed class DerethItemIcon : Control
{
    private readonly WriteableBitmap _bitmap;
    // Each art's resampled copies by side, shared by every icon drawing it and freed with the art: windows rebuild their icons on every
    // change, and resampling again for each would cost a frame's time and leave a bitmap behind each time.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WriteableBitmap, Dictionary<int, WriteableBitmap>> Resampled = new();

    // An ancestor's bitmap smoothing overrides a child's, so this relies on the panel root turning it off, as AvaloniaPanel.ApplyTheme does.
    public DerethItemIcon(WriteableBitmap bitmap) => _bitmap = bitmap;

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = ItemCellSize.Standard.Icon;
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        // The panel draws every bitmap nearest-neighbour, and no element below its root can change that. At a whole multiple of the art
        // (100%, 200%) that keeps the pixels sharp; at any other size it would drop rows and columns, so the art is resampled smoothly to
        // the size it takes on screen, once per art and size, and drawn pixel for pixel.
        var pixels = (int)Math.Round(Bounds.Width * LayoutHelper.GetLayoutScale(this));
        var art = _bitmap.PixelSize.Width;
        if (pixels <= 0 || pixels % art == 0)
        {
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
            return;
        }
        var sides = Resampled.GetOrCreateValue(_bitmap);
        if (!sides.TryGetValue(pixels, out var resampled)) sides[pixels] = resampled = Resample(_bitmap, pixels);
        context.DrawImage(resampled, new Rect(Bounds.Size));
    }

    /// <summary>
    /// The square 32-bit art at <paramref name="side"/> pixels, each one the area-weighted average of the art pixels it covers, in the art's
    /// own pixel format, so shrinking blends neighbours instead of dropping them.
    /// </summary>
    private static WriteableBitmap Resample(WriteableBitmap art, int side)
    {
        var size = art.PixelSize.Width;
        var source = new byte[size * size * 4];
        using (var locked = art.Lock())
            for (var row = 0; row < size; row++)
                System.Runtime.InteropServices.Marshal.Copy(locked.Address + row * locked.RowBytes, source, row * size * 4, size * 4);
        var result = new WriteableBitmap(new PixelSize(side, side), art.Dpi, art.Format ?? Avalonia.Platform.PixelFormat.Bgra8888, art.AlphaFormat ?? Avalonia.Platform.AlphaFormat.Premul);
        var target = new byte[side * side * 4];
        var step = (double)size / side;
        var sums = new double[4];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            Array.Clear(sums, 0, 4);
            double top = y * step, left = x * step;
            for (var sy = (int)top; sy < Math.Min(size, Math.Ceiling(top + step)); sy++)
            {
                var height = Math.Min(sy + 1, top + step) - Math.Max(sy, top);
                for (var sx = (int)left; sx < Math.Min(size, Math.Ceiling(left + step)); sx++)
                {
                    var weight = height * (Math.Min(sx + 1, left + step) - Math.Max(sx, left));
                    for (var c = 0; c < 4; c++) sums[c] += weight * source[(sy * size + sx) * 4 + c];
                }
            }
            for (var c = 0; c < 4; c++) target[(y * side + x) * 4 + c] = (byte)Math.Round(sums[c] / (step * step));
        }
        using (var locked = result.Lock())
            for (var row = 0; row < side; row++)
                System.Runtime.InteropServices.Marshal.Copy(target, row * side * 4, locked.Address + row * locked.RowBytes, side * 4);
        return result;
    }
}
