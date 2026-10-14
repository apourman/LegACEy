using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>An item a shortcut uses, as the inventory snapshot holds it: its icon and stack count.</summary>
public sealed record ShortcutItem(ItemVisual Visual, int StackCount);

/// <summary>What one shortcut slot shows: its slot index, and the item it uses while the player still holds that item.</summary>
public sealed record ShortcutSlotView(int Index, ShortcutItem? Item);

/// <summary>
/// One shortcut row as a window of its own: a row tag and nine 30 DIP slots. A slot shows its item's icon and stack count, with its key
/// number in the corner, or only its key number when empty. A click uses the slot. The frame and the tag drag the bar, unless retail's UI
/// is locked; a press on a slot never does.
/// </summary>
public sealed class ShortcutBar : Decorator, IDisposable
{
    /// <summary>The bar's height in DIPs, the toolbar's.</summary>
    public const int BarHeight = 48;
    public const int SlotCount = 9;
    private const double SlotSize = 30;
    private const double Gap = 3;

    private static readonly IBrush TagFace = DerethPalette.Brush(Color.Parse("#0B1117"));
    private static readonly IBrush TagEdge = DerethPalette.Brush(Color.Parse("#3A4A56"));
    private static readonly IBrush EmptyKey = DerethPalette.Brush(Color.Parse("#2C3B47"));

    private readonly IRetailToolbarPort _port;
    private readonly Func<uint, ShortcutItem?> _itemOf;
    private readonly IGameArtSource? _art;
    private readonly int _firstIndex;
    private readonly Func<bool> _uiLocked;
    private readonly Action _startMove;
    // The tag first, then one slot per shortcut.
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = Gap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ShortcutSlotView?[] _shown = new ShortcutSlotView?[SlotCount];
    // The icon each slot's art was drawn into; a slot's icon is disposed when the slot is rebuilt or the bar closes.
    private readonly WriteableBitmap?[] _icons = new WriteableBitmap?[SlotCount];

    /// <param name="row">The row's number, shown on its tag.</param>
    /// <param name="firstIndex">The slot index of the row's first slot: 0 for row 1, 9 for row 2.</param>
    /// <param name="port">Retail's shortcut list and its use.</param>
    /// <param name="itemOf">The icon and stack count of an object the player holds; null when it is not held.</param>
    /// <param name="art">Game art for the icons; null draws a placeholder.</param>
    /// <param name="uiLocked">Whether retail's UI is locked: a locked bar does not move.</param>
    /// <param name="startMove">Starts the window's move, from the pointer's place, on a press on the frame or the tag.</param>
    public ShortcutBar(int row, int firstIndex, IRetailToolbarPort port, Func<uint, ShortcutItem?> itemOf, IGameArtSource? art,
        Func<bool> uiLocked, Action startMove)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _itemOf = itemOf ?? throw new ArgumentNullException(nameof(itemOf));
        _art = art;
        _firstIndex = firstIndex;
        _uiLocked = uiLocked ?? throw new ArgumentNullException(nameof(uiLocked));
        _startMove = startMove ?? throw new ArgumentNullException(nameof(startMove));

        _row.Children.Add(new Border
        {
            Width = 16, Height = 30, CornerRadius = new CornerRadius(3), Background = TagFace, BorderBrush = TagEdge, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = row.ToString(), FontFamily = DerethPalette.Title, FontSize = 10, Foreground = DerethPalette.GoldBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        });
        for (var i = 0; i < SlotCount; i++) _row.Children.Add(new DerethSlot(null) { Width = SlotSize, Height = SlotSize });

