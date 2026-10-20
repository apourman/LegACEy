using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Shapes;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;
using static LegACEy.Client.Themes.DerethItemCells;

namespace LegACEy.Plugin.Market;

/// <summary>
/// The Sell screen: the pack or the Vault on tabs, the picked item's price panel, and the account's listings on Active, Sold and Expired.
/// The layout follows the window's <see cref="MarketLayoutMode"/>: wide puts the items and the price side by side with the listings under them,
/// medium stacks the items above the price panel, and narrow shows the items, then the price panel as a detail page with a Back. The screen
/// never measures itself. Everything it shows comes from <see cref="SellViewModel"/>.
/// </summary>
public sealed class SellScreen : UserControl, IDisposable
{
    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush;
    private static readonly IBrush TextBrush = DerethPalette.TextBrush;
    private static readonly IBrush MutedBrush = DerethPalette.MutedBrush;
    private static readonly IBrush InvalidBrush = DerethPalette.InvalidBrush;

    private readonly SellViewModel _model;
    private readonly IGameArtSource _art;
    private readonly Dictionary<(uint Plate, uint Underlay, uint Icon, uint Overlay, uint Secondary, uint Effects), WriteableBitmap?> _images = new();
    private MarketLayoutMode _mode = MarketLayoutMode.Medium;
    private bool _disposed;

    // the items panel: the pack and Vault tabs over the item grid
    private readonly Border _itemsPanel = new();
    private readonly DerethButton _packTab = Tab("Pack");
    private readonly DerethButton _vaultTab = Tab("Vault");
    private readonly WrapPanel _itemGrid = new() { Orientation = Orientation.Horizontal, MaxWidth = 720 };

    // the price panel: the picked item, the price field, the lifetime line and "List for N"
    private readonly Border _pricePanel = new();
    private readonly DerethButton _back = new() { Content = Label("‹ Back", GoldBrush, 12), Height = 26, IsVisible = false };
    private readonly TextBlock _pickedName = Label(string.Empty, TextBrush, 15);
    private readonly TextBlock _pickedDetail = Label(string.Empty, MutedBrush, 11);
    private readonly TextBox _price = new() { Watermark = "Whole stack, MMD", Height = 30, MinWidth = 120, FontSize = 14 };
    private readonly TextBlock _lifetime = Label(string.Empty, MutedBrush, 11);
    private readonly TextBlock _blocker = Label(string.Empty, InvalidBrush, 11);
    private readonly DerethButton _listButton = new() { Height = 34 };

    // the listings panel: Active, Sold and Expired, with time left and Delist
    private readonly Border _listingsPanel = new();
    private readonly DerethButton _activeTab = Tab("Active");
    private readonly DerethButton _soldTab = Tab("Sold");
    private readonly DerethButton _expiredTab = Tab("Expired");
    private readonly TextBlock _activeCount = Label(string.Empty, MutedBrush, 12);
    private readonly StackPanel _listingRows = new() { Spacing = 4 };
    private readonly TextBlock _notice = Label(string.Empty, InvalidBrush, 12);

    private readonly Grid _layout = new();

