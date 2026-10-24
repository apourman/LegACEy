using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Market;

/// <summary>
/// The Sell screen: the pack or the Vault on tabs, the picked item's price panel, and the account's listings on Active, Sold and Expired.
/// The layout follows the window's <see cref="MarketLayoutMode"/>: wide and medium put the items and the price side by side with the listings
/// under them, and narrow puts the items (with the price panel under them once an item is picked) and the listings on two tabs. The screen
/// never measures itself. Everything it shows comes from <see cref="SellViewModel"/>.
/// </summary>
public sealed class SellScreen : UserControl, IDisposable
{
    private readonly SellViewModel _model;
    private readonly MarketIcons _icons;
    private MarketLayoutMode _mode = MarketLayoutMode.Medium;
    private bool _showListings;
    private bool _disposed;

    // a narrow window's Items | Listings tabs
    private readonly MarketTabs _narrowTabs = new("Items", "Listings") { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left };

    // the items panel: the pack and Vault tabs over the item grid
    private readonly Border _itemsPanel;
    private readonly MarketTabs _itemTabs = new("Pack", "Vault");
    private readonly WrapPanel _itemGrid = new() { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
    private readonly ScrollViewer _itemsScroll;

    // the price panel: the picked item, the price field, the lifetime line and "List for N"
    private readonly Border _pricePanel;
    private readonly Border _pickedIcon = new() { Width = 48, Height = 48 };
    private readonly TextBlock _pickedName = MarketParts.Text(string.Empty, DerethPalette.CreamBrush, 19, title: true);
    private readonly TextBlock _pickedDetail = MarketParts.Text(string.Empty, MarketInk.MutedBrush, 12);
    private readonly TextBox _price = new()
    {
        Watermark = "Whole stack", Height = 38, MinHeight = 0, FontSize = 20, FontFamily = DerethPalette.Title, Foreground = DerethPalette.CreamBrush,
        CaretBrush = DerethPalette.GoldBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly Border _priceField;
    private readonly TextBlock _lifetime = Wrapped(MarketInk.MutedBrush, 12);
    private readonly TextBlock _blocker = Wrapped(MarketInk.BadBrush, 12);
    private readonly MarketButton _listButton = MarketParts.Button("List for —", MarketParts.Kind.Go, 34);

    // the listings panel: Active, Sold and Expired, with time left and Delist
    private readonly Border _listingsPanel;
    private readonly MarketTabs _listingTabs = new("Active", "Sold", "Expired");
    private readonly TextBlock _activeCount = MarketParts.Text(string.Empty, MarketInk.MutedBrush, 12);
    private readonly StackPanel _listingRows = new();
    private readonly TextBlock _notice = MarketParts.Text(string.Empty, MarketInk.BadBrush, 12.5);

    private readonly Grid _layout = new() { ColumnSpacing = 14, RowSpacing = 14 };

    public SellScreen(SellViewModel model, MarketIcons icons)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _itemsScroll = new ScrollViewer { Content = _itemGrid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _priceField = new Border
        {
            Height = 40, CornerRadius = new CornerRadius(8), BorderBrush = MarketInk.LineBrush, BorderThickness = new Thickness(1),
            Background = MarketInk.SunkBrush, Padding = new Thickness(12, 0),
        };
        _itemsPanel = MarketParts.Card(new Panel(), 10, new Thickness(16, 14));
        _pricePanel = MarketParts.Card(new Panel(), 10, new Thickness(16, 14));
        _listingsPanel = MarketParts.Card(new Panel(), 10, new Thickness(16, 14));
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

    /// <summary>Applies the window's layout mode: which panels sit where, and whether the items and the listings share tabs.</summary>
    public void SetMode(MarketLayoutMode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        ApplyLayout();
        Refresh();
    }

    private static TextBlock Wrapped(IBrush brush, double size)
    {
        var text = MarketParts.Text(string.Empty, brush, size);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        return text;
    }

    private void ApplyLayout()
    {
        if (_mode == MarketLayoutMode.Narrow)
        {
            // narrow: one column; the Items tab is the item grid taking the height with the price panel under it, the Listings tab takes it all
            _layout.ColumnDefinitions = new ColumnDefinitions("*");
            _layout.RowDefinitions = new RowDefinitions("*,Auto");
            Place(_itemsPanel, 0, 0, 1, 1);
            Place(_pricePanel, 1, 0, 1, 1);
            Place(_listingsPanel, 0, 0, 2, 1);
            _itemsScroll.MaxHeight = double.PositiveInfinity;
        }
        else
        {
            // the items and the price side by side, the listings under both taking the rest of the height
            _layout.ColumnDefinitions = new ColumnDefinitions(_mode == MarketLayoutMode.Wide ? "400,*" : "*,*");
            _layout.RowDefinitions = new RowDefinitions("Auto,*");
            Place(_itemsPanel, 0, 0, 1, 1);
            Place(_pricePanel, 0, 1, 1, 1);
            Place(_listingsPanel, 1, 0, 1, 2);
            // the item grid scrolls; medium shows two rows of items, so the top row is no taller than the price panel and the listings keep the height
            _itemsScroll.MaxHeight = _mode == MarketLayoutMode.Wide ? 140 : 92;
        }
        _narrowTabs.IsVisible = _mode == MarketLayoutMode.Narrow;
    }

    private static void Place(Control control, int row, int column, int rowSpan, int columnSpan)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetRowSpan(control, rowSpan);
        Grid.SetColumnSpan(control, columnSpan);
    }

    /// <summary>Redraws the panels from the model. Called on every model change, and on a layout change.</summary>
    private void Refresh()
    {
        if (_disposed) return;

        var narrow = _mode == MarketLayoutMode.Narrow;
        // a narrow window shows the Items tab or the Listings tab, and the price panel under the items only once an item is picked
        _itemsPanel.IsVisible = !(narrow && _showListings);
        _listingsPanel.IsVisible = !narrow || _showListings;
        _pricePanel.IsVisible = !narrow || (!_showListings && _model.Selected != null);
        _narrowTabs.Selected = _showListings ? 1 : 0;

        _itemTabs.Selected = _model.Tab == SellTab.Pack ? 0 : 1;
        _listingTabs.Selected = (int)_model.ListingTab;

        RefreshItems();
        RefreshPrice();
        RefreshListings();
        _notice.Text = _model.Message;
        _notice.Foreground = _model.MessageGood ? MarketInk.LitTextBrush : MarketInk.BadBrush;
        _notice.IsVisible = _model.Message.Length > 0;
    }

    private void RefreshItems()
    {
        _itemGrid.Children.Clear();
        foreach (var item in _model.Items)
            _itemGrid.Children.Add(ItemCell(item));

        if (_model.Items.Count == 0)
            _itemGrid.Children.Add(MarketParts.Text(_model.Tab == SellTab.Pack ? "Nothing in your pack to sell." : "Nothing in your Vault.", MarketInk.MutedBrush, 12.5));
    }

    /// <summary>
    /// One item: its icon, the gold dot when it is listed, the greyed look and tooltip when it can't be listed, and the picked frame.
    /// </summary>
    private Control ItemCell(SellItem item)
    {
        var picked = _model.Selected?.Guid == item.Guid;
        var canvas = new Grid { Children = { new Viewbox { Width = 34, Height = 34, Child = Icon(item) } } };
        // the gold dot marks a Vault item already for sale
        canvas.Children.Add(new Ellipse
        {
            Width = 7, Height = 7, Fill = DerethPalette.GoldBrush, Stroke = Brushes.Black, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0), IsVisible = item.Listed, Name = "GoldDot"
        });
        if (item.StackSize > 1) canvas.Children.Add(DerethItemCells.StackCount(item.StackSize));
        var cell = new Border
        {
            Width = 42, Height = 42, Child = canvas, Tag = item.Guid, Cursor = new Cursor(StandardCursorType.Hand), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(picked ? 2 : 1), BorderBrush = picked ? MarketInk.LitBrush : DerethPalette.Brush(Color.Parse("#5A4A2C")),
            Background = DerethPalette.Brush(Color.Parse("#1A160F")), Opacity = item.Greyed ? 0.35 : 1,
            BoxShadow = picked ? BoxShadows.Parse("0 0 10 0 #593FCF5F") : default,
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
        _pickedName.Text = item?.Name ?? "Pick an item";
        _pickedDetail.Text = item == null ? "From your pack or your Vault"
            : item.StackSize > 1 ? $"{(_model.Tab == SellTab.Pack ? "From your pack" : "In your Vault")} · stack of {item.StackSize:N0}"
            : _model.Tab == SellTab.Pack ? "From your pack" : "In your Vault";
        _pickedIcon.Child = item == null ? null : MarketParts.IconTile(Icon(item), 48);
        _pickedIcon.IsVisible = item != null;
        if (_price.Text != _model.PriceText) _price.Text = _model.PriceText;
        _lifetime.Text = _model.LifetimeLine;
        _lifetime.IsVisible = _lifetime.Text.Length > 0;
        _blocker.Text = _model.ListBlocker ?? string.Empty;
        _blocker.IsVisible = _blocker.Text.Length > 0;
        ((TextBlock)_listButton.Content!).Text = $"List for {_model.Price?.ToString("N0") ?? "—"}";
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
            _listingRows.Children.Add(new Border
            {
                Padding = new Thickness(0, 8),
                Child = MarketParts.Text(_model.ListingTab == SellListingTab.Active ? "You have nothing listed." : $"Nothing {_model.ListingTab.ToString().ToLowerInvariant()} yet.", MarketInk.MutedBrush, 12.5),
            });
    }

    private Control ListingRowView(ListingRow row)
    {
        var active = row.Status == "active";
        var timeLeft = active ? _model.TimeLeft(row) : TimeText(row);
        var name = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { MarketParts.Text(row.Name, DerethPalette.CreamBrush, 13), MarketParts.Text(timeLeft, MarketInk.MutedBrush, 11.5) },
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var changePrice = new Border { Name = "ChangePriceSlot", Tag = row.Id, VerticalAlignment = VerticalAlignment.Center };
        if (active) changePrice.Child = ChangePrice(row);
        actions.Children.Add(changePrice);
        if (active)
        {
            var delist = MarketParts.Button("Delist", MarketParts.Kind.Ghost, 26);
            delist.Tag = row.Id;
            delist.Click += (_, _) => _model.Delist(row.Id);
            actions.Children.Add(delist);
        }

        // the state tag: listed in gold, ending (under a day left) in amber, sold in green, expired muted
        var ending = active && _model.Left(row) < TimeSpan.FromDays(1);
        var state = row.Status switch
        {
            "active" when ending => MarketParts.Tag("Ending", MarketInk.WarnBrush, DerethPalette.Brush(MarketInk.Warn.WithAlpha(0x1A)), caps: true),
            "active" => MarketParts.Tag("Listed", DerethPalette.GoldBrush, DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x1A)), caps: true),
            "sold" => MarketParts.Tag("Sold", MarketInk.LitTextBrush, DerethPalette.Brush(MarketInk.Lit.WithAlpha(0x1A)), caps: true),
            _ => MarketParts.Tag(row.Status, MarketInk.MutedBrush, null, caps: true),
        };

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 12 };
        var icon = MarketParts.IconTile(_icons.For(row.Visual, row.OverlaySecondary), 32);
        line.Children.Add(icon);
        Grid.SetColumn(name, 1);
        line.Children.Add(name);
        var price = MarketParts.Price(row.Price.ToString("N0"));
        Grid.SetColumn(price, 2);
        line.Children.Add(price);
        Grid.SetColumn(state, 3);
        line.Children.Add(state);
        Grid.SetColumn(actions, 4);
        line.Children.Add(actions);
        return new Border { Padding = new Thickness(4, 6), Child = line, Tag = row.Id, BorderBrush = MarketInk.LineSoftBrush, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    /// <summary>
    /// The Change price control of an active listing: a button, or while it is open, the price field with Set price and Cancel. The field is
    /// silent to the model while it is typed in, so the rows are not redrawn under the caret.
    /// </summary>
    private Control ChangePrice(ListingRow row)
    {
        if (_model.RepriceListingId != row.Id)
        {
            var open = MarketParts.Button("Change price", MarketParts.Kind.Ghost, 26);
            open.Tag = row.Id;
            open.Click += (_, _) => _model.StartReprice(row.Id);
            return open;
        }

        var field = MarketParts.Field("MMD", 26, 90);
        field.Text = _model.RepriceText;
        field.FontSize = 12.5;
        field.TextChanged += (_, _) => _model.SetRepriceText(field.Text ?? string.Empty);
        var confirm = MarketParts.Button("Set price", MarketParts.Kind.Red, 26);
        confirm.Click += (_, _) => _model.Reprice();
        var cancel = MarketParts.Button("Cancel", MarketParts.Kind.Ghost, 26);
        cancel.Click += (_, _) => _model.CancelReprice();
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { field, confirm, cancel } };
    }

