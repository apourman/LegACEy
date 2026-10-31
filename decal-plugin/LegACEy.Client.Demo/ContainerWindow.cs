using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;
using static LegACEy.Client.Themes.DerethItemCells;

namespace LegACEy.Client.Demo;

/// <summary>The two layouts of the container window; the player switches between them from the header.</summary>
public enum ContainerLayout
{
    /// <summary>Retail's: the container and its packs on top, one row of items scrolling sideways under them.</summary>
    Strip,
    /// <summary>The inventory's: a header, the packs, and a grid of items that wraps and scrolls down.</summary>
    Grid,
}

/// <summary>
/// The Dereth window for an opened chest, corpse or ground container, in either <see cref="ContainerLayout"/>. It draws what
/// <see cref="ContainerState"/> holds: the container's icon and name, its packs and, in the item area, the container's items, or the
/// items of a pack the player picked (which replaces them, as in retail). A click selects an item, a double click uses it, a right click
/// appraises it, and a drag takes it to the inventory, to retail or to the world; an item of the player's released over the window is put
/// in the container. Every action goes through the inventory port, so the window does what the inventory's cells do.
/// </summary>
public sealed class ContainerWindow : UserControl, IDisposable, IRetailItemDropTarget
{
    public const string StripId = "container", GridId = "container-tall";
    public const int Width = 653, StripHeight = 126, GridHeight = 241, GridMinimumHeight = 163;
    public const string Title = "Container";
    /// <summary>Retail's container panel shows ten pack slots, and a chest holds up to as many.</summary>
    public const int PackSlots = 10;
    /// <summary>The grid's columns: retail's item row is as wide.</summary>
    public const int GridColumns = 17;
    private const int StripMinimumCells = 17;
    private const double EmptyOpacity = 0.8, DragThreshold = 4;

    /// <summary>The grid's resizing: retail's width, and a height dragged as the inventory's.</summary>
    public static readonly WindowResizing GridSizing = new(new System.Drawing.Size(Width, GridMinimumHeight), new System.Drawing.Size(Width, 10_000));

    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush, InvalidBrush = DerethPalette.InvalidBrush;

    private enum CellKind { Root, Pack, Cell }

    /// <summary>What a slot stands for: the container itself, one of its packs, or a cell of the item area (an item, or empty with its index).</summary>
    private sealed record CellId(CellKind Kind, uint ItemId, uint Container, int Index);

