using System;
using System.Collections.Generic;
using System.Linq;
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
/// One shortcut row inside the toolbar: nine item slots at the icon size, touching, as retail's are. A slot
/// shows its item's icon and stack count, with its key number in the corner, or only its key number when empty. A click selects and uses
/// the slot's item, and a slot whose item is selected is lit, so the slot last clicked stays marked. A press on a slot never moves the
/// toolbar. A drag from a slot moves the shortcut to the slot it is released on, or takes it off the bars when no LegACEy window takes it.
/// A drop of an item from outside binds it to the slot it is released on.
/// </summary>
public sealed class ShortcutBar : Decorator, IDisposable, IRetailItemDropTarget
{
    public const int SlotCount = 9;
    private const double DragThreshold = 4;
    // The slot index a drag over the bar would land on, lit while it is there.
    private int? _dropAt;
    private bool _refreshPending;

    private static readonly IBrush EmptyKey = DerethPalette.Brush(Color.Parse("#3E3626"));

    private readonly IRetailToolbarPort _port;
    private readonly Func<uint, ShortcutItem?> _itemOf;
    private readonly IGameArtSource? _art;
    private readonly int _firstIndex;
    private readonly IItemDragHost _drag;
    private readonly ShortcutDragSource _dragSource;
    // The slot a press on a slot began at, and where, in the bar's coordinates. A drag starts once the pointer moves past the threshold.
    private int? _pressed;
    private Point _pressAt;
    // The drag of a shortcut from a slot: its icon while it is held.
    private IDisposable? _dragIcon;
    // One slot per shortcut.
    // Each slot gets a ninth of the row, so at any scale the row fills the toolbar's width exactly, where slots each rounded up to whole
    // pixels would add up past it. The slots ask no width (see DerethSlot's flush), so the row takes the toolbar's.
    private readonly Grid _row = new() { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", SlotCount))) };
    private readonly ShortcutSlotView?[] _shown = new ShortcutSlotView?[SlotCount];
    // The icon each slot's art was drawn into; a slot's icon is disposed when the slot is rebuilt or the bar closes.
    private readonly WriteableBitmap?[] _icons = new WriteableBitmap?[SlotCount];

    /// <param name="firstIndex">The slot index of the row's first slot: 0 for row 1, 9 for row 2.</param>
    /// <param name="port">Retail's shortcut list and its use.</param>
    /// <param name="itemOf">The icon and stack count of an object the player holds; null when it is not held.</param>
    /// <param name="art">Game art for the icons; null draws a placeholder.</param>
    /// <param name="drag">The client's drag services: a shortcut dragged from a slot is handed to the LegACEy window under the pointer.</param>
    /// <param name="dragSource">The slot a shortcut drag started from, shared with the other bars.</param>
    public ShortcutBar(int firstIndex, IRetailToolbarPort port, Func<uint, ShortcutItem?> itemOf, IGameArtSource? art, IItemDragHost drag,
        ShortcutDragSource dragSource)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _itemOf = itemOf ?? throw new ArgumentNullException(nameof(itemOf));
        _art = art;
        _firstIndex = firstIndex;
        _drag = drag ?? throw new ArgumentNullException(nameof(drag));
        _dragSource = dragSource ?? throw new ArgumentNullException(nameof(dragSource));

        for (var i = 0; i < SlotCount; i++) _row.Children.Add(At(new DerethSlot(null, flush: true), i));
        Child = _row;
        Refresh();
        port.Changed += Refresh;
    }

    private static Control At(Control slot, int position)
    {
        Grid.SetColumn(slot, position);
        return slot;
    }

    /// <summary>The slot at a position in the row, zero-based: where the player clicks it.</summary>
    public Control SlotAt(int position) => (Control)_row.Children[position];

    /// <summary>Whether a shortcut dragged off a bar is in hand: only a bar takes it, so the toolbar's backpack must not.</summary>
    public bool ShortcutInHand => _dragSource.Slot != null;

    /// <summary>Shows the port's shortcuts for this row, with the items the player holds. A slot whose item is gone shows empty.</summary>
    public void Refresh()
    {
        // Replacing the pressed slot would end its pointer capture, and with it the drag; the bar catches up when the press ends.
        if (_pressed != null || _dragIcon != null)
        {
            _refreshPending = true;
            return;
        }
        _refreshPending = false;
        var shortcuts = _port.Shortcuts;
        for (var position = 0; position < SlotCount; position++)
        {
            var index = _firstIndex + position;
            var view = new ShortcutSlotView(index, ItemOf(shortcuts, index));
            if (view == _shown[position]) continue;
            _shown[position] = view;
            var previous = _icons[position];
            _row.Children[position] = At(Slot(view, position), position);
            // The new slot is in place before the old icon goes, so nothing draws a disposed bitmap.
            previous?.Dispose();
        }
        ShowLit();
    }

