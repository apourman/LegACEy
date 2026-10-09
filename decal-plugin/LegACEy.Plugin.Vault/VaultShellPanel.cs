using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Vault;

/// <summary>
/// The account Vault's contents, drawn with the Dereth parts. With a <see cref="VaultClient"/> it shows the live Vault from
/// the LegACEy server channel and moves items by drag: deposit, withdraw and reorder. Without one it shows a sample Vault
/// with icons read from the player's DAT.
/// </summary>
public sealed class VaultShellPanel : UserControl, IDisposable, IRetailItemDropTarget
{
    public const int WindowWidth = 546;
    public const int WindowHeight = 689;
    /// <summary>The header's chest icon, from the DAT.</summary>
    public const uint ChestIconId = 0x06001020;
    // A whole page of slots, so the grid looks the same however many items the page holds.
    private const int MinimumCells = VaultProtocol.PageSize;
    private const int SampleCount = 317;
    private const double DragThreshold = 4;
    /// <summary>How far the cells the selection doesn't hold fade while two or more are selected, or while one is lifted.</summary>
    private const double DimmedOpacity = 0.4;
    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush;
    private static readonly IBrush TextBrush = DerethPalette.TextBrush;
    private static readonly IBrush MutedBrush = DerethPalette.MutedBrush;
    private static readonly IBrush Invalid = DerethPalette.InvalidBrush;
    private static readonly IBrush ShadowBrush = DerethPalette.Brush(Colors.Black);
    private static readonly IBrush SelectedBrush = DerethPalette.Brush(DerethPalette.TealText);
    private static readonly IBrush ValidFill = DerethPalette.Brush(Color.FromArgb(0x40, DerethPalette.Gold.R, DerethPalette.Gold.G, DerethPalette.Gold.B));
    private static readonly IBrush InvalidFill = DerethPalette.Brush(Color.FromArgb(0x40, DerethPalette.Invalid.R, DerethPalette.Invalid.G, DerethPalette.Invalid.B));

    // icon, UiEffects (the sample's outline: BoostMana, BoostStamina, Frost, Magical, Lightning, Fire, Poisoned, as the prototype draws them), count
    private static readonly (string Name, uint Icon, int Effects, int Count)[] Samples =
    {
        ("Chainmail shirt", 0x06000FC7, 0, 1),
        ("Leather boots", 0x06000FAD, 0x80, 1),
        ("Gold ring", 0x06000FB5, 0x1, 1),
        ("Pendant", 0x06000FBE, 0x40, 1),
        ("Steel shield", 0x06000FCB, 0x20, 1),
        ("Leather cap", 0x06000FAA, 0, 1),
        ("Blue potion", 0x06001012, 0x8, 12),
        ("Yellow potion", 0x06001013, 0x10, 3),
        ("Treasure chest", ChestIconId, 0, 1),
        ("Small pouch", 0x06001031, 0, 25),
        ("Green bottle", 0x06001030, 0x2, 8),
        ("Silver goblet", 0x0600101F, 0x1, 1)
    };

