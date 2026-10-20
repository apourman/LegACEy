using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Market;

/// <summary>
/// The Browse screen's body: the toolbar (filter chips, Add filter, sort, the view toggle, and in a medium or narrow window the category menu), the results
/// as cards or a table, and the detail panel. The categories are the shell's side bar in a wide window. The mode is told to the view by the window; it
/// switches templates on it and never measures itself. Wide: the detail beside the results. Medium: the detail over the results, with a close button.
/// Narrow: one list, and the detail replaces it with a Back button.
/// </summary>
public sealed class MarketBrowseView : UserControl, IDisposable
{
    /// <summary>The results' columns in each mode: three, two, one</summary>
    private static readonly IReadOnlyDictionary<MarketLayoutMode, int> CardColumns = new Dictionary<MarketLayoutMode, int>
    {
        [MarketLayoutMode.Wide] = 3, [MarketLayoutMode.Medium] = 2, [MarketLayoutMode.Narrow] = 1,
    };

    private const double WideDetailWidth = 352;
    private const double MediumDetailWidth = 340;

    private readonly MarketBrowse _model;
    private readonly IGameArtSource _art;
    private readonly Func<string, string, bool>? _tell;
    private readonly EventHandler _modelChanged;
    private readonly Dictionary<(uint Plate, uint Underlay, uint Icon, uint Overlay, uint OverlaySecondary, uint UiEffects), WriteableBitmap?> _icons = new();
    private MarketLayoutMode _mode = MarketLayoutMode.Medium;
    private object? _facetsShown;
    private bool _syncing;
    private bool _disposed;

    // the tell box keeps what was typed, and whether it is open, for as long as the same listing is picked
    private readonly TextBox _tellBox = MarketParts.Field(string.Empty, 30);
    private long? _tellListing;
    private string _tellSeller = string.Empty;

