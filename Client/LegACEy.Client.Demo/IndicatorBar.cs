using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>One button on the indicator bar: its game art, and what it does when clicked.</summary>
public sealed class IndicatorSlot
{
    public IndicatorSlot(string name, uint icon, Action clicked, string? label = null)
    {
        Name = name;
        Icon = icon;
        Clicked = clicked;
        Label = label;
    }

    public string Name { get; }

    /// <summary>The portal.dat image drawn as the button.</summary>
    public uint Icon { get; }

    /// <summary>Text drawn over the icon, for slots without art of their own.</summary>
    public string? Label { get; }

    public Action Clicked { get; }
}

/// <summary>
/// A replacement for the client's floating indicators bar: the retail buttons, drawn from the
/// retail art, plus extra plugin slots in the same row.
/// </summary>
public sealed class IndicatorBar : Border
{
    public const int SlotSize = 20;
    private const int Inset = 2;

    /// <summary>The highlight drawn over a plugin slot whose window is open (retail art).</summary>
    public const uint OpenOverlayIcon = 0x0600749B;

    private readonly Dictionary<string, Panel> _slots = new();
    private readonly Func<uint, GameImage?> _art;

    public IndicatorBar(IReadOnlyList<IndicatorSlot> slots, Func<uint, GameImage?> art)
    {
        _art = art;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x5a, 0x4a, 0x2a));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(Inset - 1);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var slot in slots)
            row.Children.Add(_slots[slot.Name] = CreateSlot(slot));
        Child = row;
    }

    /// <summary>The bar's size in pixels for a number of slots.</summary>
    public static PixelSize MeasureFor(int slotCount) => new((slotCount * SlotSize) + (2 * Inset), SlotSize + (2 * Inset));

    /// <summary>Show or hide the "open" highlight on a slot.</summary>
    public void SetOpen(string name, bool open)
    {
        if (_slots.TryGetValue(name, out var slot) && slot.Children[1] is Control overlay && overlay.IsVisible != open)
            overlay.IsVisible = open;
    }

    private Panel CreateSlot(IndicatorSlot slot)
    {
        var hover = new Border { Background = new SolidColorBrush(Colors.White, 0.22), IsVisible = false, IsHitTestVisible = false };
        var panel = new Panel
        {
            Width = SlotSize,
            Height = SlotSize,
            Background = Brushes.Transparent
        };
        panel.Children.Add(Picture(slot.Icon));
        panel.Children.Add(Picture(OpenOverlayIcon, visible: false));
        if (slot.Label != null)
            panel.Children.Add(new TextBlock
            {
                Text = slot.Label,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
        panel.Children.Add(hover);
        panel.PointerEntered += (_, _) => hover.IsVisible = true;
        panel.PointerExited += (_, _) => hover.IsVisible = false;
        panel.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            slot.Clicked();
        };
        return panel;
    }

    private Control Picture(uint id, bool visible = true)
    {
        var image = _art(id);
        if (image == null)
            return new Border { Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), IsVisible = visible, IsHitTestVisible = false };

        var picture = new Image { Source = ToBitmap(image), Stretch = Stretch.Fill, IsVisible = visible, IsHitTestVisible = false };
        RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.None);
        return picture;
    }

    private static unsafe Bitmap ToBitmap(GameImage image)
    {
        fixed (byte* pixels = image.Pixels)
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (IntPtr)pixels, new PixelSize(image.Width, image.Height), new Vector(96, 96), image.Width * 4);
    }
}