    public SellScreen(SellViewModel model, IGameArtSource art)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _art = art ?? throw new ArgumentNullException(nameof(art));
        Content = BuildContent();
        _model.Changed += Refresh;
        ApplyLayout();
        Refresh();
    }

    /// <summary>The model the screen draws. Exposed for the tests, which drive it as the buttons do.</summary>
    public SellViewModel Model => _model;

    /// <summary>Loads the screen's data: the Vault, the listings and what can be sold. The shell calls it when the screen is shown.</summary>
    public void Open() => _model.Open();

    /// <summary>The layout the screen draws. Set by the window through the shell, from its width; the screen never measures.</summary>
    public MarketLayoutMode Mode => _mode;

    /// <summary>Applies the window's layout mode: which panels sit where, and whether the narrow price panel is a detail page.</summary>
    public void SetMode(MarketLayoutMode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        ApplyLayout();
        Refresh();
    }

    private void ApplyLayout()
    {
        switch (_mode)
        {
            case MarketLayoutMode.Wide:
                // the items and the price side by side, the listings under both; the item grid takes the spare height and scrolls
                _layout.ColumnDefinitions = new ColumnDefinitions("400,*");
                _layout.RowDefinitions = new RowDefinitions("*,*");
                Place(_itemsPanel, 0, 0, 1);
                Place(_pricePanel, 0, 1, 1);
                Place(_listingsPanel, 1, 0, 2);
                break;
            case MarketLayoutMode.Medium:
                // the items stack above the price panel, and the listings take the rest; the items grid scrolls, the price panel keeps its height
                _layout.ColumnDefinitions = new ColumnDefinitions("*");
                _layout.RowDefinitions = new RowDefinitions("2*,Auto,*");
                Place(_itemsPanel, 0, 0, 1);
                Place(_pricePanel, 1, 0, 1);
                Place(_listingsPanel, 2, 0, 1);
                break;
            default:
                // narrow: the items and the listings, or the price panel as a detail page in their place
                _layout.ColumnDefinitions = new ColumnDefinitions("*");
                _layout.RowDefinitions = new RowDefinitions("*,*");
                Place(_itemsPanel, 0, 0, 1);
                Place(_pricePanel, 0, 0, 1);
                Place(_listingsPanel, 1, 0, 1);
                break;
        }
    }

    private static void Place(Control control, int row, int column, int columnSpan)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
    }

    /// <summary>Redraws the panels from the model. Called on every model change, and on a layout change.</summary>
    private void Refresh()
    {
        if (_disposed) return;

        var narrow = _mode == MarketLayoutMode.Narrow;
        var picked = _model.Selected != null;
        // a narrow window shows the items or the listings, and the price panel only once an item is picked
        _itemsPanel.IsVisible = !(narrow && picked);
        _listingsPanel.IsVisible = !(narrow && picked);
        _pricePanel.IsVisible = !narrow || picked;
        _back.IsVisible = narrow;

        _packTab.Foreground = _model.Tab == SellTab.Pack ? GoldBrush : TextBrush;
        _vaultTab.Foreground = _model.Tab == SellTab.Vault ? GoldBrush : TextBrush;
        var listingTab = _model.ListingTab;
        _activeTab.Foreground = listingTab == SellListingTab.Active ? GoldBrush : TextBrush;
        _soldTab.Foreground = listingTab == SellListingTab.Sold ? GoldBrush : TextBrush;
        _expiredTab.Foreground = listingTab == SellListingTab.Expired ? GoldBrush : TextBrush;

        RefreshItems();
        RefreshPrice();
        RefreshListings();
        _notice.Text = _model.Message;
        _notice.IsVisible = _model.Message.Length > 0;
    }

    private void RefreshItems()
    {
        _itemGrid.Children.Clear();
        foreach (var item in _model.Items)
            _itemGrid.Children.Add(ItemCell(item));

        if (_model.Items.Count == 0)
            _itemGrid.Children.Add(Label(_model.Tab == SellTab.Pack ? "Nothing in your pack to sell." : "Nothing in your Vault.", MutedBrush, 12));
    }

    /// <summary>
    /// One item: its icon, the gold dot when it is listed, the greyed look and tooltip when it can't be listed, and the picked frame.
    /// </summary>
    private Control ItemCell(SellItem item)
    {
        var picked = _model.Selected?.Guid == item.Guid;
        var icon = new Image { Width = 40, Height = 40, Source = Bitmap(item), Stretch = Stretch.Uniform };
        var canvas = new Grid { Width = 44, Height = 44, Children = { icon } };
        // the gold dot marks a Vault item already for sale
        canvas.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = GoldBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0), IsVisible = item.Listed, Name = "GoldDot" });
        var cell = new Border
        {
            Width = 44, Height = 44, Margin = new Thickness(2), Child = canvas, Tag = item.Guid, Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(1), BorderBrush = picked ? GoldBrush : Brushes.Transparent,
            Opacity = item.Greyed ? 0.35 : 1,
        };
        if (item.Greyed && item.Reason.Length > 0) ToolTip.SetTip(cell, item.Reason);
        else if (item.Listed) ToolTip.SetTip(cell, "Listed for sale");
        else ToolTip.SetTip(cell, item.Name);
        cell.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) _model.Pick(item.Guid);
        };
        return cell;
    }

    private void RefreshPrice()
    {
        var item = _model.Selected;
        _pickedName.Text = item?.Name ?? string.Empty;
        _pickedDetail.Text = item == null ? string.Empty
            : item.StackSize > 1 ? $"{(_model.Tab == SellTab.Pack ? "From your pack" : "In your Vault")} · stack of {item.StackSize:N0}"
            : _model.Tab == SellTab.Pack ? "From your pack" : "In your Vault";
        if (_price.Text != _model.PriceText) _price.Text = _model.PriceText;
        _lifetime.Text = _model.LifetimeLine;
        _blocker.Text = _model.ListBlocker ?? string.Empty;
        _blocker.IsVisible = _blocker.Text.Length > 0;
        _listButton.Content = Label($"List for {_model.Price?.ToString("N0") ?? "—"}", TextBrush, 14);
        _listButton.IsEnabled = _model.ListBlocker == null;
    }

    private void RefreshListings()
    {
        _activeCount.Text = _model.ActiveCount is int count
            ? (_model.ActiveCap is int cap ? $"{count:N0} of {cap:N0}" : $"{count:N0}")
            : string.Empty;

        _listingRows.Children.Clear();
        var rows = _model.Listings?.Rows ?? Array.Empty<ListingRow>();
        foreach (var row in rows)
            _listingRows.Children.Add(ListingRowView(row));

        if (rows.Count == 0)
            _listingRows.Children.Add(Label(_model.ListingTab == SellListingTab.Active ? "You have nothing listed." : $"Nothing {_model.ListingTab.ToString().ToLowerInvariant()} yet.", MutedBrush, 12));
    }

    private Control ListingRowView(ListingRow row)
    {
        var active = row.Status == "active";
        var name = new StackPanel { Spacing = 0, Children = { Label(row.Name, TextBrush, 13), Label(active ? _model.TimeLeft(row) : TimeText(row), MutedBrush, 11) } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var changePrice = new Border { Name = "ChangePriceSlot", Tag = row.Id, VerticalAlignment = VerticalAlignment.Center };
        if (active) changePrice.Child = ChangePrice(row);
        actions.Children.Add(changePrice);
        if (active)
        {
            var delist = new DerethButton { Content = Label("Delist", GoldBrush, 12), Height = 26, Tag = row.Id };
            delist.Click += (_, _) => _model.Delist(row.Id);
            actions.Children.Add(delist);
        }

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        Grid.SetColumn(name, 0);
        line.Children.Add(name);
        var price = Label($"{row.Price:N0} MMD", TextBrush, 13);
        Grid.SetColumn(price, 1);
        line.Children.Add(price);
        Grid.SetColumn(actions, 2);
        line.Children.Add(actions);
        return new Border { Padding = new Thickness(6, 4), Child = line, Tag = row.Id, BorderBrush = DerethPalette.GoldDarkBrush, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    /// <summary>
    /// The Change price control of an active listing: a button, or while it is open, the price field with Set price and Cancel. The field is
    /// silent to the model while it is typed in, so the rows are not redrawn under the caret.
    /// </summary>
    private Control ChangePrice(ListingRow row)
    {
        if (_model.RepriceListingId != row.Id)
        {
            var open = new DerethButton { Content = Label("Change price", GoldBrush, 12), Height = 26, Tag = row.Id };
            open.Click += (_, _) => _model.StartReprice(row.Id);
            return open;
        }

        var field = new TextBox { Text = _model.RepriceText, Watermark = "MMD", Width = 90, Height = 26, FontSize = 12 };
        field.TextChanged += (_, _) => _model.SetRepriceText(field.Text ?? string.Empty);
        var confirm = new DerethButton { Content = Label("Set price", GoldBrush, 12), Height = 26 };
        confirm.Click += (_, _) => _model.Reprice();
        var cancel = new DerethButton { Content = Label("Cancel", TextBrush, 12), Height = 26 };
        cancel.Click += (_, _) => _model.CancelReprice();
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { field, confirm, cancel } };
    }

    private string TimeText(ListingRow row) => row.Status switch
    {
        "sold" => row.Closed is DateTimeOffset sold ? $"sold {sold:yyyy-MM-dd}" : "sold",
        "expired" => "expired",
        _ => row.Status,
    };

    private WriteableBitmap? Bitmap(SellItem item)
    {
        var key = (item.Visual.Plate, item.Visual.Underlay, item.Visual.Icon, item.Visual.Overlay, item.OverlaySecondary, item.Visual.UiEffects);
        if (!_images.TryGetValue(key, out var bitmap))
        {
            bitmap = GameArtImageExtension.CreateBitmap(ItemIcon.Draw(_art, item.Visual.Underlay, item.Visual.Icon, item.Visual.Overlay, item.OverlaySecondary, item.Visual.UiEffects, item.Visual.Plate));
            _images.Add(key, bitmap);
        }
        return bitmap;
    }

    private Control BuildContent()
    {
        _packTab.Click += (_, _) => _model.ShowTab(SellTab.Pack);
        _vaultTab.Click += (_, _) => _model.ShowTab(SellTab.Vault);
        _back.Click += (_, _) => _model.Back();
        _price.TextChanged += (_, _) => _model.SetPrice(_price.Text ?? string.Empty);
        _listButton.Click += (_, _) => _model.List();
        _activeTab.Click += (_, _) => _model.ShowListingTab(SellListingTab.Active);
        _soldTab.Click += (_, _) => _model.ShowListingTab(SellListingTab.Sold);
        _expiredTab.Click += (_, _) => _model.ShowListingTab(SellListingTab.Expired);

        // the header keeps its height; the scroller takes the rest of the panel, so the grid scrolls and the panel keeps its place
        var itemsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { Label("Your items", GoldBrush, 14), Tabs(_packTab, _vaultTab) } };
        var itemsScroll = new ScrollViewer { Content = _itemGrid };
        Grid.SetRow(itemsScroll, 1);
        _itemsPanel.Child = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { itemsHeader, itemsScroll } };

        _pricePanel.Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { _back } },
                _pickedName,
                _pickedDetail,
                Label("Price, whole stack", MutedBrush, 11),
                _price,
                _lifetime,
                _blocker,
                _listButton,
            },
        };

        var listingsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { Label("Your listings", GoldBrush, 14), new StackPanel(), Tabs(_activeTab, _soldTab, _expiredTab) } };
        var listingsScroll = new ScrollViewer { Content = _listingRows };
        Grid.SetRow(_activeCount, 1);
        Grid.SetRow(listingsScroll, 2);
        _listingsPanel.Child = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Children = { listingsHeader, _activeCount, listingsScroll } };

        // the panels sit in a grid, and their places change with the mode
        _layout.Children.Add(_itemsPanel);
        _layout.Children.Add(_pricePanel);
        _layout.Children.Add(_listingsPanel);

        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(_notice);
        root.Children.Add(_layout);
        return root;
    }

    private static StackPanel Tabs(params Control[] tabs)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var tab in tabs) row.Children.Add(tab);
        return row;
    }

    private static DerethButton Tab(string text) => new() { Content = Label(text, TextBrush, 12), Height = 26 };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Changed -= Refresh;
        _model.Dispose();
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
    }
}
