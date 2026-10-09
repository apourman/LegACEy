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
    public const int WindowWidth = 344;
    public const int WindowHeight = 606;
    /// <summary>The header's chest icon, from the DAT.</summary>
    public const uint ChestIconId = 0x06001020;
    private const int MinimumCells = 24;
    private const int SampleCount = 317;
    private const double DragThreshold = 4;
    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush;
    private static readonly IBrush TextBrush = DerethPalette.TextBrush;
    private static readonly IBrush MutedBrush = DerethPalette.MutedBrush;
    private static readonly IBrush Invalid = DerethPalette.InvalidBrush;
    private static readonly IBrush ShadowBrush = DerethPalette.Brush(Colors.Black);
    private static readonly IBrush ValidFill = DerethPalette.Brush(Color.FromArgb(0x40, DerethPalette.Gold.R, DerethPalette.Gold.G, DerethPalette.Gold.B));
    private static readonly IBrush InvalidFill = DerethPalette.Brush(Color.FromArgb(0x40, DerethPalette.Invalid.R, DerethPalette.Invalid.G, DerethPalette.Invalid.B));

    private static readonly (string Name, uint Icon, uint Plate, int Count)[] Samples =
    {
        ("Chainmail shirt", 0x06000FC7, 0x060011CF, 1),
        ("Leather boots", 0x06000FAD, 0x060011F3, 1),
        ("Gold ring", 0x06000FB5, 0x060011D5, 1),
        ("Pendant", 0x06000FBE, 0x060011D5, 1),
        ("Steel shield", 0x06000FCB, 0x060011CF, 1),
        ("Leather cap", 0x06000FAA, 0x060011F3, 1),
        ("Blue potion", 0x06001012, 0x060011D4, 12),
        ("Yellow potion", 0x06001013, 0x060011D4, 3),
        ("Treasure chest", ChestIconId, 0x060011D4, 1),
        ("Small pouch", 0x06001031, 0x060011D4, 25),
        ("Green bottle", 0x06001030, 0x060011D4, 8),
        ("Silver goblet", 0x0600101F, 0x060011D4, 1)
    };

    private readonly Dictionary<uint, WriteableBitmap?> _images = new();
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
    private readonly List<Grid> _liveSlots = new();
    private readonly List<Border> _dropIndicators = new();
    private bool _disposed;
    // a retail item being dragged over the window, and the cell it would land in
    private uint _retailItem;
    private string _retailName = string.Empty;
    private bool _retailOver;
    private int _dropCell = -1;
    // an item being dragged out of the window to withdraw it
    private VaultItemView? _pressItem;
    private Point _pressPoint;
    private VaultItemView? _dragItem;
    private IDisposable? _dragIcon;

    /// <param name="client">The live Vault; the panel owns it and disposes it. Null shows the sample Vault.</param>
    /// <param name="dragHost">Lets items be dragged out of the window onto the retail inventory to withdraw them.</param>
    public VaultShellPanel(IGameArtSource art, VaultClient? client = null, IItemDragHost? dragHost = null)
    {
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _client = client;
        _dragHost = dragHost;
        _sample = client == null ? SampleSnapshot() : null;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 10, 2, 8) };
        summary.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _itemsLabel, _items } });
        Grid.SetColumn(_balance, 1);
        summary.Children.Add(_balance);
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
    internal Control HeaderIcon() => Icon(new[] { ChestIconId });

    private static VaultSnapshot SampleSnapshot()
    {
        var items = new List<VaultItemView>(SampleCount);
        for (var index = 0; index < SampleCount; index++)
        {
            var sample = Samples[(index * 7) % Samples.Length];
            items.Add(new VaultItemView((uint)index + 1, sample.Name, 0, sample.Count, 0, "held", string.Empty,
                DateTimeOffset.FromUnixTimeSeconds(0), sample.Plate, 0, sample.Icon, 0, 0, 0));
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
            _balance.IsVisible = snapshot.HasBalance;
        }
        else
        {
            _itemsLabel.Text = _client?.Connection == VaultConnection.Connecting ? "Connecting to the server…" : "Vault unavailable";
            _items.Text = string.Empty;
            _balance.IsVisible = false;
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
                var cell = DerethSlotGrid.Cell(item == null ? null : Icon(item.IconLayers));
                if (item != null)
                {
                    ToolTip.SetTip(cell, Describe(item));
                    if (item.StackSize > 1) cell.Children.Add(StackCount(item.StackSize));
                    if (_dragItem != null && item.Guid == _dragItem.Guid) cell.Opacity = 0.4;
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
        ShowStatus();
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
            if (_dropCell < 0) return ($"Drop {_dragItem.Name} on your inventory to withdraw it", false);
            // A filtered page can't be rearranged, so the move would be refused: say so instead of offering it.
            return client.Search.Length > 0 ? (VaultClient.SearchBlocksMove, false) : ($"Release to move {_dragItem.Name} here", false);
        }
        return (client.Notice, false);
    }

    /// <summary>The index of the vault cell under a point in this panel's coordinates, or -1.</summary>
    private int CellAt(Point position)
    {
        var viewport = _grid.TranslatePoint(default, this);
        if (viewport == null || !new Rect(viewport.Value, _grid.Bounds.Size).Contains(position)) return -1;
        for (var index = 0; index < _liveSlots.Count; index++)
        {
            var slot = _liveSlots[index];
            var origin = slot.TranslatePoint(default, this);
            if (origin != null && new Rect(origin.Value, slot.Bounds.Size).Contains(position)) return index;
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

    private void ConnectCell(Grid cell, VaultItemView item)
    {
        // Dragging an item out of the window and onto the retail inventory withdraws it.
        cell.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnCellPressed(item, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        cell.AddHandler(InputElement.PointerMovedEvent, (_, e) => OnCellMoved(item, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        cell.AddHandler(InputElement.PointerReleasedEvent, (_, e) => OnCellReleased(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        cell.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndWithdrawDrag(), handledEventsToo: true);
    }

    private void OnCellPressed(VaultItemView item, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressItem = item;
        _pressPoint = e.GetPosition(this);
    }

    private void OnCellMoved(VaultItemView item, PointerEventArgs e)
    {
        if (_dragItem != null)
        {
            UpdateLiftedHover(e);
            return;
        }
        if (_dragHost == null || _client == null || _pressItem != item) return;
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
        _pressItem = null;
        if (item.State != "held" || _client.TransferPending)
        {
            _client.Tell(item.State == "held" ? "Wait for the current transfer to finish." : $"{item.Name} is {StateName(item.State).ToLowerInvariant()} and can't be withdrawn.");
            return;
        }
        _dragItem = item;
        _dragIcon = _dragHost.ShowDragIcon(new List<uint>(item.IconLayers));
        // The lifted item's own cell dims, as the retail inventory ghosts a dragged item.
        var from = IndexOf(item.Guid);
        if (from >= 0 && from < _liveSlots.Count) _liveSlots[from].Opacity = 0.4;
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

    private void OnCellReleased(PointerReleasedEventArgs e)
    {
        var item = _dragItem;
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
                _client.Withdraw(item.Guid);
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
        _dragIcon?.Dispose();
        _dragIcon = null;
        if (!wasDragging || _disposed) return;
        foreach (var slot in _liveSlots) slot.Opacity = 1;
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

    private WriteableBitmap? Bitmap(uint id)
    {
        if (!_images.TryGetValue(id, out var bitmap))
            _images.Add(id, bitmap = GameArtImageExtension.CreateBitmap(_art, id));
        return bitmap;
    }

    /// <summary>An item's icon layers at native size, stacked bottom to top. Missing art shows a question mark.</summary>
    private Grid Icon(IEnumerable<uint> ids)
    {
        var layers = new Grid
        {
            Width = 32, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var id in ids)
        {
            var bitmap = Bitmap(id);
            if (bitmap != null)
                layers.Children.Add(new Image { Source = bitmap, Width = 32, Height = 32, Stretch = Stretch.None });
        }
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
    }
}

/// <summary>The Vault window: the Dereth frame, its header and close box, around the Vault panel.</summary>
public sealed class VaultShellWindow : UserControl, IRetailItemDropTarget, IDisposable
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _panel.Dispose();
    }
}
