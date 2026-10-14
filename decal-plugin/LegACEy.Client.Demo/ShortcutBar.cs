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
/// is locked; a press on a slot never does. A drag from a slot moves the shortcut to the slot it is released on, or takes it off the bars
/// when no LegACEy window takes it. A drop of an item from outside binds it to the slot it is released on.
/// </summary>
public sealed class ShortcutBar : Decorator, IDisposable, IRetailItemDropTarget
{
    /// <summary>The bar's height in DIPs, the toolbar's.</summary>
    public const int BarHeight = 48;
    public const int SlotCount = 9;
    private const double SlotSize = 30;
    private const double Gap = 3;
    private const double DragThreshold = 4;

    private static readonly IBrush TagFace = DerethPalette.Brush(Color.Parse("#0B1117"));
    private static readonly IBrush TagEdge = DerethPalette.Brush(Color.Parse("#3A4A56"));
    private static readonly IBrush EmptyKey = DerethPalette.Brush(Color.Parse("#2C3B47"));

    private readonly IRetailToolbarPort _port;
    private readonly Func<uint, ShortcutItem?> _itemOf;
    private readonly IGameArtSource? _art;
    private readonly int _firstIndex;
    private readonly Func<bool> _uiLocked;
    private readonly Action _startMove;
    private readonly Func<System.Drawing.Point> _pointer;
    private readonly Action? _tagClick;
    private readonly IItemDragHost _drag;
    private readonly ShortcutDragSource _dragSource;
    private System.Drawing.Point? _tagPressAt;
    // The slot a press on a slot began at, and where, in the bar's coordinates. A drag starts once the pointer moves past the threshold.
    private int? _pressed;
    private Point _pressAt;
    // The drag of a shortcut from a slot: its icon while it is held, and the object it carries.
    private IDisposable? _dragIcon;
    private uint _dragObject;
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
    /// <param name="pointer">The pointer's place now, in screen pixels: a tag press and release at one place is a click.</param>
    /// <param name="tagClick">Run on a click on the row tag; null for a tag that does nothing but drag.</param>
    /// <param name="drag">The client's drag services: a shortcut dragged from a slot is handed to the LegACEy window under the pointer.</param>
    /// <param name="dragSource">The slot a shortcut drag started from, shared with the other bars.</param>
    public ShortcutBar(int row, int firstIndex, IRetailToolbarPort port, Func<uint, ShortcutItem?> itemOf, IGameArtSource? art,
        Func<bool> uiLocked, Action startMove, Func<System.Drawing.Point> pointer, Action? tagClick, IItemDragHost drag, ShortcutDragSource dragSource)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _itemOf = itemOf ?? throw new ArgumentNullException(nameof(itemOf));
        _art = art;
        _firstIndex = firstIndex;
        _uiLocked = uiLocked ?? throw new ArgumentNullException(nameof(uiLocked));
        _startMove = startMove ?? throw new ArgumentNullException(nameof(startMove));
        _pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        _tagClick = tagClick;
        _drag = drag ?? throw new ArgumentNullException(nameof(drag));
        _dragSource = dragSource ?? throw new ArgumentNullException(nameof(dragSource));

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
        // A tag press that ends where the pointer started is a click; a move in between was a drag of the bar.
        RowTag.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(RowTag).Properties.IsLeftButtonPressed) _tagPressAt = _pointer();
        };
        RowTag.PointerReleased += (_, e) =>
        {
            var pressedAt = _tagPressAt;
            _tagPressAt = null;
            if (pressedAt is { } at && at == _pointer()) _tagClick?.Invoke();
        };
        // A press that ends off the tag is not a click on it, so a stale press cannot toggle later.
        RowTag.PointerExited += (_, _) => _tagPressAt = null;
        RowTag.PointerCaptureLost += (_, _) => _tagPressAt = null;
        Child = hit;
        Refresh();
        port.Changed += Refresh;
    }

    /// <summary>The bar's row tag: a press on it drags the bar, and a click on it runs the tag's click.</summary>
    public Control RowTag => (Control)_row.Children[0];

    /// <summary>The hover name of the row tag: what a click on it does.</summary>
    public void SetTagTip(string tip) => ToolTip.SetTip(RowTag, tip);

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

    /// <summary>The place for a bar 4 DIP above another bar at <paramref name="row"/>: the same left edge, its top 4 DIP above the other's.</summary>
    public static System.Drawing.Point AboveRow(System.Drawing.Point row, double scale) =>
        new(row.X, row.Y - (int)Math.Round(4 * scale) - (int)Math.Round(BarHeight * scale));

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
        if (_dragIcon != null)
        {
            _dragIcon.Dispose();
            _dragIcon = null;
            _dragSource.Slot = null;
        }
        foreach (var icon in _icons) icon?.Dispose();
        Array.Clear(_icons, 0, _icons.Length);
    }

    /// <summary>A drag over the bar draws no drop indicator yet; the drop is judged on release.</summary>
    public void RetailDragOver(uint itemId, string itemName, Point? position) { }

    /// <summary>
    /// A drop on the bar. A shortcut dragged from one of the bars moves to the slot released on, and a shortcut released on the frame stays
    /// where it was. An item from outside binds to the slot released on; released on the frame, nothing is taken.
    /// </summary>
    public bool RetailDrop(uint itemId, string itemName, Point position)
    {
        var from = _dragSource.Slot;
        var to = IndexAt(position);
        if (from is { } source)
        {
            // Taken either way: the bar this lands on moves it, or it stays put over the frame. Nothing left in the source is removed.
            _dragSource.Slot = null;
            if (to is { } target) ShortcutEdits.Move(_port, source, target);
            return true;
        }
        if (to is not { } slot || itemId == 0) return false;
        ShortcutEdits.Bind(_port, slot, itemId);
        return true;
    }

    private ShortcutItem? ItemOf(IReadOnlyList<RetailShortcut> shortcuts, int index)
    {
        foreach (var shortcut in shortcuts)
            if (shortcut.Index == index) return _itemOf(shortcut.ObjectId);
        return null;
    }

    /// <summary>The slot index (row 1 is 0 to 8, row 2 is 9 to 17) of the slot under a point in the bar's coordinates, or null.</summary>
    private int? IndexAt(Point point)
    {
        for (var position = 0; position < SlotCount; position++)
        {
            var slot = SlotAt(position);
            if (slot.TranslatePoint(new Point(0, 0), this) is not { } origin) continue;
            if (new Rect(origin, slot.Bounds.Size).Contains(point)) return _firstIndex + position;
        }
        return null;
    }

    private DerethSlot Slot(ShortcutSlotView view, int position)
    {
        _icons[position] = null;
        var slot = new DerethSlot(Contents(view, position)) { Width = SlotSize, Height = SlotSize };
        slot.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (!e.GetCurrentPoint(slot).Properties.IsLeftButtonPressed) return;
            _pressed = view.Index;
            _pressAt = e.GetPosition(this);
            // The moves and the release come to this slot wherever the pointer goes, so a drag that leaves the slot still ends here.
            e.Pointer.Capture(slot);
        };
        slot.PointerMoved += (_, e) =>
        {
            if (_pressed is not { } index || _dragIcon != null || _uiLocked()) return;
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _pressAt.X) < DragThreshold && Math.Abs(point.Y - _pressAt.Y) < DragThreshold) return;
            StartDrag(index);
        };
        slot.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            var wasPressed = _pressed != null;
            _pressed = null;
            if (!wasPressed) return;
            if (_dragIcon != null)
            {
                EndDrag();
                return;
            }
            if (view.Item != null && new Rect(slot.Bounds.Size).Contains(e.GetPosition(slot))) _port.UseShortcut(view.Index);
        };
        return slot;
    }

    /// <summary>Starts the drag of the shortcut in a slot, if the slot holds one: its icon follows the pointer and the slot is the source.</summary>
    private void StartDrag(int index)
    {
        var objectId = ShortcutEdits.ObjectAt(_port.Shortcuts, index);
        if (objectId == 0) return;
        _dragObject = objectId;
        _dragSource.Slot = index;
        _dragIcon = _drag.ShowDragIcon(IconOf(objectId), 1, retailDropIndicator: false, objectId);
    }

    /// <summary>
    /// Ends the drag on release. The LegACEy window under the pointer is offered the shortcut; a bar that takes it moves it and clears the
    /// source. Nothing taking it takes it off the bars, as retail's drag start does.
    /// </summary>
    private void EndDrag()
    {
        _dragIcon?.Dispose();
        _dragIcon = null;
        _drag.DeliverAtPointer(_dragObject, string.Empty);
        if (_dragSource.Slot is not { } from) return;
        _dragSource.Slot = null;
        ShortcutEdits.Take(_port, from);
    }

    /// <summary>The icon of an object the player holds, drawn as the slot draws it; null when there is no art or no item.</summary>
    private GameImage? IconOf(uint objectId)
    {
        if (_art == null || _itemOf(objectId) is not { } item) return null;
        var visual = item.Visual;
        return ItemIcon.Draw(_art, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects, visual.Plate);
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