    private readonly Grid _toolbar = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 8 };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly MarketButton _addFilter = MarketParts.Button("+ Add filter", MarketParts.Kind.Chip, 28, MarketInk.MutedBrush);
    private readonly MarketButton _sortButton = MarketParts.Button("Newest first", MarketParts.Kind.Ghost, 28);
    private readonly MarketButton _direction = MarketParts.Button("↓", MarketParts.Kind.Ghost, 28, DerethPalette.GoldBrush);
    private readonly MarketButton _categoryButton = MarketParts.Button("All", MarketParts.Kind.Chip, 28);
    private readonly MarketButton _viewCards = ViewToggle(MarketIcon.Browse);
    private readonly MarketButton _viewTable = ViewToggle(MarketIcon.List);
    private readonly StackPanel _suggestions = new() { Spacing = 0 };
    private readonly Border _suggestionsCard;
    private readonly TextBlock _notice = MarketParts.Text(string.Empty, DerethPalette.InvalidBrush, 12);
    private readonly TextBlock _count = MarketParts.Text(string.Empty, MarketInk.MutedBrush, 12.5);
    private readonly StackPanel _resultsHost = new() { Spacing = 8 };
    private readonly ScrollViewer _resultsScroll;
    private readonly Border _detailHost = new() { IsVisible = false, BorderBrush = MarketInk.LineBrush };
    private readonly Grid _detail = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
    private readonly Grid _content = new();
    private readonly Border _editor;
    private readonly ComboBox _kindBox = Combo(150);
    private readonly TextBox _low = MarketParts.Field("Value", 28, 100);
    private readonly TextBox _high = MarketParts.Field("To", 28, 100);
    private readonly ComboBox _choiceBox = Combo(170);

    public MarketBrowseView(MarketBrowse model, IGameArtSource art, Func<string, string, bool>? tell = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _tell = tell;
        _modelChanged = OnModelChanged;
        _editor = MarketParts.Card(new Panel(), 8, new Thickness(12, 10));
        _editor.IsVisible = false;
        _suggestionsCard = MarketParts.Card(_suggestions, 8, new Thickness(4));
        _resultsScroll = new ScrollViewer { Content = _resultsHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _resultsScroll.ScrollChanged += (_, _) => LoadMoreWhenNearEnd();
        Content = BuildContent();
        _model.Changed += _modelChanged;
        ApplyMode();
        Rebuild();
    }

    /// <summary>The layout the view is drawn in. Set by the window from its width.</summary>
    public MarketLayoutMode Mode => _mode;

    /// <summary>Sets the layout the views draw in. The window calls it when its width changes the mode.</summary>
    public void SetMode(MarketLayoutMode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        ApplyMode();
        Rebuild();
    }

    /// <summary>The count line: how many listings are loaded, and whether more follow</summary>
    public string CountText => _count.Text ?? string.Empty;

    /// <summary>The Add filter editor is open</summary>
    public bool EditorOpen => _editor.IsVisible;

    /// <summary>
    /// Applies the Add filter editor's current entry, as its Apply button does. A range needs one of its two values; the others need theirs.
    /// False, and nothing is added, when the entry has no value.
    /// </summary>
    public bool ApplyEditor()
    {
        if (_model.Facets is not { } facets || _kindBox.SelectedIndex < 0 || _kindBox.SelectedIndex >= facets.Kinds.Count) return false;
        var kind = facets.Kinds[_kindBox.SelectedIndex];
        string low, high = string.Empty;
        switch (kind.Shape)
        {
            case FilterShape.Choice:
                low = _choiceBox.SelectedIndex >= 0 && _choiceBox.SelectedIndex < kind.Options.Count ? kind.Options[_choiceBox.SelectedIndex].Value : string.Empty;
                break;
            case FilterShape.Range:
                low = (_low.Text ?? string.Empty).Trim();
                high = (_high.Text ?? string.Empty).Trim();
                if (low.Length == 0 && high.Length == 0) return false;
                break;
            default:
                low = (_low.Text ?? string.Empty).Trim();
                break;
        }
        if (kind.Shape != FilterShape.Range && low.Length == 0) return false;
        _model.AddFilter(kind.Key, low, high);
        _editor.IsVisible = false;
        return true;
    }

    /// <summary>The chips' texts, as drawn, in order</summary>
    public IReadOnlyList<string> ChipTexts => _chips.Children.OfType<Button>().Where(b => b != _addFilter).Select(ChipText).ToList();

    /// <summary>The detail panel is drawn and open</summary>
    public bool DetailVisible => _detailHost.IsVisible;

    /// <summary>The results list is drawn (narrow hides it while a detail is open)</summary>
    public bool ResultsVisible => _resultsScroll.IsVisible;

    /// <summary>The Tell box's text, as typed</summary>
    public string TellText => _tellBox.Text ?? string.Empty;

    /// <summary>The Tell box is open</summary>
    public bool TellOpen => _tellBox.IsVisible;

    /// <summary>The buy button in the detail panel: opens the buy dialog; disabled on your own listing</summary>
    public Button? BuyButton => _detail.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Tag as string == "buy");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Changed -= _modelChanged;
    }

    private void OnModelChanged(object? sender, EventArgs e) => Rebuild();

    private Control BuildContent()
    {
        _addFilter.Click += (_, _) => { _editor.IsVisible = !_editor.IsVisible; Rebuild(); };
        _direction.Click += (_, _) => _model.SetSort(_model.Sort);
        _viewCards.Click += (_, _) => _model.SetView(BrowseView.Cards);
        _viewTable.Click += (_, _) => _model.SetView(BrowseView.Table);
        _sortButton.Click += (_, _) => OpenMenu(_sortButton, (_model.Facets?.Sorts ?? Array.Empty<SortOption>()).Select(o => (o.Label, (Action)(() => _model.SetSort(o.Value)))));
        _categoryButton.Click += (_, _) => OpenMenu(_categoryButton, new[] { ("All categories", (Action)(() => _model.SetCategory(null))) }
            .Concat((_model.Facets?.Categories ?? Array.Empty<FilterOption>()).Select(c => ($"{c.Label}  {c.Count:N0}", (Action)(() => _model.SetCategory(c.Value))))));
        _kindBox.SelectionChanged += (_, _) => { if (!_syncing) ShowEditorFields(); };
        _tellBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _tell?.Invoke(_tellSeller, (_tellBox.Text ?? string.Empty).Trim()) == true)
                _tellBox.Text = string.Empty;
        };

        var apply = MarketParts.Button("Apply", MarketParts.Kind.Red, 28);
        apply.Click += (_, _) => ApplyEditor();
        var cancel = MarketParts.Button("Cancel", MarketParts.Kind.Ghost, 28);
        cancel.Click += (_, _) => { _editor.IsVisible = false; Rebuild(); };
        var editorRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        editorRow.Children.Add(_kindBox);
        editorRow.Children.Add(_low);
        editorRow.Children.Add(_high);
        editorRow.Children.Add(_choiceBox);
        editorRow.Children.Add(apply);
        editorRow.Children.Add(cancel);
        _editor.Child = editorRow;

        // toolbar: the category menu (not wide), the chips and Add filter, then the sort, its direction and the cards/table toggle
        _toolbar.Children.Add(_categoryButton);
        _chips.Children.Add(_addFilter);
        Grid.SetColumn(_chips, 1);
        _toolbar.Children.Add(_chips);
        var sortIcon = new MarketGlyph(MarketIcon.Sort, DerethPalette.Gold, 14);
        var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var sortLabel = (Control)_sortButton.Content!;
        _sortButton.Content = sortRow;
        sortRow.Children.Add(sortIcon);
        sortRow.Children.Add(sortLabel);
        Grid.SetColumn(_sortButton, 2);
        _toolbar.Children.Add(_sortButton);
        Grid.SetColumn(_direction, 3);
        _toolbar.Children.Add(_direction);
        var toggle = new Border
        {
            BorderBrush = MarketInk.LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _viewCards, _viewTable } },
        };
        Grid.SetColumn(toggle, 4);
        _toolbar.Children.Add(toggle);

        var top = new StackPanel { Spacing = 10 };
        top.Children.Add(_toolbar);
        top.Children.Add(_editor);
        top.Children.Add(_suggestionsCard);
        top.Children.Add(_notice);
        top.Children.Add(_count);

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12, Margin = new Thickness(20, 16, 20, 0) };
        main.Children.Add(top);
        Grid.SetRow(_resultsScroll, 1);
        main.Children.Add(_resultsScroll);
        _resultsScroll.Padding = new Thickness(0, 0, 0, 16);

        // columns: the results, and the detail beside them (wide) or over them (medium) or in their place (narrow)
        _content.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _content.Children.Add(main);
        _detailHost.Child = _detail;
        _content.Children.Add(_detailHost);
        return _content;
    }

    private static MarketButton ViewToggle(MarketIcon icon) => new()
    {
        Width = 34, Height = 28, CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0),
        Content = new MarketGlyph(icon, MarketInk.Muted, 15),
    };

    private static ComboBox Combo(double width) => new()
    {
        Width = width, Height = 28, MinHeight = 0, FontSize = 12.5, FontFamily = DerethPalette.Body, Foreground = MarketInk.TextBrush,
        Background = MarketInk.SunkBrush, BorderBrush = MarketInk.LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 0), VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>A drop-down list of choices under a toolbar button, in the base theme's dark menu.</summary>
    private static void OpenMenu(Control anchor, IEnumerable<(string Label, Action Pick)> items)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (label, pick) in items)
        {
            var item = new MenuItem { Header = label, FontSize = 12.5, FontFamily = DerethPalette.Body };
            item.Click += (_, _) => pick();
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private void ApplyMode()
    {
        var wide = _mode == MarketLayoutMode.Wide;
        var medium = _mode == MarketLayoutMode.Medium;
        _categoryButton.IsVisible = !wide;
        _detailHost.Width = wide ? WideDetailWidth : medium ? MediumDetailWidth : double.NaN;
        _detailHost.HorizontalAlignment = medium ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _detailHost.BorderThickness = _mode == MarketLayoutMode.Narrow ? new Thickness(0) : new Thickness(1, 0, 0, 0);
        _detailHost.Background = MarketInk.Vertical(Color.Parse("#27221A"), Color.Parse("#15120D"));
        _detailHost.BoxShadow = medium ? BoxShadows.Parse("-24 0 40 0 #A6000000") : default;
        _sortButton.IsVisible = _mode != MarketLayoutMode.Narrow;
        // medium: the detail lies over the results in the same cell; wide: beside them; narrow: in the same cell, with the results hidden
        Grid.SetColumn(_detailHost, wide ? 1 : 0);
        _content.ColumnDefinitions[1].Width = wide ? GridLength.Auto : new GridLength(0);
    }

    /// <summary>Redraws everything from the model</summary>
    private void Rebuild()
    {
        if (_disposed) return;
        _syncing = true;
        try
        {
            var facets = _model.Facets;
            if (!ReferenceEquals(facets, _facetsShown) && facets != null)
            {
                _facetsShown = facets;
                _kindBox.ItemsSource = facets.Kinds.Select(k => k.Label).ToList();
                _kindBox.SelectedIndex = facets.Kinds.Count > 0 ? 0 : -1;
                ShowEditorFields();
            }
            var sort = facets?.Sorts.FirstOrDefault(s => s.Value == _model.Sort);
            SetLabel(_sortButton, $"{sort?.Label ?? "Newest"} ▾");
            var category = facets?.Categories.FirstOrDefault(c => _model.Filters.Any(f => f.Key == MarketBrowse.CategoryKey && f.Low == c.Value));
            SetLabel(_categoryButton, $"{category?.Label ?? "All"} ▾");
            SetLabel(_direction, _model.Descending ? "↓" : "↑");
            LightToggle(_viewCards, _model.View == BrowseView.Cards);
            LightToggle(_viewTable, _model.View == BrowseView.Table);

            RebuildChips(facets);
            RebuildSuggestions();
            RebuildResults();
            RebuildDetail();
            _notice.Text = _model.Notice;
            _notice.IsVisible = _model.Notice.Length > 0;
            _count.Text = CountLine();
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void SetLabel(MarketButton button, string text)
    {
        var label = button.Content as TextBlock ?? (button.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();
        if (label != null) label.Text = text;
    }

    private static void LightToggle(MarketButton button, bool on)
    {
        button.Background = on ? MarketInk.GoldWashBrush : Brushes.Transparent;
        ((MarketGlyph)button.Content!).Colour = on ? DerethPalette.Gold : MarketInk.Muted;
    }

    private void RebuildChips(MarketFacets? facets)
    {
        foreach (var old in _chips.Children.Where(c => c != _addFilter).ToList()) _chips.Children.Remove(old);
        var at = 0;
        foreach (var filter in _model.Filters)
        {
            // the category is the side bar's (or the category menu's), not a chip
            if (filter.Key == MarketBrowse.CategoryKey) continue;
            var kind = facets?.Kinds.FirstOrDefault(k => k.Key == filter.Key);
            var chip = MarketParts.Button(ChipLabel(kind, filter) + "  ×", MarketParts.Kind.ChipOn, 28);
            chip.Tag = filter.Key;
            var key = filter.Key;
            chip.Click += (_, _) => _model.RemoveFilter(key);
            _chips.Children.Insert(at++, chip);
        }
    }

    /// <summary>The chip's words: a minimum "150+", a range "1000–2000", "≤ 2000" or "≥ 1000", a choice by its option's label</summary>
    private static string ChipLabel(FilterKind? kind, ActiveFilter filter)
    {
        var label = kind?.Label ?? filter.Key;
        switch (kind?.Shape)
        {
            case FilterShape.Minimum:
                return $"{label} {filter.Low}+";
            case FilterShape.Range:
                if (filter.Low.Length == 0) return $"{label} ≤ {filter.High}";
                if (filter.High.Length == 0) return $"{label} ≥ {filter.Low}";
                return $"{label} {filter.Low}–{filter.High}";
            case FilterShape.Choice:
                return $"{label}: {kind.Options.FirstOrDefault(o => o.Value == filter.Low)?.Label ?? filter.Low}";
            default:
                return $"{label}: {filter.Low}";
        }
    }

    private void RebuildSuggestions()
    {
        _suggestions.Children.Clear();
        foreach (var name in _model.Suggestions)
        {
            var suggestion = MarketParts.Button(name, MarketParts.Kind.Quiet, 28, MarketInk.TextBrush);
            suggestion.HorizontalAlignment = HorizontalAlignment.Stretch;
            suggestion.HorizontalContentAlignment = HorizontalAlignment.Left;
            suggestion.Click += (_, _) => { _model.Search(name); _model.ClearSuggestions(); };
            _suggestions.Children.Add(suggestion);
        }
        _suggestionsCard.IsVisible = _suggestions.Children.Count > 0;
    }

    private void RebuildResults()
    {
        _resultsHost.Children.Clear();
        // narrow with a detail open: the detail replaces the list
        _resultsScroll.IsVisible = !(_mode == MarketLayoutMode.Narrow && _model.SelectedId != null);
        if (_model.View == BrowseView.Table && _mode != MarketLayoutMode.Narrow)
        {
            _resultsHost.Children.Add(TableControl());
            return;
        }
        if (_mode == MarketLayoutMode.Narrow)
        {
            foreach (var card in _model.Results) _resultsHost.Children.Add(RowControl(card));
            return;
        }
        var columns = CardColumns[_mode];
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var index = 0;
        foreach (var card in _model.Results)
        {
            if (index % columns == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var control = CardControl(card);
            Grid.SetColumn(control, index % columns);
            Grid.SetRow(control, index / columns);
            grid.Children.Add(control);
            index++;
        }
        _resultsHost.Children.Add(grid);
    }

    private void RebuildDetail()
    {
        _detail.Children.Clear();
        var open = _model.SelectedId != null;
        _detailHost.IsVisible = open;
        if (!open)
        {
            _tellListing = null;
            return;
        }

        // the tell box keeps its text while the same listing is picked; a different listing starts it closed and empty
        if (_tellListing != _model.SelectedId)
        {
            _tellListing = _model.SelectedId;
            _tellBox.Text = string.Empty;
            _tellBox.IsVisible = false;
        }

        var listing = _model.Detail?.Listing ?? _model.Results.FirstOrDefault(c => c.Id == _model.SelectedId);
        var head = new StackPanel();
        Grid.SetRow(head, 0);
        _detail.Children.Add(head);
        if (_mode == MarketLayoutMode.Narrow)
        {
            // narrow: the detail is the page, and Back returns to the list
            var back = MarketParts.Button($"‹ Back to {_model.Results.Count:N0} results", MarketParts.Kind.Quiet, 40, DerethPalette.GoldBrush);
            back.HorizontalAlignment = HorizontalAlignment.Stretch;
            back.HorizontalContentAlignment = HorizontalAlignment.Left;
            back.Padding = new Thickness(16, 0);
            back.CornerRadius = new CornerRadius(0);
            back.Click += (_, _) => _model.Back();
            head.Children.Add(back);
            head.Children.Add(MarketParts.Rule());
        }
        if (listing == null)
        {
            head.Children.Add(new Border { Padding = new Thickness(20, 16), Child = MarketParts.Text("Loading the listing…", MarketInk.MutedBrush, 12) });
            return;
        }

        // head: the icon, the name and what it is
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14, Margin = new Thickness(20, 16, 20, 12) };
        title.Children.Add(MarketParts.IconTile(Icon(listing), 56));
        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = MarketParts.Text(listing.Name, DerethPalette.CreamBrush, 19, title: true);
        name.TextWrapping = TextWrapping.Wrap;
        names.Children.Add(name);
        names.Children.Add(MarketParts.Text($"{Subtitle(listing)} · listed {Ago(listing.Listed)} ago", MarketInk.MutedBrush, 12));
        Grid.SetColumn(names, 1);
        title.Children.Add(names);
        if (_mode == MarketLayoutMode.Medium)
        {
            // medium: the detail is over the results, so it closes from here
            var close = MarketParts.Button("✕", MarketParts.Kind.Ghost, 28, MarketInk.MutedBrush);
            close.Width = 28;
            close.Padding = new Thickness(0);
            close.CornerRadius = new CornerRadius(14);
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Click += (_, _) => _model.Back();
            Grid.SetColumn(close, 2);
            title.Children.Add(close);
        }
        head.Children.Add(title);
        head.Children.Add(MarketParts.Rule());

        // body: the seller, then the appraisal
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(20, 10, 20, 12) };
        var sellerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        sellerRow.Children.Add(Avatar(22));
        var sellerName = MarketParts.Text(listing.Own ? "Your listing" : listing.Seller, listing.Own ? MarketInk.LitTextBrush : MarketInk.TextBrush, 12.5);
        Grid.SetColumn(sellerName, 1);
        sellerRow.Children.Add(sellerName);
        body.Children.Add(sellerRow);
        if (!listing.Own)
        {
            var seller = listing.Seller;
            _tellSeller = seller;
            var tell = MarketParts.Button("Tell", MarketParts.Kind.Ghost, 26);
            tell.Click += (_, _) => _tellBox.IsVisible = !_tellBox.IsVisible;
            _tellBox.Watermark = $"Tell {seller}…";
            Grid.SetColumn(tell, 2);
            sellerRow.Children.Add(tell);
            var more = MarketParts.Button($"More from {seller}", MarketParts.Kind.Ghost, 26);
            more.Click += (_, _) => _model.ShowSeller(seller);
            Grid.SetColumn(more, 3);
            sellerRow.Children.Add(more);
            // the tell box outlives each redraw: it leaves the old body first
            (_tellBox.Parent as Panel)?.Children.Remove(_tellBox);
            body.Children.Add(_tellBox);
        }

        if (_model.Detail is { } detail && detail.Listing.Id == listing.Id)
        {
            body.Children.Add(MarketParts.SectionHead("Appraisal"));
            var pairs = detail.Lines.Select(SplitLine).ToList();
            if (detail.Spells.Count > 0) pairs.Add(("Spells", string.Join(", ", detail.Spells.Select(spell => spell.Name))));
            body.Children.Add(KeyValues(pairs));
        }
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        _detail.Children.Add(scroll);

        // foot: the big price, what it buys, and Buy now
        var foot = new StackPanel { Spacing = 10 };
        var priceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        priceRow.Children.Add(MarketParts.Price(listing.Price.ToString("N0"), big: true));
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Right(MarketParts.Text($"Whole stack × {listing.Quantity:N0}", MarketInk.MutedBrush, 11.5)));
        if (!listing.Own && Balance?.Invoke() is long balance)
            stack.Children.Add(Right(MarketParts.Text($"Balance after {balance - listing.Price:N0}", MarketInk.MutedBrush, 11.5)));
        Grid.SetColumn(stack, 1);
        priceRow.Children.Add(stack);
        foot.Children.Add(priceRow);
        var buy = MarketParts.Button("Buy now", MarketParts.Kind.Go, 34);
        buy.Tag = "buy";
        buy.IsEnabled = !listing.Own;
        buy.HorizontalAlignment = HorizontalAlignment.Stretch;
        var buyId = listing.Id;
        buy.Click += (_, _) => _model.OpenBuy(buyId);
        // the watch star: drawn, not yet live
        var watch = new MarketButton { Width = 42, Height = 34, IsEnabled = false, Content = new MarketGlyph(MarketIcon.StarFilled, DerethPalette.Gold, 14) };
        var buyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        buyRow.Children.Add(buy);
        Grid.SetColumn(watch, 1);
        buyRow.Children.Add(watch);
        foot.Children.Add(buyRow);
        var footFrame = new Border
        {
            Padding = new Thickness(20, 12, 20, 14), BorderBrush = DerethPalette.GoldDarkBrush, BorderThickness = new Thickness(0, 1, 0, 0),
            Background = DerethPalette.Brush(Color.FromArgb(0x40, 0, 0, 0)), Child = foot,
        };
        Grid.SetRow(footFrame, 2);
        _detail.Children.Add(footFrame);
    }

    private static TextBlock Right(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Right;
        return text;
    }

    /// <summary>The account's balance, for the detail's "Balance after"; null while it isn't known.</summary>
    public Func<long?>? Balance { get; set; }

    /// <summary>An appraisal line "Label: value" as its two columns; a line with no label is all value.</summary>
    private static (string Key, string Value) SplitLine(string line)
    {
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 ? (line.Substring(0, colon), line.Substring(colon + 2)) : (string.Empty, line);
    }

    /// <summary>The appraisal's label and value columns: muted labels 104 px wide, values wrapping beside them.</summary>
    private static Grid KeyValues(IReadOnlyList<(string Key, string Value)> pairs)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("104,*"), RowSpacing = 5 };
        for (var i = 0; i < pairs.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = MarketParts.Text(pairs[i].Key, MarketInk.MutedBrush, 12.5);
            key.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(key, i);
            grid.Children.Add(key);
            var value = MarketParts.Text(pairs[i].Value, MarketInk.TextBrush, 12.5);
            value.TextWrapping = TextWrapping.Wrap;
            value.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        return grid;
    }

    /// <summary>A person's round face, as the seller's and the character's.</summary>
    private static Border Avatar(double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(1), BorderBrush = DerethPalette.GoldDarkBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new RadialGradientBrush
        {
            Center = new RelativePoint(0.4, 0.35, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.4, 0.35, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#C89A78"), 0), new GradientStop(Color.Parse("#5A3A20"), 1) }
        },
    };

    /// <summary>The stat tags under a name: workmanship in gold, then level and the key stat.</summary>
    private static StackPanel Tags(ListingCard card)
    {
        var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, ClipToBounds = true };
        if (card.Workmanship is int work) tags.Children.Add(MarketParts.GoldTag($"Wk {work}"));
        if (card.Level is int level) tags.Children.Add(MarketParts.Tag($"Lvl {level}"));
        if (card.Summary.Length > 0) tags.Children.Add(MarketParts.Tag(card.Summary));
        return tags;
    }

    private Control SellerLine(ListingCard card, double size = 11.5) => MarketParts.Text(
        card.Own ? "Your listing" : $"{card.Seller} · {Ago(card.Listed)}", card.Own ? MarketInk.LitTextBrush : MarketInk.MutedBrush, size);

    private Control CardControl(ListingCard card)
    {
        var selected = _model.SelectedId == card.Id;
        var names = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var name = MarketParts.Text(card.Name, DerethPalette.CreamBrush, 13.5);
        name.TextWrapping = TextWrapping.Wrap;
        name.MaxLines = 2;
        names.Children.Add(name);
        names.Children.Add(MarketParts.Text(Subtitle(card), MarketInk.MutedBrush, 11.5));
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        top.Children.Add(MarketParts.IconTile(Icon(card), 44));
        Grid.SetColumn(names, 1);
        top.Children.Add(names);
        var star = new MarketGlyph(MarketIcon.Star, MarketInk.Muted, 14) { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(star, 2);
        top.Children.Add(star);

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        var price = MarketParts.Price(card.Price.ToString("N0"));
        if (card.Quantity > 1) price.Children.Add(MarketParts.Text($"× {card.Quantity:N0}", MarketInk.MutedBrush, 11.5));
        bottom.Children.Add(price);
        var seller = SellerLine(card);
        seller.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(seller, 1);
        bottom.Children.Add(seller);

        var stackPanel = new StackPanel { Spacing = 8 };
        stackPanel.Children.Add(top);
        stackPanel.Children.Add(Tags(card));
        stackPanel.Children.Add(new Border
        {
            BorderBrush = MarketInk.LineSoftBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = bottom,
        });

        var border = MarketParts.Card(stackPanel);
        if (selected)
        {
            border.BorderBrush = MarketInk.LitBrush;
            border.BorderThickness = new Thickness(2);
            border.Padding = new Thickness(11);
            border.BoxShadow = BoxShadows.Parse("0 0 16 0 #2E3FCF5F");
        }
        var id = card.Id;
        border.PointerPressed += (_, _) => _model.Select(id);
        return border;
    }

    /// <summary>A narrow window's result: one line with the icon, the name and its tags, and the price and seller on the right.</summary>
    private Control RowControl(ListingCard card)
    {
        var names = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(MarketParts.Text(card.Name, DerethPalette.CreamBrush, 13.5));
        names.Children.Add(Tags(card));
        var end = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var price = MarketParts.Price(card.Price.ToString("N0"));
        price.HorizontalAlignment = HorizontalAlignment.Right;
        end.Children.Add(price);
        var seller = MarketParts.Text(card.Own ? "Your listing" : card.Seller, card.Own ? MarketInk.LitTextBrush : MarketInk.MutedBrush, 11.5);
        seller.HorizontalAlignment = HorizontalAlignment.Right;
        end.Children.Add(seller);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(MarketParts.IconTile(Icon(card), 40));
        Grid.SetColumn(names, 1);
        row.Children.Add(names);
        Grid.SetColumn(end, 2);
        row.Children.Add(end);
        var border = MarketParts.Card(row, 8, new Thickness(12, 10));
        var id = card.Id;
        border.PointerPressed += (_, _) => _model.Select(id);
        return border;
    }

    private Control TableControl()
    {
        var columns = Columns;
        var widths = string.Join(",", columns.Select(c => c.Width));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), ColumnSpacing = 12, Margin = new Thickness(10, 0, 10, 4) };
        for (var i = 0; i < columns.Count; i++)
        {
            var sort = columns[i].Sort;
            Control header;
            var heading = columns[i].Header.ToUpperInvariant();
            if (sort == null)
            {
                header = MarketParts.Caps(heading);
            }
            else
            {
                var arrow = _model.Sort == sort ? (_model.Descending ? " ↓" : " ↑") : string.Empty;
                var button = MarketParts.Button(heading + arrow, MarketParts.Kind.Quiet, 24, _model.Sort == sort ? DerethPalette.GoldBrush : MarketInk.MutedBrush);
                var label = (TextBlock)button.Content!;
                label.FontSize = 10.5;
                label.LetterSpacing = 1;
                label.FontFamily = DerethPalette.Body;
                button.Padding = new Thickness(0);
                button.HorizontalAlignment = columns[i].Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                var value = sort;
                button.Click += (_, _) => _model.SetSort(value);
                header = button;
            }
            if (columns[i].Right) header.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(header, i);
            grid.Children.Add(header);
        }
        var table = new StackPanel();
        table.Children.Add(grid);
        table.Children.Add(MarketParts.Rule());
        foreach (var card in _model.Results)
            table.Children.Add(TableRow(card, columns, widths));
        return table;
    }

    private Control TableRow(ListingCard card, IReadOnlyList<Column> columns, string widths)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), ColumnSpacing = 12 };
        for (var i = 0; i < columns.Count; i++)
        {
            Control cell = columns[i].Kind switch
            {
                CellKind.Name => NameCell(card),
                CellKind.Price => MarketParts.Price(card.Price.ToString("N0")),
                _ => MarketParts.Text(columns[i].Cell(card), columns[i].Brush(card), 13),
            };
            if (columns[i].Right) cell.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        var selected = _model.SelectedId == card.Id;
        var border = new Border
        {
            Child = grid, Padding = new Thickness(selected ? 8 : 10, 6, 10, 6), BorderBrush = selected ? MarketInk.LitBrush : MarketInk.LineSoftBrush,
            BorderThickness = selected ? new Thickness(2, 0, 0, 1) : new Thickness(0, 0, 0, 1),
            Background = selected ? DerethPalette.Brush(MarketInk.Lit.WithAlpha(0x14)) : Brushes.Transparent,
        };
        var id = card.Id;
        border.PointerPressed += (_, _) => _model.Select(id);
        return border;
    }

    /// <summary>The table's item cell: the icon, the name, and the quantity under it for a stack</summary>
    private Control NameCell(ListingCard card)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(MarketParts.Text(card.Name, DerethPalette.CreamBrush, 13));
        if (card.Quantity > 1) text.Children.Add(MarketParts.Text($"× {card.Quantity:N0}", MarketInk.MutedBrush, 11.5));
        var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        cell.Children.Add(MarketParts.IconTile(Icon(card), 30));
        Grid.SetColumn(text, 1);
        cell.Children.Add(text);
        return cell;
    }

    private enum CellKind { Text, Name, Price }

    /// <summary>The table's columns: the portal's, with a width each and the sort it asks for.</summary>
    private static IReadOnlyList<Column> Columns { get; } = new[]
    {
        new Column("Item", "name", "2.4*", c => c.Name, _ => DerethPalette.CreamBrush, CellKind.Name),
        new Column("Type", null, "1.3*", c => c.TypeLabel, _ => MarketInk.MutedBrush),
        new Column("Wk", "workmanship", "44", c => c.Workmanship?.ToString() ?? "—", _ => MarketInk.TextBrush),
        new Column("Lvl", "level", "44", c => c.Level?.ToString() ?? "—", _ => MarketInk.TextBrush),
        new Column("Key stat", null, "1.5*", c => c.Summary, _ => MarketInk.TextBrush),
        new Column("Price", "price", "90", c => c.Price.ToString("N0"), _ => DerethPalette.CreamBrush, CellKind.Price, Right: true),
        new Column("Seller", "seller", "*", c => c.Own ? "You" : c.Seller, c => c.Own ? MarketInk.LitTextBrush : MarketInk.TextBrush),
        new Column("Listed", "newest", "64", c => Ago(c.Listed), _ => MarketInk.MutedBrush, Right: true),
    };

    private sealed record Column(string Header, string? Sort, string Width, Func<ListingCard, string> Cell, Func<ListingCard, IBrush> Brush,
        CellKind Kind = CellKind.Text, bool Right = false);

    /// <summary>A listing's icon, from the game's art; the buy dialog draws it too.</summary>
    public Control IconFor(ListingCard card) => Icon(card);

    private Control Icon(ListingCard card)
    {
        var key = (card.Plate, card.Underlay, card.Icon, card.Overlay, card.OverlaySecondary, card.UiEffects);
        if (!_icons.TryGetValue(key, out var bitmap))
        {
            bitmap = GameArtImageExtension.CreateBitmap(ItemIcon.Draw(_art, card.Underlay, card.Icon, card.Overlay, card.OverlaySecondary, card.UiEffects, card.Plate));
            _icons[key] = bitmap;
        }
        return DerethItemCells.Icon(bitmap);
    }

    private void ShowEditorFields()
    {
        if (_model.Facets is not { } facets || _kindBox.SelectedIndex < 0 || _kindBox.SelectedIndex >= facets.Kinds.Count) return;
        var kind = facets.Kinds[_kindBox.SelectedIndex];
        var shape = kind.Shape;
        // a choice offers the kind's own options
        _choiceBox.ItemsSource = kind.Options.Select(o => o.Label).ToList();
        _choiceBox.SelectedIndex = kind.Options.Count > 0 ? 0 : -1;
        _choiceBox.IsVisible = shape == FilterShape.Choice;
        _low.IsVisible = shape != FilterShape.Choice;
        _high.IsVisible = shape == FilterShape.Range;
        _low.Watermark = shape == FilterShape.Range ? "From" : "Value";
    }

    private void LoadMoreWhenNearEnd()
    {
        if (!_model.HasMore || _model.Loading) return;
        var remaining = _resultsScroll.Extent.Height - _resultsScroll.Viewport.Height - _resultsScroll.Offset.Y;
        if (remaining < 160) _model.LoadMore();
    }

    private string CountLine()
    {
        var text = $"{_model.Results.Count:N0} listings · prices are for the whole stack";
        if (_model.Loading) text += " · loading…";
        else if (_model.HasMore) text += " · scroll for more";
        return text;
    }

    private static string Subtitle(ListingCard card) =>
        string.IsNullOrEmpty(card.Material) ? card.TypeLabel : $"{card.TypeLabel} · {card.Material}";

    private static string Ago(DateTimeOffset listed)
    {
        var age = DateTimeOffset.UtcNow - listed;
        if (age.TotalMinutes < 60) return $"{Math.Max(1, (int)age.TotalMinutes)} m";
        if (age.TotalHours < 48) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }

    private static string ChipText(Button button) => button.Content is TextBlock text ? text.Text ?? string.Empty : button.Content?.ToString() ?? string.Empty;
}
