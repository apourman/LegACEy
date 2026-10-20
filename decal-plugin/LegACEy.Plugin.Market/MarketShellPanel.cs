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

    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush;
    private static readonly IBrush TextBrush = DerethPalette.TextBrush;
    private static readonly IBrush MutedBrush = DerethPalette.MutedBrush;
    private static readonly IBrush InvalidBrush = DerethPalette.InvalidBrush;

    private readonly MarketClient _client;
    private readonly MarketBrowse _browse;
    private readonly MarketBrowseView _browseView;
    // the Sell screen, owned by the panel: its model is disposed with it
    private readonly SellScreen _sell;
    private readonly ActivityScreen _activity;
    private readonly DerethSearchField _search = new("Search the market…");
    // The search field sits in the top bar, or in its own full-width row under it in a narrow window.
    private readonly Border _topSearchHost = new() { Height = 26, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _narrowSearchHost = new() { Height = 26, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
    private readonly DerethButton _searchIcon = new()
    {
        Width = 34, Height = 26, Content = new DerethGlyphIcon(DerethGlyph.Search, DerethPalette.Gold) { Width = 16, Height = 16 }, IsVisible = false
    };
    private readonly TextBlock _balance = Label("—", GoldBrush, 12);
    private readonly TextBlock _vaultSpace = Label(string.Empty, MutedBrush, 12);
    private readonly TextBlock _character = Label(string.Empty, TextBrush, 12);
    private readonly TextBlock _portrait = new()
    {
        FontSize = 12, Foreground = GoldBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border _portraitFrame;
    private readonly TextBlock _notice = new()
    {
        FontSize = 12, FontFamily = DerethPalette.Body, TextWrapping = TextWrapping.Wrap, Foreground = InvalidBrush,
        Margin = new Thickness(4, 4, 4, 0), IsVisible = false
    };

    // The side bar in a wide window, the icon rail in a medium one and the tab row in a narrow one: the same screens, drawn three ways.
    private readonly StackPanel _sideBar = new() { Width = 168, Spacing = 4 };
    private readonly StackPanel _wideNav = new() { Spacing = 4 };
    // the categories with their counts, from the Browse screen's facets
    private readonly StackPanel _categoryList = new() { Spacing = 3 };
    private readonly StackPanel _rail = new() { Width = 46, Spacing = 6, IsVisible = false };
    private readonly StackPanel _tabRow = new() { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false };
    private readonly List<(MarketScreen Screen, TextBlock Label)> _navLabels = new();
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
        _sell = sell ?? throw new ArgumentNullException(nameof(sell));
        _activity = new ActivityScreen(client);
        _buyDialog = new BuyDialog(browse);
        _portraitFrame = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), BorderBrush = GoldBrush,
            Background = DerethPalette.TealBrush, IsVisible = false, Child = _portrait
        };
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

    /// <summary>The side bar's category entries as drawn, "All categories" first, then each category with its count</summary>
    public IReadOnlyList<string> CategoryTexts => _categoryList.Children.OfType<Button>().Select(b => (b.Content as TextBlock)?.Text ?? string.Empty).ToList();

    /// <summary>Draws the side bar's categories from the facets: all categories, then each with its count; the one being searched is gold</summary>
    private void RefreshCategories()
    {
        _categoryList.Children.Clear();
        var all = new DerethButton { Height = 24, HorizontalContentAlignment = HorizontalAlignment.Left, Content = Label("All categories", TextBrush, 12) };
        all.Click += (_, _) => _browse.SetCategory(null);
        _categoryList.Children.Add(all);
        if (_browse.Facets is not { } facets) return;
        foreach (var category in facets.Categories)
        {
            var name = category.Value;
            var selected = _browse.Filters.Any(f => f.Key == MarketBrowse.CategoryKey && f.Low == name);
            var entry = new DerethButton
            {
                Height = 24, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = Label($"{category.Label}  {category.Count:N0}", selected ? GoldBrush : TextBrush, 12),
            };
            entry.Click += (_, _) => _browse.SetCategory(name);
            _categoryList.Children.Add(entry);
        }
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
        foreach (var (navScreen, label) in _navLabels)
            label.Foreground = navScreen == screen ? GoldBrush : TextBrush;
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
        _balance.Text = $"{account.Balance:N0} MMD";
        _vaultSpace.Text = $"Vault {account.VaultCount:N0} / {account.VaultCapacity:N0}";
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
        _sideBar.Width = wide ? 168 : 46;
        _body.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : GridLength.Auto;
        _tabRow.IsVisible = narrow;
        _vaultSpace.IsVisible = wide;
        _character.IsVisible = wide;
        _portraitFrame.IsVisible = mode == MarketLayoutMode.Medium;
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

    private Control BuildContent()
    {
        _searchIcon.Click += (_, _) => FocusSearch();

        // top bar: the search (or its icon), then the balance on the right, then the Vault space, the character and the portrait
        var topBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_topSearchHost, 0);
        topBar.Children.Add(_topSearchHost);
        Grid.SetColumn(_searchIcon, 1);
        topBar.Children.Add(_searchIcon);
        Grid.SetColumn(_balance, 2);
        topBar.Children.Add(_balance);
        var place = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        place.Children.Add(_vaultSpace);
        place.Children.Add(_character);
        place.Children.Add(_portraitFrame);
        Grid.SetColumn(place, 3);
        topBar.Children.Add(place);

        // side bar (wide): labels, the categories and saved searches, which are drawn but not yet live
        _wideNav.Children.Add(Label("Market", MutedBrush, 11));
        AddScreens(_wideNav, Caption, "Watchlist");
        _wideNav.Children.Add(Label("Categories", MutedBrush, 11));
        _wideNav.Children.Add(_categoryList);
        _wideNav.Children.Add(DisabledNav("Saved searches"));
        _sideBar.Children.Add(_wideNav);

        // icon rail (medium): the same screens as glyphs, with the categories and saved searches in one chip, drawn disabled
        AddScreens(_rail, Glyph, "★");
        _rail.Children.Add(DisabledNav("▾"));
        _sideBar.Children.Add(_rail);
        Grid.SetColumn(_sideBar, 0);
        _body.Children.Add(_sideBar);

        // tab row (narrow): the same screens across the top of the content
        AddScreens(_tabRow, Caption, "Watchlist");

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
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 6 };
        Grid.SetRow(topBar, 0);
        root.Children.Add(topBar);
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

    /// <summary>The screens in order, with the disabled Watchlist entry second, as the mockup draws them.</summary>
    private void AddScreens(Panel panel, Func<MarketScreen, string> text, string watchlist)
    {
        foreach (var screen in Screens)
        {
            panel.Children.Add(NavButton(screen, text(screen), TextBrush));
            if (screen == MarketScreen.Browse) panel.Children.Add(DisabledNav(watchlist));
        }
    }

    private Button NavButton(MarketScreen screen, string text, IBrush brush)
    {
        var label = Label(text, brush, 12);
        _navLabels.Add((screen, label));
        Control content = label;
        if (screen == MarketScreen.Sell)
        {
            // the unseen sales, a green pill after the name, as the mockup draws it
            var count = Label(string.Empty, DerethPalette.Brush(Color.Parse("#062010")), 11);
            count.FontWeight = FontWeight.SemiBold;
            var badge = new Border
            {
                Background = DerethPalette.TealBrush, CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 0), Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Child = count,
            };
            _sellBadges.Add((badge, count));
            var withBadge = new StackPanel { Orientation = Orientation.Horizontal };
            withBadge.Children.Add(label);
            withBadge.Children.Add(badge);
            content = withBadge;
        }
        var button = new DerethButton { Height = 26, Content = content, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => Show(screen);
        return button;
    }

    /// <summary>A navigation entry with no screen yet: drawn, and disabled.</summary>
    private static Button DisabledNav(string text) =>
        new DerethButton { Height = 26, Content = Label(text, MutedBrush, 12), IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Left };

    private static string Caption(MarketScreen screen) => screen.ToString();

    private static string Glyph(MarketScreen screen) => screen switch
    {
        MarketScreen.Browse => "B",
        MarketScreen.Sell => "S",
        MarketScreen.Activity => "A",
        _ => "?",
    };

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
