using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Ctrl and Shift clicks pick several items of the open pack, as in the Vault. A picked set lifted together goes only into a pack or
/// to the Vault; retail never sees it.
/// </summary>
public sealed class InventoryWindow : UserControl, IDisposable, IInventoryDropZone, IRetailItemDropTarget
{
    internal const string Title = "Inventory";
    /// <summary>The grid is retail's main pack: six columns, whatever the width.</summary>
    internal const int RetailColumns = 6;
    private const double EmptyOpacity = 0.8;
    // A lifted item's slots fade while it is dragged, as the Vault's do.
    private const double LiftedOpacity = 0.4;
    private const double DragThreshold = 4;

    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush;
    private static readonly IBrush TextBrush = DerethPalette.TextBrush;
    private static readonly IBrush MutedBrush = DerethPalette.MutedBrush;
    private static readonly IBrush InvalidBrush = DerethPalette.InvalidBrush;
    private static readonly IBrush TealTextBrush = DerethPalette.Brush(DerethPalette.TealText);
    // Retail's burden bands: green to the limit, yellow to twice it, red to three times it.
    private static readonly IBrush[] BurdenBandBrushes =
        [DerethPalette.Brush(Color.Parse("#3FA83A")), DerethPalette.Brush(Color.Parse("#D8C23A")), DerethPalette.InvalidBrush];

    private readonly IInventoryPort _port;
    private readonly IGameArtSource _art;
    private PaperdollView? _doll;
    private readonly Dictionary<ItemVisual, WriteableBitmap?> _images = new();
    private readonly DerethWindow _frame;
    private readonly InventoryPaperdoll _paperdoll = new();
    // Its columns start under the contents line's name.
    private readonly DerethSlotGrid _grid = new() { ColumnAlignment = HorizontalAlignment.Left };
    // Shrinks to fit beside the count, as a window title does, before it trims.
    private readonly DerethTitleText _contentsName = new()
    {
        Foreground = TextBrush, FontFamily = DerethPalette.Body, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly TextBlock _contentsCount = new()
    {
        Foreground = MutedBrush, FontSize = 13, FontFamily = DerethPalette.Body, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
    };
    // The pack list: the main pack and the side packs, in a column right of the grid.
    private readonly StackPanel _packList = new() { Spacing = 4 };
    private readonly ContentControl _burdenMeter = new();
    private readonly TextBlock _burdenText = Label(string.Empty, TextBrush, 12);
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
    // Side packs this window dropped on the ground. Once on the ground a pack is not in the snapshot, so a retail drop of it back
    // is an outside item; these ids say it is a pack, which goes to a side slot as an own pack does.
    private readonly HashSet<uint> _packsOnGround = new();
    private bool _withdrawOver;
    // A press on a slot: a click on release, or a drag once it moves past the threshold. The item or pack being dragged is _dragged, zero when none.
    private InventorySlotId? _press;
    private Point _pressPoint;
    // How many presses in a row the press was, as the platform counts them: two or more is a double-click.
    private int _pressClicks;
    private uint _dragged;
    // The item last reported under the pointer, so the port hears of each change once.
    private uint _hovered;
    // The items picked with Ctrl and Shift in the open pack, and the modifiers held when the press began.
    private readonly InventorySelection _selection = new();
    private KeyModifiers _pressModifiers;
    // The set a drag carries, in pack order: the picked items when one of two or more picked was lifted, else empty.
    private IReadOnlyList<uint> _carried = Array.Empty<uint>();
    // The pack the player picked, or zero for the port's open container. Picking a pack is local, as in retail: the server answers a
    // use of a carried pack with only "use done", which flashed the busy cursor and changed nothing.
    private uint _picked;
    private IDisposable? _dragIcon;
    private bool _showSlots;
    private bool _suspended;
    private bool _disposed;

    /// <param name="port">The character's inventory. The window reads its snapshot and re-renders on its change event.</param>
    /// <param name="art">The game art the icons are drawn from.</param>
    /// <param name="settings">Whether the armour slots show.</param>
    /// <param name="dragHost">Shows the icon of a dragged item under the pointer, and hands an item released outside the slots to the
    /// LegACEy window under the pointer. Null shows no icon and hands nothing over; the drag still works.</param>
    public InventoryWindow(IInventoryPort port, IGameArtSource art, InventorySettings settings, IItemDragHost? dragHost = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _dragHost = dragHost;
        _showSlots = settings.ShowSlots;

        _frame = new DerethWindow(Title, null, Body(), centreTitle: true);
        _frame.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Content = new Grid { Children = { _frame, _dropIndicator } };
        // The window takes the pointer on its own handlers, so a drag that leaves a slot, or the window, still ends here.
        AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndDrag(), handledEventsToo: true);
        AddHandler(InputElement.PointerExitedEvent, (_, _) => Hover(0), handledEventsToo: true);

        _paperdoll.SlotsPressed += ToggleSlots;
        _paperdoll.ExpandButton.Click += (_, _) => InspectRequested?.Invoke();
        _paperdoll.SetSlotsOn(_showSlots);
        // Subscribe only once the first render has worked, so a failing render leaves no subscriber behind.
        Render(_port.Snapshot);
        _port.Changed += OnChanged;
    }