    /// <summary>Stops following the port and frees the icons. The toolbar lets go of the bar first.</summary>
    public void Dispose()
    {
        _port.Changed -= Refresh;
        _refreshPending = false;
        CancelDrag();
        foreach (var icon in _icons) icon?.Dispose();
        Array.Clear(_icons, 0, _icons.Length);
    }

    /// <summary>An item or a shortcut dragged over the bar lights the slot it would land on.</summary>
    public void RetailDragOver(uint itemId, string itemName, Point? position)
    {
        var at = position is { } point && (itemId != 0 || _dragSource.Slot != null) ? IndexAt(point) : null;
        if (at == _dropAt) return;
        _dropAt = at;
        ShowLit();
    }

    /// <summary>Lights the slot a drag would land on, and each slot whose item is the selected object.</summary>
    private void ShowLit()
    {
        var shortcuts = _port.Shortcuts;
        var selected = _port.Selection?.Id ?? 0;
        for (var position = 0; position < SlotCount; position++)
        {
            var index = _firstIndex + position;
            var holdsSelected = _shown[position]?.Item != null && ShortcutEdits.ObjectAt(shortcuts, index) == selected;
            if (SlotAt(position) is DerethSlot slot) slot.Selected = index == _dropAt || holdsSelected;
        }
    }

    /// <summary>
    /// A drop on the bar. A shortcut dragged from a slot lands on the slot released on, swapping with its shortcut; released on the frame,
    /// it stays off the bars. An item from outside binds to the slot released on; released on the frame, nothing changes.
    /// </summary>
    public bool RetailDrop(uint itemId, string itemName, Point position)
    {
        _dropAt = null;
        ShowLit();
        if (_dragSource.Slot is { } from)
        {
            var objectId = _dragSource.ObjectId;
            _dragSource.Slot = null;
            if (IndexAt(position) is { } to) ShortcutEdits.Land(_port, from, to, objectId);
            return true;
        }
        if (IndexAt(position) is not { } slot || itemId == 0) return false;
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
        var slot = new DerethSlot(Contents(view, position), flush: true);
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
            // Retail's lock only keeps the bars in place; shortcuts still move and come off while it is on.
            if (_pressed is not { } index || _dragIcon != null) return;
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
            if (_dragIcon != null) EndDrag();
            else if (view.Item != null && new Rect(slot.Bounds.Size).Contains(e.GetPosition(slot)))
            {
                // A click selects the item too, as retail's slot click does, so it can be examined; the number key only uses it.
                if (ShortcutEdits.ObjectAt(_port.Shortcuts, view.Index) is var objectId and not 0) _port.Select(objectId);
                _port.UseShortcut(view.Index);
            }
            if (_refreshPending) Refresh();
        };
        // A capture that ends mid-drag cancels it. After a release the drag is already over, so this does nothing then.
        slot.PointerCaptureLost += (_, _) => CancelDrag();
        return slot;
    }

    /// <summary>
    /// Starts the drag of the shortcut in a slot, if the slot holds one: its icon follows the pointer, and the shortcut leaves the slot now,
    /// as retail's drag start removes it.
    /// </summary>
    private void StartDrag(int index)
    {
        var objectId = ShortcutEdits.ObjectAt(_port.Shortcuts, index);
        if (objectId == 0) return;
        // Zero: the icon is not an item, so no other window shows a drop indicator for it.
        _dragIcon = _drag.ShowDragIcon(IconOf(objectId), 1, retailDropIndicator: false, itemId: 0);
        _dragSource.Slot = index;
        _dragSource.ObjectId = objectId;
        ShortcutEdits.Lift(_port, index);
    }

    /// <summary>
    /// Ends the drag on release. The LegACEy window under the pointer is offered the shortcut. A bar that lands it clears the source; a
    /// release anywhere else leaves the shortcut off the bars.
    /// </summary>
    private void EndDrag()
    {
        _dragIcon?.Dispose();
        _dragIcon = null;
        _drag.DeliverAtPointer(_dragSource.ObjectId, string.Empty);
        _dragSource.Slot = null;
    }

    /// <summary>
    /// Cancels the drag in progress, if any: the icon goes, and the shortcut it lifted goes back to its slot when that slot is still empty.
    /// The press is forgotten either way.
    /// </summary>
    private void CancelDrag()
    {
        _pressed = null;
        if (_dragIcon != null)
        {
            _dragIcon.Dispose();
            _dragIcon = null;
            if (_dragSource.Slot is { } from) ShortcutEdits.Restore(_port, from, _dragSource.ObjectId);
            _dragSource.Slot = null;
        }
        if (_refreshPending) Refresh();
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
        return DerethItemCells.Icon(bitmap);
    }
}
