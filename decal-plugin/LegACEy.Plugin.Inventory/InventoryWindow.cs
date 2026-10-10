using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;
using static LegACEy.Client.Themes.DerethItemCells;

namespace LegACEy.Plugin.Inventory;

/// <summary>
/// The inventory in one layout, drawn with the Dereth parts from the port's snapshot. It re-renders when the port changes, and
/// not while the owner has it <see cref="Suspend"/>ed (hidden); <see cref="Resume"/> draws the current state once.
/// Its own toggles (layout and Slots) are local and report through <see cref="SettingsChanged"/>; the owner decides what a layout
/// change does. A click on a slot selects its item, or opens a pack; a double-click uses the item; a drag moves it through the
/// port. The window never changes its own state: a command shows only when the port's next snapshot says so. An item released
/// outside every slot is offered to the LegACEy window under the pointer (a Vault deposit).
/// </summary>
public sealed class InventoryWindow : UserControl, IDisposable, IInventoryDropZone, IRetailItemDropTarget
{
    internal const uint BackpackIcon = 0x0600127E;
    internal const string Title = "Inventory";
    // The pyreal stack's icon in the portal.
    private const uint PyrealIcon = 0x06001080;
    private const double EmptyOpacity = 0.8;
    // A lifted item's slots fade while it is dragged, as the Vault's do.
    private const double LiftedOpacity = 0.4;
    // A figure: a head over shoulders and a body.
    private const string FigureGlyph = "M7,1.5 A2.2,2.2 0 1 1 6.99,1.5 Z M2.5,13 V9.5 Q2.5,6.8 7,6.8 Q11.5,6.8 11.5,9.5 V13 Z";
    private const string StackedGlyph = "M2,1 H12 V6 H2 Z M2,8 H12 V13 H2 Z";
    private const string SideBySideGlyph = "M1,2 H6 V12 H1 Z M8,2 H13 V12 H8 Z";
    private const double DragThreshold = 4;

    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush;
    private static readonly IBrush TextBrush = DerethPalette.TextBrush;
    private static readonly IBrush MutedBrush = DerethPalette.MutedBrush;
    private static readonly IBrush InvalidBrush = DerethPalette.InvalidBrush;
    private static readonly IBrush TealTextBrush = DerethPalette.Brush(DerethPalette.TealText);
    private static readonly IBrush GoldFillBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#E2C27E"), 0), new GradientStop(Color.Parse("#9C7A3C"), 1) },
    };

    private readonly IInventoryPort _port;
    private readonly IGameArtSource _art;
    private readonly InventoryLayout _layout;
    private PaperdollView? _doll;
    private readonly Dictionary<ItemVisual, WriteableBitmap?> _images = new();
    private readonly DerethWindow _frame;
    private readonly InventoryPaperdoll _paperdoll = new();
    private readonly DerethSlotGrid _grid = new();
    private readonly TextBlock _contentsName = Label(string.Empty, TextBrush, 13);
    private readonly TextBlock _contentsCount = Label(string.Empty, MutedBrush, 13);
    // The pack list: a column in the vertical layout, a strip of tabs in the horizontal one.
    private readonly Panel _packList;
    private readonly ContentControl _burdenMeter = new();
    private readonly TextBlock _burdenText = Label(string.Empty, TextBrush, 12);
    private readonly TextBlock _pyrealCount = Label(string.Empty, GoldBrush, 12);
    // Shown over the slot under the pointer while an item is dragged: a gold border where the drop is accepted, red where it is refused.
    private readonly Border _dropIndicator = new()
    {
        Name = "DropIndicator", IsVisible = false, IsHitTestVisible = false, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly IItemDragHost? _dragHost;
    // Where the indicator was last asked for, so a redraw during a drag puts it back at once instead of waiting for the next move.
    private (Point Point, uint Dragged, bool External)? _indicatorAt;
    // The item of a retail drag over this window (another retail window's item, or one of the retail inventory's), zero when none.
    private uint _retailItem;
    private bool _withdrawOver;
    // A press on a slot: a click on release, or a drag once it moves past the threshold. The item or pack being dragged is _dragged, zero when none.
    private InventorySlotId? _press;
    private Point _pressPoint;
    // How many presses in a row the press was, as the platform counts them: two or more is a double-click.
    private int _pressClicks;
    private uint _dragged;
    // The pack the player picked, or zero for the port's open container. Picking a pack is local, as in retail: the server answers a
    // use of a carried pack with only "use done", which flashed the busy cursor and changed nothing.
    private uint _picked;
    private IDisposable? _dragIcon;
    private bool _showSlots;
    // The equipment and paperdoll section is left out: the window is the packs and their contents. Fixed for the window's life.
    private readonly bool _collapsed;
    private bool _suspended;
    private bool _disposed;

    /// <param name="port">The character's inventory. The window reads its snapshot and re-renders on its change event.</param>
    /// <param name="art">The game art the icons are drawn from.</param>
    /// <param name="settings">The layout this window draws, and whether the armour slots show.</param>
    /// <param name="dragHost">Shows the icon of a dragged item under the pointer, and hands an item released outside the slots to the
    /// LegACEy window under the pointer. Null shows no icon and hands nothing over; the drag still works.</param>
    public InventoryWindow(IInventoryPort port, IGameArtSource art, InventorySettings settings, IItemDragHost? dragHost = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _dragHost = dragHost;
        _layout = settings.Layout;
        _showSlots = settings.ShowSlots;
        _collapsed = settings.Collapsed;
        _packList = _layout == InventoryLayout.Vertical
            ? new StackPanel { Spacing = 4 }
            : new WrapPanel { Orientation = Orientation.Horizontal };

        var body = _layout == InventoryLayout.Vertical ? VerticalBody() : HorizontalBody();
        _frame = new DerethWindow(Title, Icon(new ItemVisual(BackpackIcon, 0, 0, 0)), body, LayoutToggles());
        _frame.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Content = new Grid { Children = { _frame, _dropIndicator } };
        // The window takes the pointer on its own handlers, so a drag that leaves a slot, or the window, still ends here.
        AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndDrag(), handledEventsToo: true);

        _paperdoll.SlotsPressed += ToggleSlots;
        _paperdoll.SetSlotsOn(_showSlots);
        // Subscribe only once the first render has worked, so a failing render leaves no subscriber behind.
        Render(_port.Snapshot);
        _port.Changed += OnChanged;
    }

    /// <summary>The window's current settings: its layout, and the Slots toggle.</summary>
    private InventorySettings Settings => new(_layout, _showSlots, _collapsed);

    /// <summary>Raised when the player changes the layout or the Slots toggle. The settings are the new ones.</summary>
    public event Action<InventorySettings>? SettingsChanged;

    /// <summary>Raised when the header's close box is pressed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The doll area under the paperdoll: the 3D character while it shows, otherwise the empty dark panel.</summary>
    private Border DollArea => _paperdoll.DollArea;

    /// <summary>Whether the window has its 3D character yet.</summary>
    public bool HasDoll => _doll != null;

    /// <summary>
    /// Gives the window its 3D character, which shows in the doll area while the armour slots are off and the window is shown.
    /// It can come at any time: the server's actions may arrive after the window opened.
    /// </summary>
    public void SetDoll(PaperdollView doll)
    {
        _doll = doll;
        UpdateDoll();
    }

    /// <summary>
    /// Stops redrawing for port changes, and takes the 3D character out, while the window is hidden. A drag in progress ends.
    /// </summary>
    public void Suspend()
    {
        EndDrag();
        _suspended = true;
        UpdateDoll();
    }

    /// <summary>Draws the port's current state once, and redraws on each change again. Called when the window is shown.</summary>
    public void Resume()
    {
        if (_disposed) return;
        _suspended = false;
        // Each open starts the 3D character from its first view: facing the player, whole, unzoomed.
        _doll?.ResetView();
        Render(_port.Snapshot);
    }

    /// <summary>
    /// Sets the Slots toggle from outside, as the owner does when the other layout changed it while this window was hidden.
    /// It redraws without raising <see cref="SettingsChanged"/>.
    /// </summary>
    public void SetShowSlots(bool show)
    {
        if (_disposed || show == _showSlots) return;
        _showSlots = show;
        _paperdoll.SetSlotsOn(show);
        Render(_port.Snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EndDrag();
        _port.Changed -= OnChanged;
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
    }

    private void OnChanged()
    {
        if (!_disposed && !_suspended) Render(_port.Snapshot);
    }

    /// <summary>The 3D character is in the doll area only while the armour slots are off and the window is shown, so it asks for looks only then.</summary>
    private void UpdateDoll() => DollArea.Child = _doll != null && !_showSlots && !_suspended ? _doll : null;

    private void ToggleSlots()
    {
        SetShowSlots(!_showSlots);
        SettingsChanged?.Invoke(Settings);
    }

    /// <summary>
    /// A press on a slot. A right press assesses what the slot shows, as retail does. A left press sends nothing yet: a release
    /// decides between a click and a drop.
    /// </summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = e.GetCurrentPoint(this).Properties;
        if (SlotAt(e.GetPosition(this)) is not { } slot) return;
        if (button.IsRightButtonPressed && _press == null)
        {
            var id = (InventorySlotId)slot.Tag!;
            var item = id.Place == SlotPlace.Pack ? id.Container : id.ItemId;
            if (item != 0) _port.Assess(item);
            return;
        }
        if (!button.IsLeftButtonPressed) return;
        _press = (InventorySlotId)slot.Tag!;
        _pressPoint = e.GetPosition(this);
        _pressClicks = e.ClickCount;
        // Moves and the release come to this window even outside it, so a drag that leaves the window still ends here.
        e.Pointer.Capture(this);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press) return;
        var point = e.GetPosition(this);
        if (_dragged == 0)
        {
            if (!PastThreshold(point)) return;
            // Past the threshold the press is a drag or nothing, so it is never a click, even when it carries nothing.
            _dragged = DraggedId(press);
            if (_dragged == 0)
            {
                _press = null;
                return;
            }
            // Lifting an item selects it, as a click does, so another item's selection clears. A pack is never selected.
            if (press.Place != SlotPlace.Pack && _dragged != _port.Snapshot.Selected) _port.Select(_dragged);
            _dragIcon = _dragHost?.ShowDragIcon(DragImage(_dragged), 1, retailDropIndicator: false, _dragged);
            Fade(_dragged, lifted: true);
        }
        // Outside the window, with the button still held, retail may take the drag over; then it is no longer ours.
        if (!new Rect(Bounds.Size).Contains(point) && !_port.Snapshot.SidePacks.Any(pack => pack.Id == _dragged) &&
            _dragHost?.HandToRetail(_dragged) == true)
        {
            EndDrag();
            e.Pointer.Capture(null);
            return;
        }
        ShowIndicator(point);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only the button that started the press acts; another button's release leaves the press alone.
        if (_press is not { } press || e.InitialPressMouseButton != MouseButton.Left) return;
        var dragged = _dragged;
        var point = e.GetPosition(this);
        var clicks = _pressClicks;
        // A release past the threshold where no drag began (a focus loss, for one) is not a click.
        var click = dragged == 0 && !PastThreshold(point);
        EndDrag();
        if (click)
        {
            Click(press, clicks);
            return;
        }
        if (dragged == 0) return;
        // Over a slot the drop is judged as it always was: a refused one sends nothing.
        if (SlotAt(point)?.Tag is InventorySlotId target)
        {
            var drop = Judge(_port, dragged, target);
            if (drop?.Accepted == true) drop.Value.Send(_port);
            return;
        }
        HandOffOutside(dragged);
    }

    /// <summary>
    /// A release outside every slot: an item goes to the LegACEy window under the pointer, if that window takes it (a Vault deposit).
    /// Side packs stay here.
    /// </summary>
    private void HandOffOutside(uint dragged)
    {
        var snapshot = _port.Snapshot;
        if (_dragHost == null || snapshot.SidePacks.Any(pack => pack.Id == dragged) || !snapshot.Contains(dragged)) return;
        if (_dragHost.DeliverAtPointer(dragged, KindOf(snapshot, dragged).Name)) return;
        // Retail did not take the drag when it left the window (the retail panel shows no element for the item): only a release over
        // the world sends anything.
        if (_dragHost.DropTargetAtPointer() == ItemDropTarget.World) _port.DropOnGround(dragged);
    }

    /// <summary>
    /// A retail drag (another retail window's item, or the retail inventory's) is over this window. It shows the same drop indicator as
    /// our own drags. Our own drag keeps its indicator; a drag that is not over this window clears it.
    /// </summary>
    public void RetailDragOver(uint itemId, string itemName, Point? position)
    {
        if (_disposed || _dragged != 0) return;
        if (itemId == 0 || position is not { } point)
        {
            if (_retailItem != 0)
            {
                _retailItem = 0;
                _dropIndicator.IsVisible = false;
            }
            return;
        }
        _retailItem = itemId;
        ShowIndicator(point, itemId, external: !_port.Snapshot.Contains(itemId));
    }

    /// <summary>
    /// A Vault withdraw is over this window. The withdrawn item goes into the inventory wherever it is released, so the indicator is
    /// gold over any pack cell or pack, and hidden over the paperdoll, where it cannot be worn straight from the Vault.
    /// </summary>
    public void WithdrawDragOver(Point? position)
    {
        if (_disposed || _dragged != 0 || _retailItem != 0) return;
        var slot = position is { } point ? SlotAt(point) : null;
        if (slot?.Tag is InventorySlotId { Place: not SlotPlace.Paperdoll })
        {
            _withdrawOver = true;
            Light(slot, GoldBrush);
        }
        else if (_withdrawOver)
        {
            _withdrawOver = false;
            _dropIndicator.IsVisible = false;
        }
    }

    /// <summary>
    /// Where a Vault withdraw released at the position goes: the cell under it, in its pack, or the front of a pack whose tile it is on.
    /// Anywhere else, null: the server puts it where withdrawals always go.
    /// </summary>
    public (uint Container, int Position)? WithdrawPlaceAt(Point position) => SlotAt(position)?.Tag switch
    {
        InventorySlotId { Place: SlotPlace.Cell } cell => (cell.Container, cell.SlotIndex),
        InventorySlotId { Place: SlotPlace.Pack, Container: not 0 } pack => (pack.Container, 0),
        _ => null,
    };

    /// <summary>
    /// A retail drag released over this window. An item from outside our inventory goes onto the slot it is released on, or, released
    /// elsewhere in the window, to the first free slot of the open pack. An item of ours is only moved by its slots, so it is declined
    /// elsewhere. Returns true when the item was used; declined items go back to where they came from.
    /// </summary>
    public bool RetailDrop(uint itemId, string itemName, Point position)
    {
        _retailItem = 0;
        _dropIndicator.IsVisible = false;
        if (_disposed || itemId == 0) return false;
        var snapshot = _port.Snapshot;
        var external = !snapshot.Contains(itemId);
        var target = SlotAt(position)?.Tag as InventorySlotId;
        if (target == null && external) target = new InventorySlotId(SlotPlace.Pack, 0, OpenPack(snapshot).Id, -1, null);
        if (target == null) return false;
        var drop = Judge(_port, itemId, target, external);
        if (drop?.Accepted != true) return false;
        drop.Value.Send(_port);
        return true;
    }

    /// <summary>A click on release: a pack opens; an item is selected, or used when the press was the second click on it.</summary>
    private void Click(InventorySlotId slot, int clicks)
    {
        if (slot.Place == SlotPlace.Pack)
        {
            // A focus takes a pack slot but holds nothing, so it does not open.
            if (slot.Container != 0 && PackOf(_port.Snapshot, slot.Container)?.Capacity != 0)
            {
                _picked = slot.Container;
                Render(_port.Snapshot);
            }
            return;
        }
        if (slot.ItemId == 0) return;
        if (clicks >= 2) _port.Use(slot.ItemId);
        else _port.Select(slot.ItemId);
    }

    private void EndDrag()
    {
        Fade(_dragged, lifted: false);
        _press = null;
        _dragged = 0;
        _dragIcon?.Dispose();
        _dragIcon = null;
        _indicatorAt = null;
        _dropIndicator.IsVisible = false;
    }

    /// <summary>Shows the indicator over the slot under the pointer: gold when the drop is accepted, red when it is refused.</summary>
    private void ShowIndicator(Point point) => ShowIndicator(point, _dragged, external: false);

    /// <summary>The drop indicator for a dragged item over a point: the slot under it, when the drop there is judged.</summary>
    private void ShowIndicator(Point point, uint dragged, bool external)
    {
        _indicatorAt = (point, dragged, external);
        var slot = SlotAt(point);
        var drop = slot?.Tag is InventorySlotId target ? Judge(_port, dragged, target, external) : null;
        if (slot == null || drop == null)
        {
            _dropIndicator.IsVisible = false;
            return;
        }
        Light(slot, drop.Value.Accepted ? GoldBrush : InvalidBrush);
    }

    /// <summary>Shows the drop indicator over the slot, in the colour.</summary>
    private void Light(DerethSlot slot, IBrush brush)
    {
        if (slot.TranslatePoint(default, this) is not { } origin)
        {
            _dropIndicator.IsVisible = false;
            return;
        }
        _dropIndicator.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        _dropIndicator.Width = slot.Bounds.Width;
        _dropIndicator.Height = slot.Bounds.Height;
        _dropIndicator.BorderBrush = brush;
        _dropIndicator.IsVisible = true;
    }

    /// <summary>
    /// Fades every slot showing the item while it is lifted, as the Vault fades a lifted cell, and outlines it as a click's selection
    /// does; or brings them back, outlined only if the item is retail's selection.
    /// </summary>
    private void Fade(uint id, bool lifted)
    {
        if (id == 0) return;
        foreach (var slot in this.GetVisualDescendants().OfType<DerethSlot>())
            if (slot.Tag is InventorySlotId tag && DraggedId(tag) == id)
            {
                slot.Opacity = lifted ? LiftedOpacity : 1;
                slot.Selected = lifted || (tag.Place == SlotPlace.Pack ? tag.Container == OpenPack(_port.Snapshot).Id : id == _port.Snapshot.Selected);
            }
    }

    /// <summary>The slot under a point of this window, or null over anything the window does not draw as a slot.</summary>
    private DerethSlot? SlotAt(Point point)
    {
        for (var visual = this.GetVisualAt(point); visual != null; visual = visual.GetVisualParent())
            if (visual is DerethSlot { Tag: InventorySlotId } slot) return slot;
        return null;
    }

    /// <summary>What a press on a slot carries: its item, or a side pack. Zero for an empty slot and for the main pack, which is not dragged.</summary>
    private uint DraggedId(InventorySlotId slot)
    {
        if (slot.Place != SlotPlace.Pack) return slot.ItemId;
        return slot.Container != 0 && _port.Snapshot.SidePacks.Any(pack => pack.Id == slot.Container) ? slot.Container : 0;
    }

    private GameImage? DragImage(uint id)
    {
        var snapshot = _port.Snapshot;
        var visual = snapshot.Items.FirstOrDefault(item => item.Id == id)?.Visual ?? snapshot.Wielded.FirstOrDefault(item => item.Id == id)?.Visual
            ?? new ItemVisual(snapshot.SidePacks.FirstOrDefault(pack => pack.Id == id)?.Icon ?? 0, 0, 0, 0);
        return ItemIcon.Draw(_art, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects);
    }

    /// <summary>Whether a point has moved past the drag threshold from where the press began.</summary>
    private bool PastThreshold(Point point) =>
        Math.Abs(point.X - _pressPoint.X) >= DragThreshold || Math.Abs(point.Y - _pressPoint.Y) >= DragThreshold;

    /// <summary>A drop that is red: nothing is sent.</summary>
    private static readonly (bool Accepted, Action<IInventoryPort> Send)? Refused = (false, _ => { });

    /// <summary>
    /// What dropping the dragged item or pack on a slot does. Null: nothing shows and nothing is sent (the item's own slot, or an
    /// item the snapshot no longer holds). Accepted false: red, nothing sent. Accepted true: gold, and the drop sends its command.
    /// <paramref name="external"/> marks an item from another window, which the snapshot does not hold.
    /// </summary>
    private static (bool Accepted, Action<IInventoryPort> Send)? Judge(IInventoryPort port, uint dragged, InventorySlotId target, bool external = false)
    {
        var s = port.Snapshot;
        if (!external && !s.Contains(dragged)) return null;
        if (s.SidePacks.Any(pack => pack.Id == dragged)) return PackOnto(s, dragged, target);
        switch (target.Place)
        {
            case SlotPlace.Paperdoll:
                if (target.ItemId == dragged || target.Equipment is not { } slot) return null;
                // The port's mask covers an item from another window too (it reads the item's locations from the world).
                return port.WieldMask(dragged, slot) == 0 ? Refused : (true, p => p.Wield(dragged, slot));

            case SlotPlace.Pack:
            {
                if (PackOf(s, target.Container) is not { } into) return Refused;
                var free = FirstFree(s, into);
                return free < 0 ? Refused : (true, p => p.MoveToContainer(dragged, into.Id, free));
            }

            case SlotPlace.Cell:
            {
                var occupant = s.Items.FirstOrDefault(item => item.Container == target.Container && item.Slot == target.SlotIndex);
                if (occupant != null && occupant.Id == dragged) return null;
                // Stacks of the same kind merge. Any other item dropped on a cell is moved there, and the server places it.
                var (name, stackMax, wcid) = KindOf(s, dragged);
                if (occupant != null && occupant.StackMax > 1 && stackMax > 1 && occupant.Name == name && occupant.Wcid == wcid)
                    return (true, p => p.MergeStack(dragged, occupant.Id));
                return (true, p => p.MoveToContainer(dragged, target.Container, target.SlotIndex));
            }
        }
        return null;
    }

    /// <summary>
    /// What dropping a side pack does. Side packs are numbered on their own, so a pack goes to a side-pack position: the position
    /// of the tile it lands on, or the first empty position when it lands on the main pack. A pack dropped into a side pack's grid
    /// is accepted and the server refuses it.
    /// </summary>
    private static (bool Accepted, Action<IInventoryPort> Send)? PackOnto(InventorySnapshot s, uint pack, InventorySlotId target)
    {
        var main = s.MainPack.Id;
        if (target.Place == SlotPlace.Paperdoll || target.Container == pack) return Refused;
        if (target.Place == SlotPlace.Cell && target.Container != main) return (true, p => p.MoveToContainer(pack, target.Container, target.SlotIndex));
        var position = target.Place == SlotPlace.Pack && target.Container != main ? target.SlotIndex : FirstEmptySidePosition(s);
        return (true, p => p.MoveToContainer(pack, main, position));
    }

    /// <summary>The position of the first empty side-pack slot, or the number of side packs when none is empty.</summary>
    private static int FirstEmptySidePosition(InventorySnapshot s)
    {
        for (var position = 0; position < s.SidePacks.Count; position++)
            if (s.SidePacks[position].Id == 0) return position;
        return s.SidePacks.Count;
    }

    private static InventoryPack? PackOf(InventorySnapshot s, uint id) =>
        id == 0 ? null : s.MainPack.Id == id ? s.MainPack : s.SidePacks.FirstOrDefault(pack => pack.Id == id);

    /// <summary>The first slot of a pack that holds no item, or -1 when the pack is full.</summary>
    private static int FirstFree(InventorySnapshot s, InventoryPack pack)
    {
        var used = new HashSet<int>(s.Items.Where(item => item.Container == pack.Id).Select(item => item.Slot));
        for (var slot = 0; slot < pack.Capacity; slot++)
            if (!used.Contains(slot)) return slot;
        return -1;
    }

    /// <summary>The name, stack maximum and weenie class id (WCID) of an item the snapshot holds, carried or worn.</summary>
    private static (string Name, int StackMax, int Wcid) KindOf(InventorySnapshot s, uint id)
    {
        var item = s.Items.FirstOrDefault(candidate => candidate.Id == id);
        if (item != null) return (item.Name, item.StackMax, item.Wcid);
        var worn = s.Wielded.FirstOrDefault(candidate => candidate.Id == id);
        return worn != null ? (worn.Name, worn.StackMax, worn.Wcid) : (string.Empty, 0, 0);
    }

    private void RequestLayout(InventoryLayout layout)
    {
        if (layout != _layout) SettingsChanged?.Invoke(new InventorySettings(layout, _showSlots, _collapsed));
    }

    private void Render(InventorySnapshot snapshot)
    {
        if (_disposed) return;
        // The slots are rebuilt, so a drop indicator on the old ones goes; a drag in progress gets it back once they are laid out.
        _dropIndicator.IsVisible = false;
        var open = OpenPack(snapshot);
        var worn = new Dictionary<PaperdollSlot, WieldedItem>();
        foreach (var item in snapshot.Wielded)
            foreach (var slot in item.Slots)
                worn[slot] = item;
        _paperdoll.Show(slot => WornSlot(snapshot, worn, slot), _showSlots);
        UpdateDoll();
        RenderPacks(snapshot, open);
        RenderContents(snapshot, open);
        RenderBurden(snapshot);
        _pyrealCount.Text = snapshot.Pyreals.ToString("N0");
        Fade(_dragged, lifted: true);
        if (_indicatorAt is { } at && (_dragged != 0 || _retailItem != 0))
        {
            UpdateLayout();
            ShowIndicator(at.Point, at.Dragged, at.External);
        }
    }

    /// <summary>The pack the grid shows: the picked pack, else the port's open container, else the main pack.</summary>
    private InventoryPack OpenPack(InventorySnapshot snapshot)
    {
        var open = _picked != 0 ? _picked : snapshot.OpenContainer;
        return open == snapshot.MainPack.Id ? snapshot.MainPack
            : snapshot.SidePacks.FirstOrDefault(pack => pack.Id != 0 && pack.Id == open) ?? snapshot.MainPack;
    }

    private DerethSlot WornSlot(InventorySnapshot snapshot, Dictionary<PaperdollSlot, WieldedItem> worn, PaperdollSlot slot)
    {
        if (!worn.TryGetValue(slot, out var item))
            return new DerethSlot(InventoryGlyphs.For(slot)) { Tag = new InventorySlotId(SlotPlace.Paperdoll, 0, 0, -1, slot) };
        return Slot(new InventorySlotId(SlotPlace.Paperdoll, item.Id, 0, -1, slot), Layers(item.Visual, item.StackCount), item.Id == snapshot.Selected);
    }

    private void RenderPacks(InventorySnapshot snapshot, InventoryPack open)
    {
        _packList.Children.Clear();
        // A side pack's tile carries its position in the side-pack list: a drop of a pack reorders by that position.
        if (_layout == InventoryLayout.Vertical)
        {
            _packList.Children.Add(PackColumnSlot(snapshot, snapshot.MainPack, -1, open));
            _packList.Children.Add(new DerethRule { Margin = new Thickness(0, 2), Opacity = 0.7 });
            for (var position = 0; position < snapshot.SidePacks.Count; position++)
                _packList.Children.Add(PackColumnSlot(snapshot, snapshot.SidePacks[position], position, open));
            return;
        }
        _packList.Children.Add(PackTab(snapshot, snapshot.MainPack, -1, open));
        _packList.Children.Add(new Border { Width = 1, Background = DerethPalette.GrooveEdgeBrush, Margin = new Thickness(3, 4) });
        for (var position = 0; position < snapshot.SidePacks.Count; position++)
            _packList.Children.Add(PackTab(snapshot, snapshot.SidePacks[position], position, open));
    }

    /// <summary>A pack in the vertical column: its icon, with a fill bar along the foot. An empty side-pack slot is a placeholder.</summary>
    private DerethSlot PackColumnSlot(InventorySnapshot snapshot, InventoryPack pack, int position, InventoryPack open)
    {
        if (pack.Id == 0) return Empty(new InventorySlotId(SlotPlace.Pack, 0, 0, position, null));
        var layers = new Grid();
        layers.Children.Add(Icon(new ItemVisual(pack.Icon, 0, 0, 0)));
        if (pack.Capacity > 0) layers.Children.Add(FillBar(ItemsIn(snapshot, pack.Id), pack.Capacity));
        return Slot(new InventorySlotId(SlotPlace.Pack, 0, pack.Id, position, null), layers, pack.Id == open.Id);
    }

    /// <summary>A pack in the horizontal strip: its icon, with "n/cap" under it. An empty side-pack slot is a placeholder.</summary>
    private Control PackTab(InventorySnapshot snapshot, InventoryPack pack, int position, InventoryPack open)
    {
        var stack = new StackPanel { Spacing = 2, Width = DerethSlotGrid.Pitch };
        if (pack.Id == 0)
        {
            stack.Children.Add(Empty(new InventorySlotId(SlotPlace.Pack, 0, 0, position, null)));
            return stack;
        }
        var selected = pack.Id == open.Id;
        stack.Children.Add(Slot(new InventorySlotId(SlotPlace.Pack, 0, pack.Id, position, null), Icon(new ItemVisual(pack.Icon, 0, 0, 0)), selected));
        var count = Label(pack.Capacity > 0 ? $"{ItemsIn(snapshot, pack.Id)}/{pack.Capacity}" : " ", selected ? TealTextBrush : MutedBrush, 10);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(count);
        return stack;
    }

    private void RenderContents(InventorySnapshot snapshot, InventoryPack open)
    {
        var items = new Dictionary<int, InventoryItem>();
        foreach (var item in snapshot.Items)
            if (item.Container == open.Id) items[item.Slot] = item;
        _contentsName.Text = "Contents of " + open.Name;
        _contentsCount.Text = $"{ItemsIn(snapshot, open.Id)} / {open.Capacity}";
        // One cell per slot of the container, empty ones included, in slot-index order. The grid keeps its scroll position.
        _grid.Cells.Clear();
        for (var index = 0; index < open.Capacity; index++)
        {
            items.TryGetValue(index, out var item);
            _grid.Cells.Add(Cell(snapshot, open.Id, index, item));
        }
    }

    private DerethSlot Cell(InventorySnapshot snapshot, uint container, int index, InventoryItem? item) =>
        item == null
            ? Empty(new InventorySlotId(SlotPlace.Cell, 0, container, index, null))
            : Slot(new InventorySlotId(SlotPlace.Cell, item.Id, container, index, null), Layers(item.Visual, item.StackCount), item.Id == snapshot.Selected);

    private static DerethSlot Slot(InventorySlotId id, Control content, bool selected) => new(content) { Tag = id, Selected = selected };

    private static DerethSlot Empty(InventorySlotId id) => new(null) { Tag = id, Opacity = EmptyOpacity };

    private void RenderBurden(InventorySnapshot snapshot)
    {
        var over = snapshot.BurdenLimit > 0 && snapshot.Burden > snapshot.BurdenLimit;
        var percent = snapshot.BurdenLimit > 0 ? snapshot.Burden * 100 / snapshot.BurdenLimit : 0;
        var vertical = _layout == InventoryLayout.Vertical;
        _burdenMeter.Content = BurdenMeter(snapshot, over, vertical);
        // Over the limit the reading turns red as well as the meter, so it reads at a glance.
        _burdenText.Text = vertical ? $"{percent}%" : $"{snapshot.Burden:N0} / {snapshot.BurdenLimit:N0}  ·  {percent}%";
        _burdenText.Foreground = over ? InvalidBrush : TextBrush;
    }

    private static int ItemsIn(InventorySnapshot snapshot, uint container) => snapshot.Items.Count(item => item.Container == container);

    private static Control FillBar(int count, int capacity)
    {
        var share = capacity <= 0 ? 0 : Math.Min(1.0, (double)count / capacity);
        var track = new Border
        {
            Width = 30, Height = 3, Background = DerethPalette.GrooveBrush, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
        };
        track.Child = new Border { Width = 30 * share, HorizontalAlignment = HorizontalAlignment.Left, Background = GoldBrush };
        return track;
    }

    /// <summary>A groove filled in gold to the burden. Over the limit the fill turns red.</summary>
    private static Control BurdenMeter(InventorySnapshot snapshot, bool over, bool vertical)
    {
        var share = snapshot.BurdenLimit <= 0 ? 0 : Math.Min(1.0, (double)snapshot.Burden / snapshot.BurdenLimit);
        var track = new Border
        {
            Background = DerethPalette.GrooveBrush, BorderBrush = DerethPalette.GrooveEdgeBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), Padding = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
        };
        if (vertical)
        {
            track.Width = 12;
            track.Height = 62;
        }
        else
        {
            track.Height = 8;
        }
        var fill = new Border { Background = over ? InvalidBrush : GoldFillBrush, CornerRadius = new CornerRadius(1) };
        var holder = new Grid();
        if (vertical)
        {
            holder.RowDefinitions = new RowDefinitions($"{1 - share:0.###}*,{share:0.###}*");
            Grid.SetRow(fill, 1);
        }
        else
        {
            holder.ColumnDefinitions = new ColumnDefinitions($"{share:0.###}*,{1 - share:0.###}*");
        }
        holder.Children.Add(fill);
        track.Child = holder;
        return track;
    }

    /// <summary>An item's icon and, for a stack, its count, in a slot-sized layer stack.</summary>
    private Grid Layers(ItemVisual visual, int stack)
    {
        var layers = Icon(visual);
        if (stack > 1) layers.Children.Add(StackCount(stack));
        return layers;
    }

    private Grid Icon(ItemVisual visual) => DerethItemCells.Icon(Bitmap(visual));

    /// <summary>The drawn icon, cached per look. The grid redraws on every change, so the bitmaps are kept.</summary>
    private WriteableBitmap? Bitmap(ItemVisual visual)
    {
        if (!_images.TryGetValue(visual, out var bitmap))
            _images.Add(visual, bitmap = GameArtImageExtension.CreateBitmap(ItemIcon.Draw(_art, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects, visual.Plate)));
        return bitmap;
    }

    private Control VerticalBody()
    {
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        if (!_collapsed)
        {
            left.Children.Add(At(_paperdoll, 0));
            left.Children.Add(At(new DerethRule { Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 }, 1));
        }
        left.Children.Add(At(ContentsLine(), 2));
        left.Children.Add(At(_grid, 3));

        // The pack column: burden at its head, then the main pack, a rule and the side packs, which scroll.
        var packs = new ScrollViewer
        {
            Content = _packList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var column = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Width = 44 };
        column.Children.Add(At(Centred(_burdenMeter), 0));
        column.Children.Add(At(Centred(_burdenText), 1));
        column.Children.Add(At(packs, 2));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(2, 8, 2, 0) };
        body.Children.Add(At(left, 0, 0));
        body.Children.Add(At(column, 0, 1));
        var footer = PyrealsFooter();
        body.Children.Add(At(footer, 1, 0));
        Grid.SetColumnSpan(footer, 2);
        return body;
    }

    private Control HorizontalBody()
    {
        var left = new StackPanel { Children = { _paperdoll } };
        var rule = new Border { Width = 1, Background = DerethPalette.GoldWashBrush, Margin = new Thickness(0, 4) };

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        _packList.Margin = new Thickness(0, 0, 0, 6);
        right.Children.Add(At(_packList, 0));
        right.Children.Add(At(ContentsLine(), 1));
        right.Children.Add(At(_grid, 2));

        // The footer: pyreals, then the burden meter with its reading.
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(2, 6, 2, 0) };
        footer.Children.Add(PyrealsLine());
        footer.Children.Add(At(Label("Burden", MutedBrush, 12), 0, 1));
        // The meter stretches across its star column, so it has a width to fill.
        _burdenMeter.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(At(_burdenMeter, 0, 2));
        footer.Children.Add(At(_burdenText, 0, 3));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), RowDefinitions = new RowDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(2, 8, 2, 0) };
        if (!_collapsed)
        {
            body.Children.Add(At(left, 0, 0));
            body.Children.Add(At(rule, 0, 1));
        }
        else body.ColumnSpacing = 0;
        body.Children.Add(At(right, 0, 2));
        body.Children.Add(At(footer, 1, 0));
        Grid.SetColumnSpan(footer, 3);
        return body;
    }

    private Control LayoutToggles()
    {
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        toggles.Children.Add(LayoutToggle(InventoryLayout.Vertical, StackedGlyph));
        toggles.Children.Add(LayoutToggle(InventoryLayout.Horizontal, SideBySideGlyph));
        toggles.Children.Add(EquipmentToggle());
        return toggles;
    }

    /// <summary>A header button that shows or collapses the equipment and paperdoll section. Lit teal while the section shows.</summary>
    private Control EquipmentToggle()
    {
        var icon = new Path
        {
            Data = Geometry.Parse(FigureGlyph), Width = 14, Height = 14, StrokeThickness = 1.2, UseLayoutRounding = false,
            Stroke = _collapsed ? MutedBrush : DerethPalette.TealBrush,
            Fill = _collapsed ? null : DerethPalette.Brush(DerethPalette.Teal.WithAlpha(0x40)),
        };
        var button = new DerethButton { Width = 24, Height = 24, Name = "EquipmentToggle", Content = icon, Margin = new Thickness(4, 0, 0, 0) };
        ToolTip.SetTip(button, _collapsed ? "Show equipment" : "Hide equipment");
        button.Click += (_, _) => SettingsChanged?.Invoke(new InventorySettings(_layout, _showSlots, !_collapsed));
        return button;
    }

    /// <summary>A header button that switches to its layout. The window's own layout is lit teal.</summary>
    private Control LayoutToggle(InventoryLayout layout, string glyph)
    {
        var active = layout == _layout;
        var icon = new Path
        {
            // Unrounded, so the glyph sits at the button's exact centre: at a scale the button can round to an odd number of
            // pixels and the glyph to an even one, and snapping both would push the glyph a pixel off.
            Data = Geometry.Parse(glyph), Width = 14, Height = 14, StrokeThickness = 1.2, UseLayoutRounding = false,
            Stroke = active ? DerethPalette.TealBrush : MutedBrush,
            Fill = active ? DerethPalette.Brush(DerethPalette.Teal.WithAlpha(0x40)) : null,
        };
        var button = new DerethButton { Width = 24, Height = 24, Tag = layout, Content = icon };
        button.Click += (_, _) => RequestLayout(layout);
        return button;
    }

    private Control ContentsLine()
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 6, 2, 6) };
        line.Children.Add(_contentsName);
        line.Children.Add(At(_contentsCount, 0, 1));
        return line;
    }

    /// <summary>The pyreal count with its icon, as the footer's first item.</summary>
    private Control PyrealsLine()
    {
        var money = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        money.Children.Add(new Image { Source = Bitmap(new ItemVisual(PyrealIcon, 0, 0, 0)), Width = 20, Height = 20 });
        money.Children.Add(_pyrealCount);
        return money;
    }

    /// <summary>The vertical layout's footer: the pyreals alone.</summary>
    private Grid PyrealsFooter()
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 6, 2, 0) };
        line.Children.Add(PyrealsLine());
        return line;
    }

    private static T At<T>(T control, int row, int column = 0) where T : Control
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        return control;
    }

    private static T Centred<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Center;
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }
}