    /// <summary>The window's current settings: the Slots toggle.</summary>
    private InventorySettings Settings => new(_showSlots);

    /// <summary>Raised when the player changes the Slots toggle. The settings are the new ones.</summary>
    public event Action<InventorySettings>? SettingsChanged;

    /// <summary>Raised when the player asks to see the character in the larger Inspect window.</summary>
    public event Action? InspectRequested;

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
    private void UpdateDoll()
    {
        DollArea.Child = _doll != null && !_showSlots && !_suspended ? _doll : null;
        _paperdoll.ExpandButton.IsVisible = DollArea.Child != null;
    }

    private void ToggleSlots()
    {
        SetShowSlots(!_showSlots);
        SettingsChanged?.Invoke(Settings);
    }

    /// <summary>
    /// A press on a slot, or on a worn item on the doll. A right press assesses what the slot shows, as retail does. A left press sends
    /// nothing yet: a release decides between a click and a drop.
    /// </summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = e.GetCurrentPoint(this).Properties;
        if (PressAt(e.GetPosition(this)) is not { } id) return;
        if (button.IsRightButtonPressed && _press == null)
        {
            var item = id.Place == SlotPlace.Pack ? id.Container : id.ItemId;
            if (item != 0) _port.Assess(item);
            return;
        }
        if (!button.IsLeftButtonPressed) return;
        _press = id;
        _pressModifiers = e.KeyModifiers;
        _pressPoint = e.GetPosition(this);
        _pressClicks = e.ClickCount;
        // Moves and the release come to this window even outside it, so a drag that leaves the window still ends here.
        e.Pointer.Capture(this);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_press is not { } press)
        {
            Hover(PressAt(point) is { } hovered ? TargetOf(hovered) : 0);
            return;
        }
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
            // A picked item of two or more lifts the whole set, which stays picked; any other item drops the set and lifts alone, as in the Vault.
            if (press.Place != SlotPlace.Pack && _selection.Count > 1)
            {
                if (_selection.Contains(_dragged)) _carried = _selection.InOrder(OpenOrder(_port.Snapshot));
                else _selection.Clear();
                ShowSelection();
            }
            // Lifting an item selects it, as a click does, so another item's selection clears. A lifted pack is never selected, not even a focus; nor is a set.
            if (press.Place != SlotPlace.Pack && _carried.Count == 0 && _dragged != _port.Snapshot.Selected) _port.Select(_dragged);
            _dragIcon = _dragHost?.ShowDragIcon(DragImage(_dragged), Math.Max(1, _carried.Count), retailDropIndicator: false, _dragged);
            Fade(_dragged, lifted: true);
        }
        // Outside the window, with the button still held, retail may take the drag over; then it is no longer ours. Never a set.
        // Side packs go too (retail's pack column lists them), so one can be sold to a vendor or dropped as retail drops it.
        if (_carried.Count == 0 && !new Rect(Bounds.Size).Contains(point) && _dragHost?.HandToRetail(_dragged) == true)
        {
            // A pack retail drops on the ground comes back to the pack column, as one we dropped there does.
            if (_port.Snapshot.SidePacks.Any(pack => pack.Id == _dragged)) _packsOnGround.Add(_dragged);
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
        var carried = _carried;
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
        // Over a slot, or the doll, the drop is judged as it always was: a refused one sends nothing.
        if ((carried.Count > 1 ? SlotAt(point)?.Tag as InventorySlotId : DropAt(point, dragged)?.Target) is { } target)
        {
            var drop = carried.Count > 1 ? JudgeSet(_port.Snapshot, carried, target) : Judge(_port, dragged, target);
            if (drop?.Accepted == true) drop.Value.Send(_port);
            return;
        }
        // A set goes only to a LegACEy window that takes sets (the Vault): never to retail or the ground.
        if (carried.Count > 1) _dragHost?.DeliverManyAtPointer(carried);
        else HandOffOutside(dragged);
    }

    /// <summary>
    /// A release outside every slot: an item goes to the LegACEy window under the pointer, if that window takes it (a Vault deposit).
    /// Packs are never given to a LegACEy window, and the main pack is the character, so neither ever goes to the ground.
    /// </summary>
    private void HandOffOutside(uint dragged)
    {
        var snapshot = _port.Snapshot;
        if (_dragHost == null || !snapshot.Contains(dragged) || snapshot.MainPack.Id == dragged) return;
        if (PackOf(snapshot, dragged) == null && _dragHost.DeliverAtPointer(dragged, KindOf(snapshot, dragged).Name)) return;
        // Retail did not take the drag when it left the window (the retail panel shows no element for the item): only a release over
        // the world sends anything.
        if (_dragHost.DropTargetAtPointer() != ItemDropTarget.World) return;
        if (PackOf(snapshot, dragged) != null) _packsOnGround.Add(dragged);
        _port.DropOnGround(dragged);
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
                HideIndicator();
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
            HideIndicator();
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
        HideIndicator();
        if (_disposed || itemId == 0) return false;
        var snapshot = _port.Snapshot;
        var external = !snapshot.Contains(itemId);
        var target = DropAt(position, itemId)?.Target;
        if (target == null && external) target = new InventorySlotId(SlotPlace.Pack, 0, OpenPack(snapshot).Id, -1, null);
        if (target == null) return false;
        var pack = _packsOnGround.Contains(itemId);
        var drop = pack ? PackOnto(snapshot, itemId, target) : Judge(_port, itemId, target, external);
        if (drop?.Accepted != true) return false;
        _packsOnGround.Remove(itemId);
        drop.Value.Send(_port);
        return true;
    }

    /// <summary>A click on release: a pack or a focus opens; an item is selected, or used when the press was the second click on it.</summary>
    private void Click(InventorySlotId slot, int clicks)
    {
        if (slot.Place == SlotPlace.Pack)
        {
            // A focus opens too, as in retail: it holds nothing, so the grid shows no cells under "Contents of" its name.
            if (slot.Container != 0)
            {
                _picked = slot.Container;
                Render(_port.Snapshot);
            }
            // The main pack is the character: a click selects them, or makes them the target of an item in use (a healing kit). A side
            // pack or a focus is an item, so a click selects it. Each clears a picked set, as a plain click on an item does.
            if (TargetOf(slot) is var target and not 0)
            {
                _selection.Clear();
                ShowSelection();
                _port.Select(target);
            }
            return;
        }
        if (slot.ItemId == 0) return;
        var ctrl = (_pressModifiers & KeyModifiers.Control) != 0;
        var shift = (_pressModifiers & KeyModifiers.Shift) != 0;
        if (clicks >= 2 && !ctrl && !shift)
        {
            _port.Use(slot.ItemId);
            return;
        }
        if (slot.Place == SlotPlace.Cell) _selection.Press(slot.ItemId, OpenOrder(_port.Snapshot), ctrl, shift);
        else _selection.Clear();
        ShowSelection();
        // A Ctrl or Shift click only picks; retail's selection follows plain clicks.
        if (!ctrl && !shift || slot.Place != SlotPlace.Cell) _port.Select(slot.ItemId);
    }

    private void EndDrag()
    {
        Fade(_dragged, lifted: false);
        var wasSet = _carried.Count > 1;
        _carried = Array.Empty<uint>();
        if (wasSet) ShowSelection();
        _press = null;
        _dragged = 0;
        _dragIcon?.Dispose();
        _dragIcon = null;
        _indicatorAt = null;
        HideIndicator();
    }

    /// <summary>
    /// Shows the picked set on the open pack's cells. While two or more items are picked, the picked ones are outlined and the others fade, as in
    /// the Vault; a lifted set is outlined as carried. Otherwise a cell is outlined when it is retail's selection.
    /// </summary>
    private void ShowSelection()
    {
        var set = _selection.Count > 1;
        var selected = _port.Snapshot.Selected;
        foreach (var slot in _grid.Cells.OfType<DerethSlot>())
            if (slot.Tag is InventorySlotId { Place: SlotPlace.Cell, ItemId: not 0 } tag)
            {
                var picked = _selection.Contains(tag.ItemId);
                slot.Selected = set ? picked : tag.ItemId == selected;
                slot.Carried = set && picked && _carried.Count > 1;
                slot.Opacity = set && !picked ? LiftedOpacity : 1;
            }
    }

    /// <summary>Shows the indicator over the slot under the pointer: gold when the drop is accepted, red when it is refused.</summary>
    private void ShowIndicator(Point point) => ShowIndicator(point, _dragged, external: false);

    /// <summary>The drop indicator for a dragged item over a point: the slot under it, when the drop there is judged.</summary>
    private void ShowIndicator(Point point, uint dragged, bool external)
    {
        _indicatorAt = (point, dragged, external);
        var set = _carried.Count > 1 && !external;
        var at = set ? SlotAt(point) is { Tag: InventorySlotId tag } slot ? (slot, tag) : default((Control Shown, InventorySlotId Target)?) : DropAt(point, dragged);
        var drop = at is not { } place ? null : set ? JudgeSet(_port.Snapshot, _carried, place.Target) : Judge(_port, dragged, place.Target, external);
        if (at == null || drop == null)
        {
            HideIndicator();
            OutlineLifted(dragged);
            return;
        }
        // Over the doll the body parts the item would cover are outlined, rather than a frame drawn round the doll.
        if (at.Value.Shown == DollArea && DollShown && at.Value.Target.Equipment is { } wornAt)
        {
            HideIndicator();
            // A worn item lifted over its own doll keeps its lift outline: dropping it there puts nothing new on.
            if (_port.Snapshot.Wielded.Any(item => item.Id == dragged))
            {
                OutlineLifted(dragged);
                return;
            }
            var covered = InventorySnapshotBuilder.PartsCovered(_port.CoverageOf(dragged));
            var mask = _port.WieldMask(dragged, wornAt);
            var parts = covered.Count > 0 ? covered : InventorySnapshotBuilder.PartsUnder(mask != 0 ? mask : _port.Snapshot.ValidLocationsOf(dragged) ?? 0);
            // A weapon or a ring covers no body part, so the doll is framed instead.
            if (parts.Count > 0)
            {
                _doll!.Outline(parts, drop.Value.Accepted ? GoldOutline : InvalidOutline);
                return;
            }
        }
        Light(at.Value.Shown, drop.Value.Accepted ? GoldBrush : InvalidBrush);
        OutlineLifted(dragged);
    }

    /// <summary>
    /// Where one item dropped at the point goes, and what lights up for it: the slot under the point, or, over the doll, the paperdoll
    /// slot the item is worn in (an empty one first, else the first it fits), and the doll lights. Null where nothing takes a drop.
    /// </summary>
    private (Control Shown, InventorySlotId Target)? DropAt(Point point, uint item)
    {
        if (SlotAt(point) is { Tag: InventorySlotId tag } under) return (under, tag);
        if (!Over(DollArea, point)) return null;
        var fits = Enum.GetValues(typeof(PaperdollSlot)).Cast<PaperdollSlot>().Where(slot => _port.WieldMask(item, slot) != 0).ToList();
        if (fits.Count == 0) return null;
        var worn = new HashSet<PaperdollSlot>(_port.Snapshot.Wielded.SelectMany(wielded => wielded.Slots));
        var into = fits.Where(slot => !worn.Contains(slot)).DefaultIfEmpty(fits[0]).First();
        var shown = _paperdoll.GetVisualDescendants().OfType<DerethSlot>()
            .FirstOrDefault(slot => slot.Tag is InventorySlotId { Place: SlotPlace.Paperdoll } id && id.Equipment == into);
        return (DollArea, shown?.Tag as InventorySlotId ?? new InventorySlotId(SlotPlace.Paperdoll, 0, 0, -1, into));
    }

    private bool Over(Control control, Point point) =>
        control.IsEffectivelyVisible && control.TranslatePoint(default, this) is { } origin && new Rect(origin, control.Bounds.Size).Contains(point);

    private static readonly uint GoldOutline = DerethPalette.Gold.ToUInt32();
    private static readonly uint InvalidOutline = DerethPalette.Invalid.ToUInt32();
    private static readonly uint LiftedOutline = DerethPalette.Teal.ToUInt32();

    private bool DollShown => _doll != null && DollArea.Child == _doll;

    /// <summary>Hides the drop indicator and the doll's outline.</summary>
    private void HideIndicator()
    {
        _dropIndicator.IsVisible = false;
        _doll?.Outline(Array.Empty<int>(), 0);
    }

    /// <summary>
    /// While a worn item is lifted, the body parts it covers are outlined on the doll: the ones the clothing tables say it draws, else
    /// those its coverage and wield slots name. Anything else leaves the doll as it is.
    /// </summary>
    private void OutlineLifted(uint dragged)
    {
        var wielded = _port.Snapshot.Wielded;
        var index = wielded.ToList().FindIndex(item => item.Id == dragged);
        if (!DollShown || index < 0) return;
        var parts = DrawnParts(wielded)[index];
        _doll!.Outline(parts.Count > 0 ? parts : ClaimOf(wielded[index]), LiftedOutline);
    }

    /// <summary>Shows the drop indicator over the slot, in the colour.</summary>
    private void Light(Control slot, IBrush brush)
    {
        if (slot.TranslatePoint(default, this) is not { } origin)
        {
            HideIndicator();
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
        // A lifted set stays bright, outlined as carried, as the render draws it.
        if (id == 0 || _carried.Count > 1) return;
        foreach (var slot in this.GetVisualDescendants().OfType<DerethSlot>())
            if (slot.Tag is InventorySlotId tag && DraggedId(tag) == id)
            {
                slot.Opacity = lifted ? LiftedOpacity : 1;
                slot.Selected = lifted || (tag.Place == SlotPlace.Pack ? PackOutlined(_port.Snapshot, tag.Container, OpenPack(_port.Snapshot)) : id == _port.Snapshot.Selected);
            }
    }

    /// <summary>Tells the port which item the pointer is over, once per change, so retail's target cursor can judge it.</summary>
    private void Hover(uint itemId)
    {
        if (itemId == _hovered) return;
        _hovered = itemId;
        _port.Hover(itemId);
    }

    /// <summary>The slot under a point of this window, or null over anything the window does not draw as a slot.</summary>
    private DerethSlot? SlotAt(Point point)
    {
        for (var visual = this.GetVisualAt(point); visual != null; visual = visual.GetVisualParent())
            if (visual is DerethSlot { Tag: InventorySlotId } slot) return slot;
        return null;
    }

    /// <summary>What a press at a point acts on: the slot under it, or, on the doll, the worn item over the body part there.</summary>
    private InventorySlotId? PressAt(Point point) => SlotAt(point)?.Tag as InventorySlotId ?? WornAt(point);

    /// <summary>
    /// The worn item drawn under a point of the doll: the outermost the clothing tables say draws that body part (tall boots up to the
    /// knee), or, where they can't tell, the outermost worn there by where it is worn (a coat over the shirt beneath it). Null off the
    /// doll, or where that part wears nothing.
    /// </summary>
    private InventorySlotId? WornAt(Point point)
    {
        if (_doll == null || DollArea.Child != _doll || !Over(_doll, point) || this.TranslatePoint(point, _doll) is not { } at) return null;
        var wielded = _port.Snapshot.Wielded;
        var part = _doll.PartAt(at);
        var drawn = DrawnParts(wielded);
        var worn = wielded.Where((_, i) => drawn[i].Contains(part)).OrderBy(Layer).LastOrDefault() ?? InventorySnapshotBuilder.WornOn(wielded, part);
        return worn == null ? null : new InventorySlotId(SlotPlace.Paperdoll, worn.Id, 0, -1, worn.Slots.Count > 0 ? worn.Slots[0] : null);
    }

    /// <summary>The body parts each worn item draws on the doll, in the order of <paramref name="wielded"/>; empty where the doll can't tell.</summary>
    private IReadOnlyList<IReadOnlyCollection<int>> DrawnParts(IReadOnlyList<WieldedItem> wielded)
    {
        // PartsDrawn takes the items in the order the look draws them, so a coat claiming all a shirt does wins the parts it covers.
        var order = Enumerable.Range(0, wielded.Count).OrderBy(i => Layer(wielded[i])).ToList();
        var drawn = _doll!.PartsDrawn(order.Select(i => ClaimOf(wielded[i])).ToList());
        var parts = new IReadOnlyCollection<int>[wielded.Count];
        for (var i = 0; i < order.Count; i++) parts[order[i]] = drawn[i];
        return parts;
    }

    /// <summary>
    /// Where a worn item comes in the look, as ACE builds it: clothing first, then armour and what is worn on the head, hands and feet
    /// over it, each by its coverage. Sorting by it puts the items under to over.
    /// </summary>
    private (bool Over, uint Coverage) Layer(WieldedItem item) => ((item.EquippedMask & ArmourAndExtremities) != 0, _port.CoverageOf(item.Id));

    // EquipMask's armour (chest to lower legs) and extremities (head, hands, feet).
    private const uint ArmourAndExtremities = 0x7E00 | 0x121;

    /// <summary>The body parts a worn item says it covers: its coverage, and where it is worn.</summary>
    private IReadOnlyCollection<int> ClaimOf(WieldedItem item) =>
        InventorySnapshotBuilder.PartsCovered(_port.CoverageOf(item.Id)).Union(InventorySnapshotBuilder.PartsUnder(item.EquippedMask)).ToList();

    /// <summary>
    /// What a press on a slot carries: its item, a side pack, or the main pack, which is the character and only goes to retail (a
    /// shortcut on the bar). Zero for an empty slot.
    /// </summary>
    private uint DraggedId(InventorySlotId slot)
    {
        if (slot.Place != SlotPlace.Pack) return slot.ItemId;
        var s = _port.Snapshot;
        return slot.Container != 0 && (slot.Container == s.MainPack.Id || s.SidePacks.Any(pack => pack.Id == slot.Container)) ? slot.Container : 0;
    }

    /// <summary>The object a slot stands for when it is selected or targeted: its item, or a pack (the main pack is the character).</summary>
    private static uint TargetOf(InventorySlotId slot) => slot.Place != SlotPlace.Pack ? slot.ItemId : slot.Container;

    /// <summary>A pack tile is outlined while its pack is open; a focus that is retail's selection takes the outline from it.</summary>
    private static bool PackOutlined(InventorySnapshot s, uint id, InventoryPack open) => PackOf(s, s.Selected)?.Capacity == 0 ? id == s.Selected : id == open.Id;

    private GameImage? DragImage(uint id)
    {
        var snapshot = _port.Snapshot;
        var visual = snapshot.Items.FirstOrDefault(item => item.Id == id)?.Visual ?? snapshot.Wielded.FirstOrDefault(item => item.Id == id)?.Visual
            ?? PackOf(snapshot, id)?.Visual ?? new ItemVisual(0, 0, 0, 0);
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
        if (!external && !s.Contains(dragged) || dragged == s.MainPack.Id) return null;
        if (s.SidePacks.Any(pack => pack.Id == dragged)) return PackOnto(s, dragged, target);
        switch (target.Place)
        {
            case SlotPlace.Paperdoll:
            {
                if (target.ItemId == dragged || target.Equipment is not { } slot) return null;
                // The port's mask covers an item from another window too (it reads the item's locations from the world).
                var mask = port.WieldMask(dragged, slot);
                if (mask == 0) return Refused;
                // A weapon or shield takes the place of what is in its slot: retail's paperdoll puts that back in the main pack before
                // it wields, since the server refuses a wield onto a held location. The weapon slot spans several locations, so a bow
                // dropped there takes the sword's place though their locations differ. Armour and clothing don't swap: the worn piece
                // has to come off first, and the server refuses the wield.
                var swaps = slot is PaperdollSlot.Weapon or PaperdollSlot.Shield;
                var held = s.Wielded.Where(item => swaps && item.Id != dragged && (item.Slots.Contains(slot) || (item.EquippedMask & mask) != 0))
                    .Select(item => item.Id).ToList();
                var free = s.FreeSlots(s.MainPack).Take(held.Count).ToList();
                if (free.Count < held.Count) return Refused;
                return (true, p =>
                {
                    for (var index = 0; index < held.Count; index++) p.MoveToContainer(held[index], s.MainPack.Id, free[index]);
                    p.Wield(dragged, slot);
                });
            }

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
    /// What dropping a set does: into a pack's cell or onto its tile, the items move there in turn, in order, from that cell or the pack's first
    /// free slot. A pack without room for the items not already in it is red, as is the paperdoll. Onto one of the set's own cells: nothing.
    /// </summary>
    private static (bool Accepted, Action<IInventoryPort> Send)? JudgeSet(InventorySnapshot s, IReadOnlyList<uint> carried, InventorySlotId target)
    {
        if (target.Place == SlotPlace.Paperdoll || PackOf(s, target.Container) is not { } into) return Refused;
        if (target.ItemId != 0 && carried.Contains(target.ItemId)) return null;
        var arriving = carried.Count(id => s.Items.FirstOrDefault(item => item.Id == id)?.Container != into.Id);
        if (target.Place == SlotPlace.Pack && arriving == 0) return null;
        if (ItemsIn(s, into.Id) + arriving > into.Capacity) return Refused;
        var start = target.Place == SlotPlace.Cell ? target.SlotIndex : FirstFree(s, into);
        return (true, p =>
        {
            for (var index = 0; index < carried.Count; index++) p.MoveToContainer(carried[index], into.Id, start + index);
        });
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
    private static int FirstFree(InventorySnapshot s, InventoryPack pack) => s.FreeSlots(pack).DefaultIfEmpty(-1).First();

    /// <summary>The name, stack maximum and weenie class id (WCID) of an item the snapshot holds, carried or worn.</summary>
    private static (string Name, int StackMax, int Wcid) KindOf(InventorySnapshot s, uint id)
    {
        var item = s.Items.FirstOrDefault(candidate => candidate.Id == id);
        if (item != null) return (item.Name, item.StackMax, item.Wcid);
        var worn = s.Wielded.FirstOrDefault(candidate => candidate.Id == id);
        return worn != null ? (worn.Name, worn.StackMax, worn.Wcid) : (string.Empty, 0, 0);
    }

    private void Render(InventorySnapshot snapshot)
    {
        if (_disposed) return;
        // The slots are rebuilt, so a drop indicator on the old ones goes; a drag in progress gets it back once they are laid out.
        HideIndicator();
        var open = OpenPack(snapshot);
        _selection.Keep(OpenOrder(snapshot));
        var worn = new Dictionary<PaperdollSlot, WieldedItem>();
        foreach (var item in snapshot.Wielded)
            foreach (var slot in item.Slots)
                worn[slot] = item;
        _paperdoll.Show(slot => WornSlot(snapshot, worn, slot), _showSlots);
        UpdateDoll();
        RenderPacks(snapshot, open);
        RenderContents(snapshot, open);
        ShowSelection();
        RenderBurden(snapshot);
        _frame.Title = snapshot.CharacterName.Length == 0 ? Title : Title + " of " + snapshot.CharacterName;
        _frame.ShortTitle = snapshot.CharacterName.Length == 0 ? null : snapshot.CharacterName;
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

    /// <summary>The open pack's items in slot order, the order a Shift click picks a range in.</summary>
    private IReadOnlyList<uint> OpenOrder(InventorySnapshot snapshot)
    {
        var open = OpenPack(snapshot).Id;
        return snapshot.Items.Where(item => item.Container == open).OrderBy(item => item.Slot).Select(item => item.Id).ToList();
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
        _packList.Children.Add(PackColumnSlot(snapshot, snapshot.MainPack, -1, open));
        for (var position = 0; position < snapshot.SidePacks.Count; position++)
            _packList.Children.Add(PackColumnSlot(snapshot, snapshot.SidePacks[position], position, open));
    }

    /// <summary>A pack in the column: its icon, with an upright fill bar inside its right edge, as retail's. An empty side-pack slot is a placeholder.</summary>
    private Control PackColumnSlot(InventorySnapshot snapshot, InventoryPack pack, int position, InventoryPack open)
    {
        var count = pack.Id == 0 ? 0 : ItemsIn(snapshot, pack.Id);
        var tile = pack.Id == 0
            ? Empty(new InventorySlotId(SlotPlace.Pack, 0, 0, position, null))
            : Slot(new InventorySlotId(SlotPlace.Pack, 0, pack.Id, position, null), Icon(pack.Visual), PackOutlined(snapshot, pack.Id, open));
        // Only a container holding something shows its fill. The bar takes no presses, so the tile under it still does.
        if (pack.Capacity <= 0 || count <= 0) return tile;
        var bar = FillBar(count, pack.Capacity);
        bar.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Margin = new Thickness(0, 0, FillInset, 0);
        bar.IsHitTestVisible = false;
        return new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { tile, bar } };
    }

    private void RenderContents(InventorySnapshot snapshot, InventoryPack open)
    {
        var items = new Dictionary<int, InventoryItem>();
        foreach (var item in snapshot.Items)
            if (item.Container == open.Id) items[item.Slot] = item;
        _contentsName.FullText = "Contents of " + open.Name;
        // A focus has no slots, so it has no count, as in retail.
        _contentsCount.Text = open.Capacity == 0 ? string.Empty : $"{ItemsIn(snapshot, open.Id)} / {open.Capacity}";
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
        // Retail stops the reading at 300%, the most a character can carry.
        var percent = snapshot.BurdenLimit > 0 ? Math.Min(300, snapshot.Burden * 100 / snapshot.BurdenLimit) : 0;
        _burdenMeter.Content = BurdenMeter(snapshot);
        _burdenText.Text = $"{percent}%";
    }

    private static int ItemsIn(InventorySnapshot snapshot, uint container) => snapshot.Items.Count(item => item.Container == container);

    // The bar sits this far in from the tile's right edge, inside its rim.
    private const double FillWidth = 3, FillInset = 3;

    /// <summary>A pack's fill: a narrow upright groove, filled in gold from the foot to the share of its slots in use.</summary>
    private static Control FillBar(int count, int capacity)
    {
        var share = capacity <= 0 ? 0 : Math.Min(1.0, (double)count / capacity);
        var track = new Border
        {
            Width = FillWidth, Height = 30, Background = DerethPalette.GrooveBrush, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(1),
        };
        track.Child = new Border { Height = 30 * share, VerticalAlignment = VerticalAlignment.Bottom, Background = GoldBrush, CornerRadius = new CornerRadius(1) };
        return track;
    }

    /// <summary>
    /// A groove filled to the burden, full at the most a character can carry (three times the limit). As retail's, each third
    /// keeps its band's colour: green to the limit, yellow above it, red above twice it.
    /// </summary>
    private static Control BurdenMeter(InventorySnapshot snapshot)
    {
        const double Height = 62, Inside = Height - 4, LeastBand = 3;
        var load = snapshot.BurdenLimit <= 0 ? 0 : (double)snapshot.Burden / snapshot.BurdenLimit;
        var bands = BurdenBandBrushes.Select((_, band) => Math.Max(0, Math.Min(1, load - band)) * Inside / 3).ToArray();
        // The band the load is in shows at least a sliver, so a load just past the limit already shows yellow (and past twice it, red).
        var top = Array.FindLastIndex(bands, height => height > 0);
        if (top > 0) bands[top] = Math.Max(bands[top], LeastBand);
        var track = new Border
        {
            Background = DerethPalette.GrooveBrush, BorderBrush = DerethPalette.GrooveEdgeBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), Padding = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Width = 12, Height = Height,
        };
        // The bands run from the foot in order; the empty share sits at the top.
        var rows = new[] { "*" }.Concat(Enumerable.Reverse(bands).Select(height => height.ToString("0.###", CultureInfo.InvariantCulture)));
        var holder = new Grid { RowDefinitions = new RowDefinitions(string.Join(",", rows)) };
        for (var band = 0; band < bands.Length; band++)
        {
            var fill = new Border { Background = BurdenBandBrushes[band] };
            Grid.SetRow(fill, bands.Length - band);
            holder.Children.Add(fill);
        }
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

    private Control Body()
    {
        // The pack's six columns sit under the paperdoll's left column, with the contents line across their width.
        _grid.Columns = RetailColumns;
        // The paperdoll insets each cell 1 px in its pitch, so the pack's columns do too, and the two sections line up.
        var pack = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 0, 0, 0) };
        pack.Children.Add(At(ContentsLine(), 0));
        pack.Children.Add(At(_grid, 1));

        // The pack column stands on the right, as retail's does, the window's full height: burden at its head, then the main pack and
        // the side packs, which scroll.
        var packs = new ScrollViewer
        {
            Content = _packList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var column = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(6, 0, 0, 0) };
        column.Children.Add(At(Centred(_burdenMeter), 0));
        column.Children.Add(At(Centred(_burdenText), 1));
        column.Children.Add(At(packs, 2));

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(2, 8, 2, 6)
        };
        body.Children.Add(At(_paperdoll, 0));
        body.Children.Add(At(new DerethRule { Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 }, 1));
        body.Children.Add(At(pack, 2));
        body.Children.Add(At(column, 0, 1));
        Grid.SetRowSpan(column, 3);
        return body;
    }

    private Control ContentsLine()
    {
        // The count hugs the right edge, its margin a gap from a long pack name, which shrinks rather than running into it (Backlog 107).
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 6, 0, 6) };
        line.Children.Add(_contentsName);
        line.Children.Add(At(_contentsCount, 0, 1));
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
