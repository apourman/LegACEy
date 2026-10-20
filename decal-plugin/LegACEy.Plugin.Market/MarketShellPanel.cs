using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;
using static LegACEy.Client.Themes.DerethItemCells;

namespace LegACEy.Plugin.Market;

/// <summary>The Market's screens, in side bar order.</summary>
public enum MarketScreen { Browse, Sell, Activity }

/// <summary>
/// The Market window's contents: the top bar, the side bar, the icon rail or the tab row, and the Browse, Sell and Activity screens behind them.
/// The window tells it the <see cref="MarketLayoutMode"/>; it never measures itself.
/// </summary>
public sealed class MarketShellPanel : UserControl, IDisposable
{
    private static readonly MarketScreen[] Screens = { MarketScreen.Browse, MarketScreen.Sell, MarketScreen.Activity };

    private static readonly IBrush InvalidBrush = DerethPalette.InvalidBrush;

    private readonly MarketClient _client;
    private readonly MarketBrowse _browse;
    private readonly MarketBrowseView _browseView;
    // the Sell screen, owned by the panel: its model is disposed with it
    private readonly SellScreen _sell;
    private readonly ActivityScreen _activity;
    private readonly MarketSearch _search = new("Search the market");
    // The search field sits in the top bar, or in its own full-width row under it in a narrow window.
    private readonly Border _topSearchHost = new() { Height = 32, MaxWidth = 480, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _narrowSearchHost = new() { Height = 34, Margin = new Thickness(10, 8, 10, 0), IsVisible = false };
    private readonly MarketButton _searchIcon = new()
    {
        Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Content = new MarketGlyph(MarketIcon.Search, DerethPalette.Gold), IsVisible = false
    };
    private readonly TextBlock _balance = MarketParts.Text("—", DerethPalette.CreamBrush, 14);
    private readonly TextBlock _vaultSpace = MarketParts.Text(string.Empty, DerethPalette.CreamBrush, 14);
    private readonly StackPanel _vaultStat;
    private readonly TextBlock _character = MarketParts.Text(string.Empty, DerethPalette.CreamBrush, 13);
    private readonly TextBlock _portrait = new()
    {
        FontSize = 12, FontFamily = DerethPalette.Title, Foreground = DerethPalette.Brush(Color.Parse("#2A1A0C")),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border _portraitFrame;
    private readonly TextBlock _notice = new()
    {
        FontSize = 12, FontFamily = DerethPalette.Body, TextWrapping = TextWrapping.Wrap, Foreground = InvalidBrush,
        Margin = new Thickness(4, 4, 4, 0), IsVisible = false
    };

    // The side bar in a wide window, the icon rail in a medium one and the tab row in a narrow one: the same screens, drawn three ways.
    private readonly Border _sideBar = new()
    {
        BorderBrush = MarketInk.LineSoftBrush, BorderThickness = new Thickness(0, 0, 1, 0), Background = DerethPalette.Brush(Color.FromArgb(0x2E, 0, 0, 0)),
    };
    private readonly StackPanel _wideNav = new() { Width = 192, Spacing = 3, Margin = new Thickness(12, 16, 12, 12) };
    // the categories with their counts, from the Browse screen's facets
    private readonly StackPanel _categoryList = new() { Spacing = 1 };
    private readonly StackPanel _rail = new() { Spacing = 4, Margin = new Thickness(8, 14, 8, 8), IsVisible = false };
    private readonly Grid _tabRow = new() { IsVisible = false };
    private readonly List<(MarketScreen Screen, MarketButton Button, MarketGlyph? Glyph, TextBlock? Label, NavShape Shape)> _navEntries = new();
    private enum NavShape { Row, Rail, Tab }
    private readonly Dictionary<MarketScreen, Control> _screens = new();
    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
    private readonly BuyDialog _buyDialog;
    // the purchase toast, bottom right over the window; it doesn't block, and goes on a click or after a while
    private readonly DerethToast _toast = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 20, 20)
    };
    // the unseen sales the Sell entries count, kept by the plugin across windows
    private readonly MarketSales? _sales;
    private readonly List<(Border Badge, TextBlock Count)> _sellBadges = new();
    private MarketScreen _screen = MarketScreen.Browse;
    private MarketLayoutMode _mode = MarketLayoutMode.Medium;
    private bool _disposed;