    private static string TimeText(ListingRow row) =>
        row.Status == "sold" && row.Closed is DateTimeOffset sold ? $"sold {sold:yyyy-MM-dd}" : row.Status;

    private Control Icon(SellItem item) => _icons.For(item.Visual, item.OverlaySecondary);

    private Control BuildContent()
    {
        _itemTabs.Chosen += index => _model.ShowTab(index == 0 ? SellTab.Pack : SellTab.Vault);
        _narrowTabs.Chosen += index =>
        {
            _showListings = index == 1;
            Refresh();
        };
        _price.TextChanged += (_, _) => _model.SetPrice(_price.Text ?? string.Empty);
        _price.GotFocus += (_, _) => _priceField.BorderBrush = MarketInk.LitBrush;
        _price.LostFocus += (_, _) => _priceField.BorderBrush = MarketInk.LineBrush;
        _listButton.Click += (_, _) => _model.List();
        _listingTabs.Chosen += index => _model.ShowListingTab((SellListingTab)index);
        _listButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        // items: the head and the Pack | Vault tabs, the grid, and what the marks mean
        var itemsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        itemsHeader.Children.Add(MarketParts.Head("Your items"));
        Grid.SetColumn(_itemTabs, 1);
        itemsHeader.Children.Add(_itemTabs);
        var marks = MarketParts.Text("Gold dot: already listed. Greyed out: can't be sold here (attuned or bonded).", MarketInk.MutedBrush, 12);
        marks.TextWrapping = TextWrapping.Wrap;
        // a grid, not a stack, so the item grid's scroll viewer has a height to scroll in when a narrow window gives the panel the height
        Grid.SetRow(_itemsScroll, 1);
        Grid.SetRow(marks, 2);
        _itemsPanel.Child = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10, Children = { itemsHeader, _itemsScroll, marks } };