    private readonly ContainerLayout _layout;
    private readonly ContainerState _state;
    private readonly IInventoryPort _port;
    private readonly IGameArtSource _art;
    private readonly IItemDragHost? _drag;
    private readonly Action? _beginMove;
    private readonly Dictionary<ItemVisual, WriteableBitmap?> _images = new();
    private readonly DerethWindow? _frameWindow;
    private readonly DerethTitleText _name = new()
    {
        Foreground = DerethPalette.CreamBrush, FontFamily = DerethPalette.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly StackPanel _packs = new() { Orientation = Orientation.Horizontal, Spacing = ItemCellSize.Standard.Pitch - ItemCellSize.Standard.Cell };
    private readonly Control _items;
    private readonly Panel _itemCells;
    private readonly DerethSlotGrid _grid = new() { ColumnAlignment = HorizontalAlignment.Left, Columns = GridColumns };
    private readonly Border _containerIcon = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _count = new()
    {
        Foreground = DerethPalette.MutedBrush, FontSize = 12, FontFamily = DerethPalette.Body, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly Border _dropIndicator = new()
    {
        IsVisible = false, IsHitTestVisible = false, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly DerethHeaderButton _toggle;

    // The pack the player picked, or zero for the opened container itself. It is forgotten when another container opens.
    private uint _picked;
    private uint _shownContainer;
    // A press on a slot: a click on release, or a drag past the threshold. The item being dragged is _dragged, zero when none.
    private CellId? _press;
    private Point _pressPoint;
    private int _pressClicks;
    private uint _dragged;
    private uint _hovered;
    private IDisposable? _dragIcon;
    private bool _disposed;
    private bool _suspended;

    public ContainerWindow(ContainerLayout layout, ContainerState state, IInventoryPort port, IGameArtSource art, IItemDragHost? drag = null, Action? beginMove = null)
    {
        _layout = layout;
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _drag = drag;
        _beginMove = beginMove;
        // The toggle shows the layout it switches to: the grid's four squares from the strip, the strip's bar from the grid.
        _toggle = new DerethHeaderButton(new DerethPathGlyph(layout == ContainerLayout.Strip
            ? "M0,0 H4 V4 H0 Z M6,0 H10 V4 H6 Z M0,6 H4 V10 H0 Z M6,6 H10 V10 H6 Z"
            : "M0,2 H10 V8 H0 Z") { Fill = DerethPalette.CreamBrush });
        _toggle.Click += (_, _) => ToggleRequested?.Invoke();
        ToolTip.SetTip(_toggle, layout == ContainerLayout.Strip ? "Show as a grid" : "Show as a row");

        Control body;
        if (layout == ContainerLayout.Strip)
        {
            _itemCells = new StackPanel { Orientation = Orientation.Horizontal, Spacing = ItemCellSize.Standard.Pitch - ItemCellSize.Standard.Cell };
            _items = new ScrollViewer
            {
                Content = _itemCells, HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 10, 0, 0),
            };
            body = StripBody();
        }
        else
        {
            _itemCells = new Panel();
            _items = _grid;
            _frameWindow = new DerethWindow(Title, null, GridBody(), _toggle, iconHeader: true);
            _frameWindow.CloseRequested += (_, _) => CloseRequested?.Invoke();
            body = _frameWindow;
        }
        Content = new Grid { Children = { body, _dropIndicator } };
        AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndDrag(), handledEventsToo: true);
        AddHandler(InputElement.PointerExitedEvent, (_, _) => Hover(0), handledEventsToo: true);
        Render();
        _state.Changed += OnChanged;
        _port.Changed += OnChanged;
    }

    /// <summary>Raised when the close box is pressed.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the player asks for the other layout.</summary>
    public event Action? ToggleRequested;

    /// <summary>The object a slot or the header's icon stands for (an item, a pack or the container), or zero for an empty slot.</summary>
    internal static uint ItemIdOf(Control slot) => slot.Tag is CellId cell ? cell.ItemId : 0;

    /// <summary>The cells the item area shows (empty ones included): the strip's row, or the grid's wrapped rows.</summary>
    public int ItemCellCount => ItemCells.OfType<DerethSlot>().Count();

    /// <summary>The pack slots the top row shows.</summary>
    public int PackCellCount => _packs.Children.OfType<DerethSlot>().Count();

    /// <summary>The items drawn in the item area, in cell order.</summary>
    public IReadOnlyList<uint> ItemsShown => ItemCells.OfType<DerethSlot>().Select(cell => (cell.Tag as CellId)?.ItemId ?? 0).Where(id => id != 0).ToList();

    /// <summary>The window's title: the container's name.</summary>
    public string ContainerName => _name.FullText;

    /// <summary>The header's count of items over capacity (the grid's), empty in the strip.</summary>
    public string CountText => _count.Text ?? string.Empty;

    private IEnumerable<Control> ItemCells => _layout == ContainerLayout.Strip ? _itemCells.Children : _grid.Cells;

    /// <summary>Stops redrawing for changes while the window is hidden.</summary>
    public void Suspend()
    {
        EndDrag();
        _suspended = true;
    }

    /// <summary>Draws the current state once, and again on each change. Called when the window is shown.</summary>
    public void Resume()
    {
        if (_disposed) return;
        _suspended = false;
        Render();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EndDrag();
        _state.Changed -= OnChanged;
        _port.Changed -= OnChanged;
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
    }

    private void OnChanged()
    {
        if (!_disposed && !_suspended) Render();
    }

    // The strip: the container's icon and its packs on one row with its name and the buttons, then the items, then their scroll bar.
    private Control StripBody()
    {
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = ItemCellSize.Standard.Cell };
        _containerIcon.Margin = new Thickness(0, 0, 6, 0);
        _packs.VerticalAlignment = VerticalAlignment.Top;
        _name.Margin = new Thickness(12, 0, 8, 0);
        _name.HorizontalAlignment = HorizontalAlignment.Left;
        var close = new DerethHeaderButton(new DerethPathGlyph("M0,0 L8,8 M8,0 L0,8", 8) { Stroke = DerethPalette.CreamBrush, StrokeThickness = 1.5 });
        close.Click += (_, _) => CloseRequested?.Invoke();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DerethWindow.ActionGap, VerticalAlignment = VerticalAlignment.Center, Children = { _toggle, close } };
        top.Children.Add(_containerIcon);
        top.Children.Add(At(_packs, 1));
        top.Children.Add(At(_name, 2));
        top.Children.Add(At(buttons, 3));
        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(DerethWindow.Inset - 2, DerethWindow.Inset - 2, DerethWindow.Inset - 2, DerethWindow.Inset) };
        rows.Children.Add(top);
        rows.Children.Add(At(_items, 0, 1));
        return new DerethFrame(DerethFrameArt.Window) { Child = rows };
    }

    // The grid: the packs and their count on one row, and the items under them.
    private Control GridBody()
    {
        var packRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 6, 2, 6) };
        packRow.Children.Add(_packs);
        packRow.Children.Add(At(_count, 1));
        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(2, 0, 2, 2) };
        rows.Children.Add(packRow);
        rows.Children.Add(At(_items, 0, 1));
        return rows;
    }

    private static T At<T>(T control, int column, int row = 0) where T : Control
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        return control;
    }

    // What a render shows: the container, the pack whose items fill the item area, and each slot of both rows.
    private void Render()
    {
        HideIndicator();
        var container = _state.Opened;
        if (container != _shownContainer)
        {
            _shownContainer = container;
            _picked = 0;
        }
        var packs = _state.PacksOf(container);
        if (_picked != 0 && packs.All(pack => pack.Id != _picked)) _picked = 0;
        var shown = _picked != 0 ? _picked : container;
        var name = _state.NameOf(container);
        _name.FullText = name.Length == 0 ? Title : name;
        var visual = _state.VisualOf(container);
        var icon = visual == null ? null : Icon(visual);
        var selected = _port.Snapshot.Selected;
        if (_layout == ContainerLayout.Strip)
            _containerIcon.Child = Slot(new CellId(CellKind.Root, container, container, -1), icon, _picked == 0);
        else
        {
            _frameWindow!.Title = _name.FullText;
            // The header's icon is the container's, which a click returns to; the picked pack is outlined in the row.
            _frameWindow.SetIcon(icon == null ? null : new Border { Child = icon, Tag = new CellId(CellKind.Root, container, container, -1), Background = Brushes.Transparent });
        }
        _packs.Children.Clear();
        for (var index = 0; index < Math.Max(PackSlots, packs.Count); index++)
        {
            var pack = index < packs.Count ? packs[index] : null;
            _packs.Children.Add(pack == null
                ? Empty(new CellId(CellKind.Pack, 0, container, index))
                : Slot(new CellId(CellKind.Pack, pack.Id, container, index), Icon(pack.Visual), pack.Id == _picked));
        }
        RenderItems(shown, selected);
        Fade(_dragged, lifted: true);
    }

    private void RenderItems(uint shown, uint selected)
    {
        var items = _state.ItemsOf(shown);
        var capacity = Math.Max(_state.CapacityOf(shown), items.Count == 0 ? 0 : items.Max(item => item.Slot + 1));
        _count.Text = _layout == ContainerLayout.Grid && _state.CapacityOf(shown) > 0 ? $"{items.Count} / {_state.CapacityOf(shown)}" : string.Empty;
        var cells = new List<Control>();
        var bySlot = items.ToDictionary(item => item.Slot);
        // One cell per slot of the container, empty ones included. The strip always shows a row's worth, as retail's panel is that wide;
        // the grid fills out its last row.
        var total = _layout == ContainerLayout.Strip ? Math.Max(capacity, StripMinimumCells)
            : (int)Math.Ceiling(Math.Max(capacity, GridColumns) / (double)GridColumns) * GridColumns;
        for (var index = 0; index < total; index++)
            cells.Add(bySlot.TryGetValue(index, out var item)
                ? Slot(new CellId(CellKind.Cell, item.Id, shown, index), Layers(item), item.Id == selected)
                : Empty(new CellId(CellKind.Cell, 0, shown, index)));
        if (_layout == ContainerLayout.Strip)
        {
            _itemCells.Children.Clear();
            // The first cell stands under the container's icon; the rest line up under the pack slots.
            for (var index = 0; index < cells.Count; index++)
            {
                if (index == 1) cells[1].Margin = new Thickness(ItemCellSize.Standard.Pitch - ItemCellSize.Standard.Cell + 4, 0, 0, 0);
                _itemCells.Children.Add(cells[index]);
            }
        }
        else
        {
            _grid.Cells.Clear();
            foreach (var cell in cells) _grid.Cells.Add(cell);
        }
    }

    private Grid Layers(InventoryItem item)
    {
        var layers = Icon(item.Visual);
        if (item.StackCount > 1) layers.Children.Add(StackCount(item.StackCount));
        if (item.PartFull is { } full) layers.Children.Add(FullnessBar(full));
        return layers;
    }

    private Grid Icon(ItemVisual visual)
    {
        if (!_images.TryGetValue(visual, out var bitmap))
            _images.Add(visual, bitmap = GameArtImageExtension.CreateBitmap(ItemIcon.Draw(_art, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects, visual.Plate)));
        return DerethItemCells.Icon(bitmap);
    }

    private static DerethSlot Slot(CellId id, Control? content, bool selected) => new(content) { Tag = id, Selected = selected };

    private static DerethSlot Empty(CellId id) => new(null) { Tag = id, Opacity = EmptyOpacity };

    // The header icon, which is not a DerethSlot, is tagged too.
    private static CellId? TagOf(Visual? visual) => visual is Control { Tag: CellId slot } ? slot : null;

    /// <summary>The slot under a point of this window, or null over anything else.</summary>
    private CellId? SlotAt(Point point)
    {
        for (var visual = this.GetVisualAt(point); visual != null; visual = visual.GetVisualParent())
            if (TagOf(visual) is { } slot) return slot;
        return null;
    }

    private Control? SlotControlAt(Point point)
    {
        for (var visual = this.GetVisualAt(point); visual != null; visual = visual.GetVisualParent())
            if (TagOf(visual) != null) return visual as Control;
        return null;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = e.GetCurrentPoint(this).Properties;
        var point = e.GetPosition(this);
        if (SlotAt(point) is not { } slot)
        {
            // A press on the strip's frame, off its slots and buttons, moves the window: it has no title bar.
            if (_layout == ContainerLayout.Strip && button.IsLeftButtonPressed && !(e.Source is Visual source && InControl(source))) _beginMove?.Invoke();
            return;
        }
        if (button.IsRightButtonPressed && _press == null)
        {
            if (slot.ItemId != 0) _port.Assess(slot.ItemId);
            return;
        }
        if (!button.IsLeftButtonPressed) return;
        _press = slot;
        _pressPoint = point;
        _pressClicks = e.ClickCount;
        // Moves and the release come here even outside the window, so a drag that leaves it still ends here.
        e.Pointer.Capture(this);
    }

    // A press on a button or a scroll bar is theirs.
    private static bool InControl(Visual source)
    {
        for (var visual = source; visual != null; visual = visual.GetVisualParent())
            if (visual is ScrollBar or Button) return true;
        return false;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_press is not { } press)
        {
            Hover(SlotAt(point)?.ItemId ?? 0);
            return;
        }
        if (_dragged == 0)
        {
            if (!PastThreshold(point)) return;
            // Past the threshold the press is a drag or nothing, never a click. The container itself is not carried.
            _dragged = press.Kind == CellKind.Root ? 0 : press.ItemId;
            if (_dragged == 0)
            {
                _press = null;
                return;
            }
            _port.Select(_dragged);
            _dragIcon = _drag?.ShowDragIcon(DragImage(_dragged), 1, retailDropIndicator: false, _dragged);
            Fade(_dragged, lifted: true);
        }
        // Outside the window with the button held, retail may take the drag over: its list for the container has the item.
        if (!new Rect(Bounds.Size).Contains(point) && _drag?.HandToRetail(_dragged) == true)
        {
            EndDrag();
            e.Pointer.Capture(null);
            return;
        }
        ShowIndicator(point, _dragged);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is not { } press || e.InitialPressMouseButton != MouseButton.Left) return;
        var dragged = _dragged;
        var point = e.GetPosition(this);
        var clicks = _pressClicks;
        var click = dragged == 0 && !PastThreshold(point);
        EndDrag();
        if (click)
        {
            Click(press, clicks);
            return;
        }
        if (dragged == 0) return;
        // Over the window the item can only go into one of the container's packs; elsewhere a LegACEy window under the pointer may take it.
        if (new Rect(Bounds.Size).Contains(point))
        {
            if (SlotAt(point) is { Kind: CellKind.Pack, ItemId: not 0 } pack && pack.ItemId != dragged && !Holds(pack.ItemId, dragged))
                _port.MoveToContainer(dragged, pack.ItemId, 0);
        }
        else _drag?.DeliverAtPointer(dragged, ItemName(dragged));
    }

    private void Click(CellId slot, int clicks)
    {
        switch (slot.Kind)
        {
            case CellKind.Root:
                // The container's icon shows its own items again, and selects it, as retail's list does.
                _picked = 0;
                if (slot.ItemId != 0) _port.Select(slot.ItemId);
                Render();
                break;
            case CellKind.Pack when slot.ItemId != 0:
                // A pack replaces the items shown with its own, as in retail, and is selected too.
                _picked = slot.ItemId;
                _port.Select(slot.ItemId);
                Render();
                break;
            case CellKind.Cell when slot.ItemId != 0:
                if (clicks >= 2) _port.Use(slot.ItemId);
                else _port.Select(slot.ItemId);
                break;
        }
    }

    private uint Shown => _picked != 0 ? _picked : _state.Opened;

    private bool Holds(uint container, uint item) => _state.ItemsOf(container).Concat(_state.PacksOf(container)).Any(held => held.Id == item);

    private InventoryItem? Find(uint id) => _state.ItemsOf(Shown).Concat(_state.PacksOf(_state.Opened)).FirstOrDefault(item => item.Id == id);

    private string ItemName(uint id) => Find(id)?.Name ?? string.Empty;

    private GameImage? DragImage(uint id)
    {
        var visual = Find(id)?.Visual ?? new ItemVisual(0, 0, 0, 0);
        return ItemIcon.Draw(_art, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects);
    }

    private void EndDrag()
    {
        Fade(_dragged, lifted: false);
        _press = null;
        _dragged = 0;
        _dragIcon?.Dispose();
        _dragIcon = null;
        HideIndicator();
    }

    private bool PastThreshold(Point point) => Math.Abs(point.X - _pressPoint.X) >= DragThreshold || Math.Abs(point.Y - _pressPoint.Y) >= DragThreshold;

    private void Hover(uint itemId)
    {
        if (itemId == _hovered) return;
        _hovered = itemId;
        _port.Hover(itemId);
    }

    // A lifted item's cell fades while it is dragged, and is outlined as a selection is.
    private void Fade(uint id, bool lifted)
    {
        if (id == 0) return;
        foreach (var slot in this.GetVisualDescendants().OfType<DerethSlot>())
            if (slot.Tag is CellId { Kind: not CellKind.Root } tag && tag.ItemId == id)
            {
                slot.Opacity = lifted ? 0.4 : 1;
                slot.Selected = lifted || id == _port.Snapshot.Selected;
            }
    }

    /// <summary>Where an item released at the point would go: the pack under it, or the container (or picked pack) at the cell under it.</summary>
    private (uint Container, int Place) DropPlace(Point point)
    {
        var under = SlotAt(point);
        return under?.Kind == CellKind.Pack && under.ItemId != 0 ? (under.ItemId, 0)
            : (Shown, under?.Kind == CellKind.Cell ? under.Index : 0);
    }

    // The slot under the pointer lights gold where an item dragged over it would be taken, and red where it would not.
    private void ShowIndicator(Point point, uint dragged)
    {
        var control = SlotControlAt(point);
        if (control == null || TagOf(control) is not { Kind: not CellKind.Root } || control.TranslatePoint(default, this) is not { } origin)
        {
            HideIndicator();
            return;
        }
        var (into, _) = DropPlace(point);
        var refused = into == dragged || Holds(into, dragged);
        _dropIndicator.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        _dropIndicator.Width = control.Bounds.Width;
        _dropIndicator.Height = control.Bounds.Height;
        _dropIndicator.BorderBrush = refused ? InvalidBrush : GoldBrush;
        _dropIndicator.IsVisible = true;
    }

    private void HideIndicator() => _dropIndicator.IsVisible = false;

    /// <summary>A retail drag (an item of the inventory's, or another window's) is over this window: its drop shows where it would land.</summary>
    public void RetailDragOver(uint itemId, string itemName, Point? position)
    {
        if (_disposed || _dragged != 0) return;
        if (itemId == 0 || position is not { } point) HideIndicator();
        else ShowIndicator(point, itemId);
    }

    /// <summary>
    /// An item released over the window goes into the container, or onto the pack under it, at the cell under it: the server places it. An
    /// item the container already holds goes nowhere. True when the move was sent.
    /// </summary>
    public bool RetailDrop(uint itemId, string itemName, Point position)
    {
        HideIndicator();
        if (_disposed || itemId == 0 || _state.Opened == 0) return false;
        var (into, place) = DropPlace(position);
        if (into == itemId || Holds(into, itemId)) return false;
        _port.MoveToContainer(itemId, into, place);
        return true;
    }
}