    private readonly Dictionary<uint, WriteableBitmap?> _images = new();
    // an item's icon as drawn, by the fields that decide it
    private readonly Dictionary<(uint Underlay, uint Icon, uint Overlay, uint OverlaySecondary, uint UiEffects), WriteableBitmap?> _itemImages = new();
    private readonly IGameArtSource _art;
    private readonly VaultClient? _client;
    private readonly VaultSnapshot? _sample;
    private readonly IItemDragHost? _dragHost;
    private readonly TextBlock _itemsLabel = Label(string.Empty, MutedBrush, 13);
    private readonly TextBlock _items = Label(string.Empty, TextBrush, 13);
    private readonly TextBlock _balance = Label(string.Empty, GoldBrush, 13);
    private readonly TextBlock _pagerText = Label(string.Empty, MutedBrush, 12);
    private readonly DerethSearchField _search = new("Search vault…") { Margin = new Thickness(2, 0, 2, 10) };
    private readonly DerethPagerButton _previous = new(DerethSpriteArt.PagerPrevious) { Width = 34, Height = 32, IsEnabled = false };
    private readonly DerethPagerButton _next = new(DerethSpriteArt.PagerNext) { Width = 34, Height = 32, IsEnabled = false };
    // The message line above the pager: wraps rather than trims, so a long refusal reason stays whole.
    private readonly TextBlock _status = new()
    {
        FontSize = 12, FontFamily = DerethPalette.Body, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        Margin = new Thickness(4, 6, 4, 0), IsVisible = false
    };
    private readonly DerethSlotGrid _grid = new();
    private readonly List<DerethSlot> _liveSlots = new();
    private readonly List<Border> _dropIndicators = new();
    // The header line: "Items: n / capacity" and the balance, or "N selected" with the selection's buttons instead.
    private readonly StackPanel _itemsLine = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly TextBlock _selectedLabel = Label(string.Empty, SelectedBrush, 13);
    private readonly TextBlock _withdrawText = Label(string.Empty, GoldBrush, 12);
    private readonly DerethButton _withdrawSelection = new() { Height = 24 };
    private readonly DerethButton _clearSelection = new() { Height = 24, Content = Label("Clear", MutedBrush, 12) };
    // The selection's line takes the header line: the label on the left, its buttons on the right.
    private readonly Grid _selectionLine = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false };
    private readonly StackPanel _selectionButtons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    // The selection: the live Vault's when there is one, so the sample Vault selects too.
    private readonly VaultSelection _selection;
    // How the press on a cell was made, so its click selects by the same modifiers.
    private KeyModifiers _pressModifiers;
    private bool _disposed;
    // a retail item being dragged over the window, and the cell it would land in
    private uint _retailItem;
    private string _retailName = string.Empty;
    private bool _retailOver;
    private int _dropCell = -1;
    // an item being dragged out of the window to withdraw it, and the items the drag carries: the selection when the item is selected
    private VaultItemView? _pressItem;
    private Point _pressPoint;
    private VaultItemView? _dragItem;
    private IReadOnlyList<uint> _dragGuids = Array.Empty<uint>();
    private IDisposable? _dragIcon;

    /// <param name="client">The live Vault; the panel owns it and disposes it. Null shows the sample Vault.</param>
    /// <param name="dragHost">Lets items be dragged out of the window onto the retail inventory to withdraw them.</param>
    public VaultShellPanel(IGameArtSource art, VaultClient? client = null, IItemDragHost? dragHost = null)
    {
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _client = client;
        _dragHost = dragHost;
        _selection = client?.Selection ?? new VaultSelection();
        _itemsLine.Children.Add(_itemsLabel);
        _itemsLine.Children.Add(_items);
        _withdrawSelection.Content = _withdrawText;
        _selectionButtons.Children.Add(_withdrawSelection);
        _selectionButtons.Children.Add(_clearSelection);
        _selectionLine.Children.Add(_selectedLabel);
        Grid.SetColumn(_selectionButtons, 1);
        _selectionLine.Children.Add(_selectionButtons);
        _sample = client == null ? SampleSnapshot() : null;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 10, 2, 8) };
        summary.Children.Add(_itemsLine);
        Grid.SetColumn(_balance, 1);
        summary.Children.Add(_balance);
        // The selection's line takes the whole header line.
        Grid.SetColumnSpan(_selectionLine, 2);
        summary.Children.Add(_selectionLine);
        // Sample mode has no server to withdraw from: its button stays disabled.
        _withdrawSelection.IsEnabled = _client != null;
        _withdrawSelection.Click += (_, _) => _client?.WithdrawMany(_client.SelectedGuids());
        _clearSelection.Click += (_, _) =>
        {
            _selection.Clear();
            ShowSelection();
        };
        Grid.SetRow(_search, 1);
        _search.TextChanged += (_, _) => _client?.SetSearch(_search.Text);
        Grid.SetRow(_grid, 2);
        var pager = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(4, 10, 4, 0) };
        _previous.Click += (_, _) => _client?.PreviousPage();
        pager.Children.Add(_previous);
        _pagerText.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(_pagerText, 1);
        pager.Children.Add(_pagerText);
        _next.Click += (_, _) => _client?.NextPage();
        Grid.SetColumn(_next, 2);
        pager.Children.Add(_next);
        Grid.SetRow(_status, 3);
        Grid.SetRow(pager, 4);

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        content.Children.Add(summary);
        content.Children.Add(_search);
        content.Children.Add(_grid);
        content.Children.Add(_status);
        content.Children.Add(pager);
        Content = content;

        if (_client == null) Render();
        else
        {
            _client.Changed += OnClientChanged;
            _client.DepositCheckChanged += OnDepositCheckChanged;
            Render();
            _client.Start();
        }
    }

    /// <summary>The header's chest icon, drawn from the DAT.</summary>
    internal Control HeaderIcon() => Icon(Bitmap(ChestIconId));

    private static VaultSnapshot SampleSnapshot()
    {
        var items = new List<VaultItemView>(SampleCount);
        for (var index = 0; index < SampleCount; index++)
        {
            var sample = Samples[(index * 7) % Samples.Length];
            items.Add(new VaultItemView((uint)index + 1, sample.Name, 0, sample.Count, 0, "held", string.Empty,
                DateTimeOffset.FromUnixTimeSeconds(0), 0, 0, sample.Icon, 0, 0, sample.Effects));
        }
        return new VaultSnapshot(true, 245, 1000, SampleCount, SampleCount, items);
    }

    private VaultSnapshot? Snapshot => _client == null ? _sample : _client.Snapshot;

    private void OnClientChanged(object? sender, EventArgs e)
    {
        if (!_disposed) Render();
    }

    private void Render()
    {
        var snapshot = Snapshot;
        var items = snapshot?.Items ?? Array.Empty<VaultItemView>();
        var available = snapshot is { Available: true };
        if (available)
        {
            _itemsLabel.Text = "Items:";
            _items.Text = $"{snapshot!.VaultCount:N0} / {snapshot.Capacity:N0}";
            _balance.Text = snapshot.HasBalance ? $"{snapshot.Balance:N0} MMD" : string.Empty;
        }
        else
        {
            _itemsLabel.Text = _client?.Connection == VaultConnection.Connecting ? "Connecting to the server…" : "Vault unavailable";
            _items.Text = string.Empty;
        }

        _liveSlots.Clear();
        _dropIndicators.Clear();
        _grid.Cells.Clear();
        if (available)
        {
            // One cell more than the items, so there is always an empty slot to drop into.
            var cells = Math.Max(MinimumCells, items.Count + 1);
            for (var index = 0; index < cells; index++)
            {
                var item = index < items.Count ? items[index] : null;
                var cell = new DerethSlot(item == null ? null : Icon(ItemBitmap(item)));
                if (item != null)
                {
                    ToolTip.SetTip(cell, Describe(item));
                    if (item.StackSize > 1) cell.Children.Add(StackCount(item.StackSize));
                    ConnectCell(cell, item);
                }
                // Shown on the cell a dragged item would be dropped into.
                var indicator = new Border
                {
                    Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2), IsHitTestVisible = false
                };
                cell.Children.Add(indicator);
                _dropIndicators.Add(indicator);
                _liveSlots.Add(cell);
                _grid.Cells.Add(cell);
            }
            ShowDropIndicator(_dropCell);
        }

        _pagerText.Text = available ? PagerText(_client?.Offset ?? 0, items.Count, snapshot!.Total) : string.Empty;
        _previous.IsEnabled = available && _client?.CanPageBack == true;
        _next.IsEnabled = available && _client?.CanPageForward == true;
        ShowSelection();
        ShowStatus();
    }

    /// <summary>
    /// Shows the selection on the cells and in the header line. Two or more selected make the line "N selected" with its buttons
    /// and fade the cells that aren't selected; a lifted item's cell fades for the drag.
    /// </summary>
    private void ShowSelection()
    {
        var multi = _selection.Count >= 2;
        var lifted = _dragItem == null ? -1 : IndexOf(_dragItem.Guid);
        for (var index = 0; index < _liveSlots.Count; index++)
        {
            var selected = _selection.Contains(index);
            var slot = _liveSlots[index];
            slot.Selected = selected;
            slot.Opacity = index == lifted || (multi && !selected) ? DimmedOpacity : 1;
        }
        var snapshot = Snapshot;
        _itemsLine.IsVisible = !multi;
        _balance.IsVisible = !multi && snapshot is { Available: true, HasBalance: true };
        _selectionLine.IsVisible = multi;
        _selectedLabel.Text = $"{_selection.Count:N0} selected";
        _withdrawText.Text = $"Withdraw {_selection.Count:N0}";
    }

    /// <summary>"1 – 100 of 317" for the page on screen, or "0 of 0" when nothing matches.</summary>
    private static string PagerText(int offset, int shown, int total) =>
        total == 0 ? "0 of 0" : $"{offset + 1:N0} – {offset + shown:N0} of {total:N0}";

    /// <summary>The message line above the pager: the latest message or drag hint, shown only while there is one.</summary>
    private void ShowStatus()
    {
        var (text, refused) = StatusText();
        _status.Text = text;
        _status.Foreground = refused ? Invalid : GoldBrush;
        _status.IsVisible = text.Length > 0;
    }

    private (string Text, bool Refused) StatusText()
    {
        var client = _client;
        if (client == null) return (string.Empty, false);
        if (_retailOver && client.Connection == VaultConnection.Live)
        {
            var name = _retailName.Length == 0 ? "this item" : _retailName;
            var check = client.DepositCheck(_retailItem);
            return check is { Ok: false } refused
                ? (refused.Message, true)
                : (_dropCell >= 0 ? $"Release to deposit {name}" : $"Drop {name} on a vault cell to deposit it", false);
        }
        if (_dragItem != null && client.Connection == VaultConnection.Live)
        {
            if (_dropCell < 0)
                return _dragGuids.Count > 1
                    ? ($"Drop {_dragGuids.Count:N0} items on your inventory to withdraw them", false)
                    : ($"Drop {_dragItem.Name} on your inventory to withdraw it", false);
            // A filtered page can't be rearranged, so the move would be refused: say so instead of offering it.
            // A selection dragged over a cell moves only the item grabbed: the others stay where they are.
            var only = _dragGuids.Count > 1 ? " (only this item)" : string.Empty;
            return client.Search.Length > 0 ? (VaultClient.SearchBlocksMove, false) : ($"Release to move {_dragItem.Name} here{only}", false);
        }
        return (client.Notice, false);
    }

    private const double Gap = DerethSlotGrid.Pitch - DerethSlotGrid.CellSize;

    /// <summary>The index of the vault cell under a point in this panel's coordinates, or -1.</summary>
    private int CellAt(Point position)
    {
        var viewport = _grid.TranslatePoint(default, this);
        if (viewport == null || !new Rect(viewport.Value, _grid.Bounds.Size).Contains(position)) return -1;
        for (var index = 0; index < _liveSlots.Count; index++)
        {
            var slot = _liveSlots[index];
            // Each cell owns half the gap around it, so a drop between two cells lands on the nearer one.
            var origin = slot.TranslatePoint(default, this);
            if (origin != null && new Rect(origin.Value, slot.Bounds.Size).Inflate(Gap / 2).Contains(position)) return index;
        }
        return -1;
    }

    public void RetailDragOver(uint itemId, string itemName, Point? position)
    {
        if (_client == null || _disposed) return;
        var over = itemId != 0 && position != null;
        // The host reports "no retail drag" every frame; that must not touch the frame of a vault item being lifted.
        if (!over && !_retailOver)
        {
            _retailItem = itemId;
            return;
        }
        var cell = over && _client.Connection == VaultConnection.Live ? CellAt(position!.Value) : -1;
        if (itemId == _retailItem && over == _retailOver && cell == _dropCell) return;
        // A new drag over the window: ask the server whether this item may go in, so the drop can show as invalid.
        if (over && (itemId != _retailItem || !_retailOver) && _client.Connection == VaultConnection.Live) _client.CheckDeposit(itemId);
        _retailItem = itemId;
        _retailName = itemName ?? string.Empty;
        _retailOver = over;
        ShowDropIndicator(cell);
        ShowStatus();
    }

    public bool RetailDrop(uint itemId, string itemName, Point position)
    {
        if (_client == null || _disposed) return false;
        var cell = _client.Connection == VaultConnection.Live ? CellAt(position) : -1;
        var check = _client.DepositCheck(itemId);
        RetailDragOver(0, string.Empty, null);
        if (cell < 0)
        {
            if (_client.Connection == VaultConnection.Live) _client.Tell("Drop the item on a vault cell to deposit it.");
            return false;
        }
        if (check is { Ok: false } refused)
        {
            _client.Tell(refused.Message);
            return false;
        }
        _client.Deposit(itemId);
        return true;
    }

    private void OnDepositCheckChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_retailOver) return;
        ShowDropIndicator(_dropCell);
        ShowStatus();
    }

    /// <summary>Frames the cell an item would land in: gold, or red when the server refuses a dragged retail item.</summary>
    private void ShowDropIndicator(int cell)
    {
        _dropCell = cell;
        var refused = _retailOver && _client?.DepositCheck(_retailItem) is { Ok: false };
        for (var index = 0; index < _dropIndicators.Count; index++)
        {
            var indicator = _dropIndicators[index];
            indicator.IsVisible = index == cell;
            indicator.BorderBrush = refused ? Invalid : GoldBrush;
            indicator.Background = refused ? InvalidFill : ValidFill;
        }
    }

    private void ConnectCell(DerethSlot cell, VaultItemView item)
    {
        // A press selects its item on release, unless it becomes a drag; dragging an item out of the window onto the retail inventory withdraws it.
        cell.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnCellPressed(item, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        cell.AddHandler(InputElement.PointerMovedEvent, (_, e) => OnCellMoved(item, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        cell.AddHandler(InputElement.PointerReleasedEvent, (_, e) => OnCellReleased(item, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        cell.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndWithdrawDrag(), handledEventsToo: true);
    }

    private void OnCellPressed(VaultItemView item, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressItem = item;
        _pressPoint = e.GetPosition(this);
        _pressModifiers = e.KeyModifiers;
    }

    private void OnCellMoved(VaultItemView item, PointerEventArgs e)
    {
        if (_dragItem != null)
        {
            UpdateLiftedHover(e);
            return;
        }
        if (_pressItem != item) return;
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
        // Past the threshold the press is a drag or nothing, so it is never a click, even when no host can take the drag.
        _pressItem = null;
        if (_dragHost == null || _client == null) return;
        if (item.State != "held" || _client.TransferPending)
        {
            _client.Tell(item.State == "held" ? "Wait for the current transfer to finish." : $"{item.Name} is {StateName(item.State).ToLowerInvariant()} and can't be withdrawn.");
            return;
        }
        _dragItem = item;
        // A selected item drags the whole selection, which stays selected; an unselected one drops the selection and drags only itself.
        var selected = _selection.Contains(IndexOf(item.Guid));
        if (!selected) _selection.Clear();
        _dragGuids = selected ? _client.SelectedGuids() : new[] { item.Guid };
        _dragIcon = _dragHost.ShowDragIcon(ItemImage(item), _dragGuids.Count);
        // The lifted item's own cell dims, as the retail inventory ghosts a dragged item.
        ShowSelection();
        UpdateLiftedHover(e);
    }

    private void UpdateLiftedHover(PointerEventArgs e)
    {
        var cell = CellAt(e.GetPosition(this));
        if (cell == _dropCell) return;
        ShowDropIndicator(cell);
        ShowStatus();
    }

    private int IndexOf(uint guid)
    {
        var items = Snapshot?.Items;
        if (items == null) return -1;
        for (var index = 0; index < items.Count; index++)
            if (items[index].Guid == guid) return index;
        return -1;
    }

    /// <summary>E inspects the selected item, when exactly one is selected.</summary>
    public bool GameKeyDown(Key key)
    {
        if (key != Key.E || _client?.Connection != VaultConnection.Live || _selection.Count != 1) return false;
        _client.Inspect(_client.SelectedGuids()[0]);
        return true;
    }

    /// <summary>A right-click on an item selects it alone and inspects it, unless several items are selected.</summary>
    public void RightClick(Point position)
    {
        var cell = CellAt(position);
        var items = Snapshot?.Items;
        if (_client?.Connection != VaultConnection.Live || items == null || cell < 0 || cell >= items.Count || _selection.Count > 1) return;
        if (!_selection.Contains(cell))
        {
            _selection.Press(cell, ctrl: false, shift: false);
            ShowSelection();
        }
        _client.Inspect(items[cell].Guid);
    }

    private void OnCellReleased(VaultItemView pressed, PointerReleasedEventArgs e)
    {
        // A press released on its own item without a drag is a click: it selects by the modifiers held when it went down.
        if (_dragItem == null && _pressItem == pressed)
        {
            _pressItem = null;
            var place = IndexOf(pressed.Guid);
            if (place >= 0)
            {
                _selection.Press(place, (_pressModifiers & KeyModifiers.Control) != 0, (_pressModifiers & KeyModifiers.Shift) != 0);
                ShowSelection();
            }
            return;
        }
        var item = _dragItem;
        var guids = _dragGuids;
        var cell = item == null ? -1 : CellAt(e.GetPosition(this));
        EndWithdrawDrag();
        if (item == null || _client == null || _dragHost == null) return;
        // Released over this window: onto a cell moves the item there; anywhere else puts it back.
        var top = TopLevel.GetTopLevel(this);
        if (top != null && new Rect(top.Bounds.Size).Contains(e.GetPosition(top)))
        {
            if (cell >= 0) _client.Move(item.Guid, cell);
            return;
        }
        switch (_dragHost.DropTargetAtPointer())
        {
            case ItemDropTarget.Inventory:
                _client.WithdrawMany(guids);
                break;
            case ItemDropTarget.InventoryClosed:
                _client.Tell("Open your inventory, then drop the item on it to withdraw it.");
                break;
            default:
                _client.Tell("Drop the item on your inventory to withdraw it.");
                break;
        }
    }

    private void EndWithdrawDrag()
    {
        var wasDragging = _dragItem != null;
        _pressItem = null;
        _dragItem = null;
        _dragGuids = Array.Empty<uint>();
        _dragIcon?.Dispose();
        _dragIcon = null;
        if (!wasDragging || _disposed) return;
        ShowSelection();
        ShowDropIndicator(-1);
        if (_client != null) ShowStatus();
    }

    private static string Describe(VaultItemView item) =>
        $"{item.Name}{(item.StackSize > 1 ? $" ×{item.StackSize:N0}" : string.Empty)} — {StateName(item.State)}, 0x{item.Guid:X8}";

    private static string StateName(string state) => state switch
    {
        "held" => "Stored",
        "listed" => "Listed for sale",
        "withdrawing" => "Withdrawing",
        _ => state
    };

    private static TextBlock Label(string text, IBrush brush, double size) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontFamily = DerethPalette.Body,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A stack count in the cell's corner, with a one-pixel dark shadow.</summary>
    private static Control StackCount(int count)
    {
        var panel = new Panel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 1) };
        var shadow = Label(count.ToString(), ShadowBrush, 11);
        shadow.FontWeight = FontWeight.SemiBold;
        shadow.Margin = new Thickness(1, 1, 0, 0);
        var text = Label(count.ToString(), TextBrush, 11);
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, 0, 1, 1);
        panel.Children.Add(shadow);
        panel.Children.Add(text);
        return panel;
    }

    /// <summary>A picture that isn't an item (the header's chest): its outline is black, as retail draws it.</summary>
    private WriteableBitmap? Bitmap(uint id)
    {
        if (!_images.TryGetValue(id, out var bitmap))
            _images.Add(id, bitmap = GameArtImageExtension.CreateBitmap(ItemIcon.Draw(_art, 0, id, 0, 0, 0)));
        return bitmap;
    }

    /// <summary>An item's icon as drawn: outline from its UI effect, no plate. The grid redraws on every change, so the bitmap is kept per look.</summary>
    private WriteableBitmap? ItemBitmap(VaultItemView item)
    {
        var key = (item.Underlay, item.Icon, item.Overlay, item.OverlaySecondary, unchecked((uint)item.UiEffects));
        if (!_itemImages.TryGetValue(key, out var bitmap))
            _itemImages.Add(key, bitmap = GameArtImageExtension.CreateBitmap(ItemImage(item)));
        return bitmap;
    }

    private GameImage? ItemImage(VaultItemView item) =>
        ItemIcon.Draw(_art, item.Underlay, item.Icon, item.Overlay, item.OverlaySecondary, unchecked((uint)item.UiEffects));

    /// <summary>An item's icon at native size. Missing art shows a question mark.</summary>
    private Grid Icon(WriteableBitmap? bitmap)
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
            var fallback = Label("?", MutedBrush, 12);
            fallback.HorizontalAlignment = HorizontalAlignment.Center;
            layers.Children.Add(fallback);
        }
        return layers;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EndWithdrawDrag();
        if (_client != null)
        {
            _client.Changed -= OnClientChanged;
            _client.DepositCheckChanged -= OnDepositCheckChanged;
            _client.Dispose();
        }
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
        foreach (var image in _itemImages.Values) image?.Dispose();
        _itemImages.Clear();
    }
}

/// <summary>The Vault window: the Dereth frame, its header and close box, around the Vault panel.</summary>
public sealed class VaultShellWindow : UserControl, IRetailItemDropTarget, IGameInputTarget, IDisposable
{
    private readonly VaultShellPanel _panel;
    private bool _disposed;
    public event EventHandler? CloseRequested;

    public VaultShellWindow(VaultShellPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        var window = new DerethWindow("Vault", panel.HeaderIcon(), panel);
        window.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Content = window;
    }

    public void RetailDragOver(uint itemId, string itemName, Point? position) =>
        _panel.RetailDragOver(itemId, itemName, position is { } point ? this.TranslatePoint(point, _panel) : null);

    public bool RetailDrop(uint itemId, string itemName, Point position) =>
        this.TranslatePoint(position, _panel) is { } point && _panel.RetailDrop(itemId, itemName, point);

    public bool GameKeyDown(Key key) => _panel.GameKeyDown(key);

    public void RightClick(Point position)
    {
        if (this.TranslatePoint(position, _panel) is { } point) _panel.RightClick(point);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _panel.Dispose();
    }
}