    /// <param name="browse">the Browse screen's state; the panel owns it, as it owns the client</param>
    /// <param name="art">the game's art, for the item icons</param>
    /// <param name="tell">sends a tell to a seller: the name and the text. False when nothing was sent.</param>
    /// <param name="sell">the Sell screen; the panel owns it</param>
    /// <param name="sales">the unseen sales, which badge the Sell entries</param>
    public MarketShellPanel(MarketClient client, MarketBrowse browse, IGameArtSource art, Func<string, string, bool>? tell, SellScreen sell,
        MarketSales? sales = null)
    {
        _sales = sales;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        _browseView = new MarketBrowseView(browse, art ?? throw new ArgumentNullException(nameof(art)), tell);
        _browseView.Balance = () => client.Account?.Balance;
        _sell = sell ?? throw new ArgumentNullException(nameof(sell));
        _activity = new ActivityScreen(client);
        _buyDialog = new BuyDialog(browse, _browseView.IconFor);
        _portraitFrame = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), BorderBrush = MarketInk.GoldRimBrush,
            Child = _portrait,
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.4, 0.35, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.4, 0.35, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#D8B08C"), 0), new GradientStop(Color.Parse("#6A4A30"), 1) }
            },
        };
        _vaultStat = Stat("Vault", _vaultSpace, coin: false);
        Content = BuildContent();
        _client.Changed += OnClientChanged;
        _browse.Changed += OnBrowseChanged;
        _browse.Bought += OnBought;
        if (_sales != null) _sales.Changed += OnSalesChanged;
        if (SearchBox() is { } box)
        {
            // typing suggests names; Enter searches for the text, as the portal's search does
            box.TextChanged += (_, _) => { if (SearchHasFocus) _browse.SuggestFor(box.Text ?? string.Empty); };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                _browse.Search(box.Text ?? string.Empty);
                _browse.ClearSuggestions();
                e.Handled = true;
            };
        }
        ApplyMode(_mode);
        RefreshBadges();
        Show(_screen);
        Refresh();
        RefreshCategories();
        _browse.Start();
    }

    /// <summary>The Browse screen</summary>
    public MarketBrowseView BrowseScreen => _browseView;

    /// <summary>The buy dialog, drawn over the window while a purchase is open</summary>
    public BuyDialog BuyDialog => _buyDialog;

    /// <summary>The purchase toast's two lines, or empty while it is not shown</summary>
    public string ToastText => _toast.Text;

    /// <summary>Once a frame, from the window: the toast hides when its time is up.</summary>
    public void Tick() => _toast.Tick();

    /// <summary>The Sell entries' count of unseen sales, or empty while there are none</summary>
    public string SellBadgeText => _sellBadges.Count > 0 && _sellBadges[0].Badge.IsVisible ? _sellBadges[0].Count.Text ?? string.Empty : string.Empty;

    /// <summary>
    /// A sale or a look at Sold: the badges show the count, and a sale reads the account (the balance) again. A sale while the Sold tab is
    /// showing is seen at once, and the tab reloads to show it.
    /// </summary>
    private void OnSalesChanged()
    {
        if (_disposed) return;
        if (_sales!.Unseen > 0)
        {
            _client.Start();
            if (_screen == MarketScreen.Sell && _sell.Model.ListingTab == SellListingTab.Sold) _sell.Model.ShowListingTab(SellListingTab.Sold);
        }
        RefreshBadges();
    }

    private void RefreshBadges()
    {
        var unseen = _sales?.Unseen ?? 0;
        foreach (var (badge, count) in _sellBadges)
        {
            badge.IsVisible = unseen > 0;
            count.Text = unseen.ToString("N0");
        }
    }

    /// <summary>
    /// A purchase went through: a toast says where the item is and who was paid, and the account (the balance, the Vault count) is read again.
    /// </summary>
    private void OnBought(ListingCard listing, BuyReply receipt)
    {
        if (_disposed) return;
        _toast.Show($"Bought {listing.Name}", $"{receipt.Message} {receipt.Price:N0} MMD paid to {listing.Seller}.");
        _client.Start();
    }

    /// <summary>The Browse screen's state, which the panel's side bar and search read</summary>
    public MarketBrowse BrowseState => _browse;

    /// <summary>The side bar's category entries as drawn, "All" first, then each category, each as "name  count"</summary>
    public IReadOnlyList<string> CategoryTexts => _categoryList.Children.OfType<Button>()
        .Select(b => b.Content is Grid row ? string.Join("  ", row.Children.OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t))) : string.Empty)
        .ToList();

    /// <summary>Draws the side bar's categories from the facets: all categories, then each with its count; the one being searched is lit</summary>
    private void RefreshCategories()
    {
        _categoryList.Children.Clear();
        var facets = _browse.Facets;
        var current = _browse.Filters.FirstOrDefault(f => f.Key == MarketBrowse.CategoryKey)?.Low;
        var total = facets?.Categories.Sum(c => (long)c.Count);
        _categoryList.Children.Add(CategoryRow("All", total, current == null, () => _browse.SetCategory(null)));
        if (facets == null) return;
        foreach (var category in facets.Categories)
        {
            var name = category.Value;
            _categoryList.Children.Add(CategoryRow(category.Label, category.Count, current == name, () => _browse.SetCategory(name)));
        }
    }

    /// <summary>A side bar category: the name, and its count on the right; the chosen one has a light wash.</summary>
    private static Button CategoryRow(string name, long? count, bool on, Action pick)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(MarketParts.Text(name, on ? DerethPalette.CreamBrush : MarketInk.TextBrush, 13));
        var number = MarketParts.Text(count?.ToString("N0") ?? string.Empty, MarketInk.MutedBrush, 11.5);
        Grid.SetColumn(number, 1);
        row.Children.Add(number);
        var button = new MarketButton
        {
            Content = row, Height = 26, Padding = new Thickness(10, 0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = on ? DerethPalette.Brush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
        };
        button.Click += (_, _) => pick();
        return button;
    }

    /// <summary>The layout the views are drawn in. Set by the window from its width, never by a view.</summary>
    public MarketLayoutMode Mode => _mode;

    /// <summary>Whether the search field has the keyboard.</summary>
    public bool SearchHasFocus => SearchBox()?.IsFocused == true;

    /// <summary>The text in the search field.</summary>
    public string SearchText => SearchBox()?.Text ?? string.Empty;

    /// <summary>The top bar's balance text.</summary>
    public string BalanceText => _balance.Text ?? string.Empty;

    /// <summary>The top bar's Vault space text, or empty while it is not shown (a window that isn't wide).</summary>
    public string VaultSpaceText => _vaultSpace.IsVisible ? _vaultSpace.Text ?? string.Empty : string.Empty;

    /// <summary>The notice line: why the market can't show its account, or that it is paused or frozen. Empty when there is nothing to say.</summary>
    public string NoticeText => _notice.IsVisible ? _notice.Text ?? string.Empty : string.Empty;

    /// <summary>Applies a layout mode to the views. Called by the window when its width changes the mode.</summary>
    public void SetMode(MarketLayoutMode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        ApplyMode(mode);
    }

    /// <summary>Shows a screen, from the side bar, the rail or the tab row. The Sell screen loads its data each time it is shown.</summary>
    public void Show(MarketScreen screen)
    {
        _screen = screen;
        if (screen == MarketScreen.Sell) _sell.Open();
        if (screen == MarketScreen.Activity) _activity.Load();
        foreach (var pair in _screens) pair.Value.IsVisible = pair.Key == screen;
        foreach (var entry in _navEntries) Light(entry, entry.Screen == screen);
    }

    /// <summary>Puts the cursor in the search field. A narrow window opens its full-width field first.</summary>
    public void FocusSearch()
    {
        if (_mode == MarketLayoutMode.Narrow) _narrowSearchHost.IsVisible = true;
        SearchBox()?.Focus();
    }

    /// <summary>Whether a narrow window's full-width search row is open.</summary>
    public bool NarrowSearchOpen => _narrowSearchHost.IsVisible;

    private TextBox? SearchBox() => _search.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();

    private void OnClientChanged(object? sender, EventArgs e)
    {
        Refresh();
        if (!_disposed) _buyDialog.Show(_browse.Buying, _client.Account);
    }

    /// <summary>Keeps the search box showing the name the results are searched by, unless the player is typing in it</summary>
    private void OnBrowseChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        RefreshCategories();
        _buyDialog.Show(_browse.Buying, _client.Account);
        if (SearchHasFocus) return;
        var name = _browse.Filters.FirstOrDefault(f => f.Key == "name")?.Low ?? string.Empty;
        if (SearchBox() is { } box && box.Text != name) box.Text = name;
    }

    /// <summary>Fills the top bar and the notice from the account the client has.</summary>
    private void Refresh()
    {
        if (_disposed) return;
        var account = _client.Account;
        if (account == null)
        {
            _balance.Text = "—";
            _vaultSpace.Text = string.Empty;
            _character.Text = string.Empty;
            _portrait.Text = string.Empty;
            _notice.Text = _client.Notice;
            _notice.IsVisible = _client.Notice.Length > 0;
            return;
        }
        _balance.Text = $"{account.Balance:N0}";
        _vaultSpace.Text = $"{account.VaultCount:N0} / {account.VaultCapacity:N0}";
        _character.Text = account.Character;
        _portrait.Text = account.Character.Length > 0 ? account.Character.Substring(0, 1) : string.Empty;
        var notice = account.Frozen ? "Your account is banned, so its market is frozen."
            : account.Paused ? "The market is paused, so purchases are stopped for now."
            : _client.Notice;
        _notice.Text = notice;
        _notice.IsVisible = notice.Length > 0;
    }

    private void ApplyMode(MarketLayoutMode mode)
    {
        _browseView.SetMode(mode);
        _sell.SetMode(mode);
        var narrow = mode == MarketLayoutMode.Narrow;
        var wide = mode == MarketLayoutMode.Wide;
        _sideBar.IsVisible = !narrow;
        _wideNav.IsVisible = wide;
        _rail.IsVisible = mode == MarketLayoutMode.Medium;
        _body.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : GridLength.Auto;
        _tabRow.IsVisible = narrow;
        _vaultStat.IsVisible = wide;
        _vaultSpace.IsVisible = wide;
        _character.IsVisible = wide;
        _who.IsVisible = !narrow;
        _who.Padding = wide ? new Thickness(4, 4, 12, 4) : new Thickness(4);
        _activity.SetMode(mode);
        // narrow: an icon opens the full-width search row; the top bar's field is shown otherwise
        _topSearchHost.IsVisible = !narrow;
        _searchIcon.IsVisible = narrow;
        if (!narrow) _narrowSearchHost.IsVisible = false;
        PlaceSearch(narrow);
    }

    private void PlaceSearch(bool inRow)
    {
        if (inRow)
        {
            _topSearchHost.Child = null;
            _narrowSearchHost.Child = _search;
        }
        else
        {
            _narrowSearchHost.Child = null;
            _topSearchHost.Child = _search;
        }
    }

    private Border _who = null!;

    /// <summary>The top bar (search, balance, Vault space, character), which the window puts in its header.</summary>
    public Grid TopBar { get; private set; } = null!;

    /// <summary>A top bar figure: a small-caps name over a value, right-aligned, as BALANCE and VAULT.</summary>
    private static StackPanel Stat(string name, TextBlock value, bool coin)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        if (coin) line.Children.Add(MarketParts.Coin());
        value.TextTrimming = TextTrimming.None;
        line.Children.Add(value);
        var stat = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = MarketParts.Caps(name);
        head.HorizontalAlignment = HorizontalAlignment.Right;
        stat.Children.Add(head);
        stat.Children.Add(line);
        return stat;
    }

    private Control BuildContent()
    {
        _searchIcon.Click += (_, _) => FocusSearch();

        // top bar: the search (or its icon), then on the right the balance, the Vault space and who the window acts for
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_searchIcon);
        right.Children.Add(Stat("Balance", _balance, coin: true));
        right.Children.Add(_vaultStat);
        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        who.Children.Add(_portraitFrame);
        who.Children.Add(_character);
        _who = new Border
        {
            BorderBrush = MarketInk.LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Child = who,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(_who);
        // the window draws the top bar in its header, beside the title
        TopBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 18, Margin = new Thickness(18, 0, 6, 0) };
        TopBar.Children.Add(_topSearchHost);
        Grid.SetColumn(right, 1);
        TopBar.Children.Add(right);

        // side bar (wide): the screens with their counts, then the categories
        AddScreens(_wideNav, NavShape.Row);
        var categoriesHead = MarketParts.Caps("Categories");
        categoriesHead.Margin = new Thickness(10, 16, 10, 6);
        _wideNav.Children.Add(categoriesHead);
        _wideNav.Children.Add(_categoryList);
        // icon rail (medium): the same screens as icons
        AddScreens(_rail, NavShape.Rail);
        var nav = new Panel();
        nav.Children.Add(new ScrollViewer { Content = _wideNav, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        nav.Children.Add(_rail);
        _sideBar.Child = nav;
        Grid.SetColumn(_sideBar, 0);
        _body.Children.Add(_sideBar);

        // tab row (narrow): the same screens across the top of the content
        var tabs = new Grid { Margin = new Thickness(10, 6), ColumnSpacing = 2 };
        AddScreens(tabs, NavShape.Tab);
        _tabRow.Children.Add(new Border
        {
            Child = tabs, BorderBrush = MarketInk.LineSoftBrush, BorderThickness = new Thickness(0, 0, 0, 1),
            Background = DerethPalette.Brush(Color.FromArgb(0x2E, 0, 0, 0)),
        });

        // the screens behind the navigation
        var content = new Grid();
        foreach (var screen in Screens)
        {
            Control control = screen switch { MarketScreen.Browse => _browseView, MarketScreen.Sell => _sell, _ => _activity };
            _screens[screen] = control;
            content.Children.Add(control);
        }

        var column = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetRow(_tabRow, 0);
        column.Children.Add(_tabRow);
        Grid.SetRow(_narrowSearchHost, 1);
        column.Children.Add(_narrowSearchHost);
        Grid.SetRow(content, 2);
        column.Children.Add(content);
        Grid.SetColumn(column, 1);
        _body.Children.Add(column);

        // the body takes the rest of the window's height, so a screen can scroll inside it
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(-6, 0, -6, -6) };
        Grid.SetRow(_notice, 1);
        root.Children.Add(_notice);
        Grid.SetRow(_body, 2);
        root.Children.Add(_body);

        // the buy dialog and the toast lie over everything
        var layers = new Grid();
        layers.Children.Add(root);
        layers.Children.Add(_buyDialog);
        layers.Children.Add(_toast);
        return layers;
    }

    private static readonly (MarketScreen? Screen, string Name, MarketIcon Icon)[] NavItems =
    {
        (MarketScreen.Browse, "Browse", MarketIcon.Browse),
        (null, "Watchlist", MarketIcon.Star),
        (MarketScreen.Sell, "Sell", MarketIcon.Sell),
        (MarketScreen.Activity, "Activity", MarketIcon.Ledger),
    };

    /// <summary>The screens in order, with the disabled Watchlist second, as the mockup draws them: rows, rail icons or tabs.</summary>
    private void AddScreens(Panel panel, NavShape shape)
    {
        var column = 0;
        foreach (var (screen, name, icon) in NavItems)
        {
            var entry = NavEntry(screen, name, icon, shape);
            if (panel is Grid grid)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                Grid.SetColumn(entry, column++);
            }
            panel.Children.Add(entry);
        }
    }

    private MarketButton NavEntry(MarketScreen? screen, string name, MarketIcon icon, NavShape shape)
    {
        var glyph = shape == NavShape.Tab ? null : new MarketGlyph(icon, MarketInk.Muted, shape == NavShape.Rail ? 19 : 17);
        var label = shape == NavShape.Rail ? null : MarketParts.Text(name, MarketInk.TextBrush, shape == NavShape.Tab ? 13 : 14);
        Control? end = null;
        if (screen == MarketScreen.Sell)
        {
            var (badge, count) = MarketParts.Badge();
            _sellBadges.Add((badge, count));
            end = badge;
        }
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        if (glyph != null) content.Children.Add(glyph);
        if (label != null)
        {
            Grid.SetColumn(label, 1);
            content.Children.Add(label);
        }
        if (end != null)
        {
            Grid.SetColumn(end, 2);
            content.Children.Add(end);
        }
        var button = new MarketButton
        {
            Content = content, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(shape == NavShape.Rail ? 8 : 6),
            Height = shape switch { NavShape.Rail => 40, NavShape.Tab => 30, _ => 34 },
            Width = shape == NavShape.Rail ? 40 : double.NaN,
            Padding = shape switch { NavShape.Rail => new Thickness(0), NavShape.Tab => new Thickness(4, 0), _ => new Thickness(10, 0) },
            HorizontalContentAlignment = shape == NavShape.Row ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
        };
        if (shape == NavShape.Rail)
        {
            // the rail's badge sits on the icon's corner
            content.ColumnDefinitions = new ColumnDefinitions("*");
            if (end != null)
            {
                Grid.SetColumn(end, 0);
                end.HorizontalAlignment = HorizontalAlignment.Right;
                end.VerticalAlignment = VerticalAlignment.Top;
                end.Margin = new Thickness(0, -6, -6, 0);
            }
            ToolTip.SetTip(button, name);
        }
        if (shape == NavShape.Tab) content.ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto");
        if (screen is { } target)
        {
            button.Click += (_, _) => Show(target);
            _navEntries.Add((target, button, glyph, label, shape));
        }
        else
        {
            // Watchlist: drawn, not yet live
            button.IsEnabled = false;
            if (label != null) label.Foreground = MarketInk.MutedBrush;
        }
        return button;
    }

    /// <summary>Lights the chosen screen's entry: a gold wash and edge, a gold icon and cream text.</summary>
    private static void Light((MarketScreen Screen, MarketButton Button, MarketGlyph? Glyph, TextBlock? Label, NavShape Shape) entry, bool on)
    {
        var (_, button, glyph, label, shape) = entry;
        button.Background = !on ? Brushes.Transparent : shape == NavShape.Row ? MarketInk.NavOnBrush : MarketInk.GoldWashBrush;
        button.BorderBrush = DerethPalette.GoldBrush;
        button.BorderThickness = !on ? new Thickness(0) : shape == NavShape.Tab ? new Thickness(0, 0, 0, 2) : new Thickness(2, 0, 0, 0);
        if (glyph != null) glyph.Colour = on ? DerethPalette.Gold : MarketInk.Muted;
        if (label != null) label.Foreground = on ? DerethPalette.CreamBrush : shape == NavShape.Tab ? MarketInk.MutedBrush : MarketInk.TextBrush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Changed -= OnClientChanged;
        _browse.Changed -= OnBrowseChanged;
        _browse.Bought -= OnBought;
        if (_sales != null) _sales.Changed -= OnSalesChanged;
        _toast.Hide();
        _browseView.Dispose();
        // the panel owns its client and its browse state, as the Vault's panel owns its Vault client
        _browse.Dispose();
        _sell.Dispose();
        _activity.Dispose();
        _client.Dispose();
    }
}