        // A transparent square takes the pointer over the whole frame, as the medallions do; a frame alone takes none.
        var hit = new Border
        {
            Background = Brushes.Transparent, Height = BarHeight,
            Child = new DerethFrame(DerethFrameArt.Window) { Child = new Border { Padding = new Thickness(4, 0), Child = _row } }
        };
        // A slot marks its presses handled, so only the frame and the tag, and the gaps between slots, reach this press.
        hit.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed && !_uiLocked()) _startMove();
        };
        Child = hit;
        Refresh();
        port.Changed += Refresh;
    }

    /// <summary>The bar's row tag: a press on it drags the bar.</summary>
    public Control RowTag => (Control)_row.Children[0];

    /// <summary>The slot at a position in the row, zero-based: where the player clicks it.</summary>
    public Control SlotAt(int position) => (Control)_row.Children[position + 1];

    /// <summary>
    /// The default place for a shortcut bar beside the toolbar: the bar's right edge 4 DIP left of the toolbar's left edge, and its bottom
    /// level with the toolbar's. <paramref name="toolbar"/> and the result are screen pixels; <paramref name="bar"/> is in DIPs.
    /// </summary>
    public static System.Drawing.Point DefaultLocation(System.Drawing.Rectangle toolbar, System.Drawing.Size bar, double scale)
    {
        var gap = (int)Math.Round(4 * scale);
        return new System.Drawing.Point(
            toolbar.Left - gap - (int)Math.Round(bar.Width * scale),
            toolbar.Bottom - (int)Math.Round(bar.Height * scale));
    }

    /// <summary>Shows the port's shortcuts for this row, with the items the player holds. A slot whose item is gone shows empty.</summary>
    public void Refresh()
    {
        var shortcuts = _port.Shortcuts;
        for (var position = 0; position < SlotCount; position++)
        {
            var index = _firstIndex + position;
            var view = new ShortcutSlotView(index, ItemOf(shortcuts, index));
            if (view == _shown[position]) continue;
            _shown[position] = view;
            var previous = _icons[position];
            _row.Children[position + 1] = Slot(view, position);
            // The new slot is in place before the old icon goes, so nothing draws a disposed bitmap.
            previous?.Dispose();
        }
    }

    /// <summary>Stops following the port and frees the icons. The bar's window is released by the client first.</summary>
    public void Dispose()
    {
        _port.Changed -= Refresh;
        foreach (var icon in _icons) icon?.Dispose();
        Array.Clear(_icons, 0, _icons.Length);
    }

    private ShortcutItem? ItemOf(IReadOnlyList<RetailShortcut> shortcuts, int index)
    {
        foreach (var shortcut in shortcuts)
            if (shortcut.Index == index) return _itemOf(shortcut.ObjectId);
        return null;
    }

    private DerethSlot Slot(ShortcutSlotView view, int position)
    {
        _icons[position] = null;
        var slot = new DerethSlot(Contents(view, position)) { Width = SlotSize, Height = SlotSize };
        var pressed = false;
        slot.PointerPressed += (_, e) =>
        {
            pressed = true;
            e.Handled = true;
        };
        slot.PointerReleased += (_, e) =>
        {
            var wasPressed = pressed;
            pressed = false;
            e.Handled = true;
            if (wasPressed && view.Item != null && new Rect(slot.Bounds.Size).Contains(e.GetPosition(slot))) _port.UseShortcut(view.Index);
        };
        return slot;
    }

    private Control Contents(ShortcutSlotView view, int position)
    {
        var key = position + 1;
        var layers = new Grid();
        if (view.Item is not { } item)
        {
            // An empty slot shows its key number, centred, as the mockup does.
            layers.Children.Add(new TextBlock
            {
                Text = key.ToString(), FontFamily = DerethPalette.Title, FontSize = 14, Foreground = EmptyKey,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            return layers;
        }
        layers.Children.Add(Icon(item.Visual, position));
        if (item.StackCount > 1) layers.Children.Add(DerethItemCells.StackCount(item.StackCount));
        layers.Children.Add(new TextBlock
        {
            Text = key.ToString(), FontFamily = DerethPalette.Body, FontSize = 8, Foreground = DerethPalette.MutedBrush,
            Margin = new Thickness(3, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        });
        return layers;
    }

    private Control Icon(ItemVisual visual, int position)
    {
        var bitmap = _art == null ? null
            : GameArtImageExtension.CreateBitmap(ItemIcon.Draw(_art, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects, visual.Plate));
        if (bitmap == null)
            return new TextBlock { Text = "?", FontFamily = DerethPalette.Body, FontSize = 12, Foreground = DerethPalette.MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _icons[position] = bitmap;
        return new Image { Source = bitmap, Width = 28, Height = 28, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }
}