        // price: the picked item's head, then the price field, the lifetime, why it can't be listed, and List for N
        var coinRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        coinRow.Children.Add(MarketParts.Coin(16));
        Grid.SetColumn(_price, 1);
        coinRow.Children.Add(_price);
        var unit = MarketParts.Text("MMD", MarketInk.MutedBrush, 12);
        Grid.SetColumn(unit, 2);
        coinRow.Children.Add(unit);
        _priceField.Child = coinRow;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        head.Children.Add(_pickedIcon);
        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _pickedName, _pickedDetail } };
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        var form = new StackPanel { Spacing = 8, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(MarketParts.Caps("Price, whole stack", MarketInk.MutedBrush, 11));
        form.Children.Add(_priceField);
        form.Children.Add(_lifetime);
        form.Children.Add(_blocker);
        form.Children.Add(_listButton);
        _pricePanel.Child = new StackPanel { Spacing = 12, Children = { head, form } };

        // listings: the head with the count, the Active | Sold | Expired tabs, then the rows
        var listingsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 10 };
        listingsHeader.Children.Add(MarketParts.Head("Your listings"));
        Grid.SetColumn(_activeCount, 1);
        listingsHeader.Children.Add(_activeCount);
        Grid.SetColumn(_listingTabs, 3);
        listingsHeader.Children.Add(_listingTabs);
        var listingsScroll = new ScrollViewer { Content = _listingRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(listingsScroll, 1);
        _listingsPanel.Child = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10, Children = { listingsHeader, listingsScroll } };

        // the panels sit in a grid, and their places change with the mode
        _layout.Children.Add(_itemsPanel);
        _layout.Children.Add(_pricePanel);
        _layout.Children.Add(_listingsPanel);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8, Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(_notice);
        Grid.SetRow(_narrowTabs, 1);
        root.Children.Add(_narrowTabs);
        Grid.SetRow(_layout, 2);
        root.Children.Add(_layout);
        return root;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Changed -= Refresh;
        _model.Dispose();
    }
}
