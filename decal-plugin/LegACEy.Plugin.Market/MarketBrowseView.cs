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

    private const double WideDetailWidth = 300;
    private const double MediumDetailWidth = 320;

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
    private readonly TextBox _tellBox = new() { MinWidth = 120, IsVisible = false };
    private long? _tellListing;
    private string _tellSeller = string.Empty;

    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly Button _addFilter = Pill("+ Add filter");
    private readonly ComboBox _sortBox = new() { MinWidth = 150, Height = 26 };
    private readonly Button _direction = Pill("▾");
    private readonly ComboBox _categoryBox = new() { MinWidth = 150, Height = 26 };
    private readonly Button _viewCards = Pill("Cards");
    private readonly Button _viewTable = Pill("Table");
    private readonly StackPanel _suggestions = new() { Spacing = 2 };
    private readonly TextBlock _notice = Text(string.Empty, DerethPalette.InvalidBrush, 12);
    private readonly TextBlock _count = Text(string.Empty, DerethPalette.MutedBrush, 12);
    private readonly StackPanel _resultsHost = new() { Spacing = 6 };
    private readonly ScrollViewer _resultsScroll;
    private readonly Border _detailHost = new()
    {
        BorderThickness = new Thickness(1), BorderBrush = DerethPalette.GrooveEdgeBrush, Background = DerethPalette.Brush(DerethPalette.Navy),
        Padding = new Thickness(10), IsVisible = false,
    };
    private readonly StackPanel _detail = new() { Spacing = 6 };
    private readonly Grid _content = new();
    private readonly Border _editor = new() { IsVisible = false, Padding = new Thickness(8), BorderThickness = new Thickness(1), BorderBrush = DerethPalette.GrooveEdgeBrush };
    private readonly ComboBox _kindBox = new() { MinWidth = 130, Height = 26 };
    private readonly TextBox _low = Input("Value");
    private readonly TextBox _high = Input("To");
    private readonly ComboBox _choiceBox = new() { MinWidth = 150, Height = 26 };

    public MarketBrowseView(MarketBrowse model, IGameArtSource art, Func<string, string, bool>? tell = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _tell = tell;
        _modelChanged = OnModelChanged;
        _resultsScroll = new ScrollViewer { Content = _resultsHost };
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
    public IReadOnlyList<string> ChipTexts => _chips.Children.OfType<Button>().Select(ChipText).ToList();

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
        _sortBox.SelectionChanged += (_, _) => { if (!_syncing && _sortBox.SelectedIndex >= 0 && _model.Facets is { } f) _model.SetSort(f.Sorts[_sortBox.SelectedIndex].Value); };
        _categoryBox.SelectionChanged += (_, _) => { if (!_syncing) _model.SetCategory(CategoryAt(_categoryBox.SelectedIndex)); };
        _kindBox.SelectionChanged += (_, _) => { if (!_syncing) ShowEditorFields(); };
        _tellBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _tell?.Invoke(_tellSeller, (_tellBox.Text ?? string.Empty).Trim()) == true)
                _tellBox.Text = string.Empty;
        };

        var apply = Pill("Apply");
        apply.Click += (_, _) => ApplyEditor();
        var cancel = Pill("Cancel");
        cancel.Click += (_, _) => { _editor.IsVisible = false; Rebuild(); };
        var editorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        editorRow.Children.Add(_kindBox);
        editorRow.Children.Add(_low);
        editorRow.Children.Add(_high);
        editorRow.Children.Add(_choiceBox);
        editorRow.Children.Add(apply);
        editorRow.Children.Add(cancel);
        _editor.Child = editorRow;

        _toolbar.Children.Add(_addFilter);
        _toolbar.Children.Add(_direction);
        _toolbar.Children.Add(_sortBox);
        _toolbar.Children.Add(_categoryBox);
        _toolbar.Children.Add(_viewCards);
        _toolbar.Children.Add(_viewTable);

        var top = new StackPanel { Spacing = 4 };
        top.Children.Add(_toolbar);
        top.Children.Add(_chips);
        top.Children.Add(_editor);
        top.Children.Add(_suggestions);
        top.Children.Add(_notice);
        top.Children.Add(_count);

        // columns: the results, and the detail beside them (wide) or over them (medium) or in their place (narrow)
        _content.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Grid.SetColumn(_resultsScroll, 0);
        _content.Children.Add(_resultsScroll);
        _detailHost.Child = new ScrollViewer { Content = _detail, Background = DerethPalette.Brush(DerethPalette.Navy) };
        _content.Children.Add(_detailHost);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(top, 0);
        root.Children.Add(top);
        Grid.SetRow(_content, 1);
        root.Children.Add(_content);
        return root;
    }

    private void ApplyMode()
    {
        var wide = _mode == MarketLayoutMode.Wide;
        _categoryBox.IsVisible = !wide;
        _detailHost.Width = wide ? WideDetailWidth : _mode == MarketLayoutMode.Medium ? MediumDetailWidth : double.NaN;
        _detailHost.HorizontalAlignment = _mode == MarketLayoutMode.Medium ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        // medium: the detail lies over the results in the same cell; wide: beside them; narrow: in the same cell, with the results hidden
        if (wide)
        {
            Grid.SetColumn(_detailHost, 1);
            _content.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _content.ColumnDefinitions[1].Width = GridLength.Auto;
        }
        else
        {
            Grid.SetColumn(_detailHost, 0);
            _content.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _content.ColumnDefinitions[1].Width = new GridLength(0);
        }
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
                _sortBox.ItemsSource = facets.Sorts.Select(s => s.Label).ToList();
                _kindBox.ItemsSource = facets.Kinds.Select(k => k.Label).ToList();
                _kindBox.SelectedIndex = facets.Kinds.Count > 0 ? 0 : -1;
                ShowEditorFields();
                _categoryBox.ItemsSource = new[] { "All categories" }.Concat(facets.Categories.Select(c => $"{c.Label} ({c.Count:N0})")).ToList();
            }
            if (facets != null)
            {
                _sortBox.SelectedIndex = facets.Sorts.ToList().FindIndex(s => s.Value == _model.Sort);
                _categoryBox.SelectedIndex = CategoryIndex(facets);
            }
            _direction.Content = Text(_model.Descending ? "↓" : "↑", DerethPalette.GoldBrush, 12);
            _viewCards.Opacity = _model.View == BrowseView.Cards ? 1 : 0.6;
            _viewTable.Opacity = _model.View == BrowseView.Table ? 1 : 0.6;

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

    private void RebuildChips(MarketFacets? facets)
    {
        _chips.Children.Clear();
        foreach (var filter in _model.Filters)
        {
            var kind = facets?.Kinds.FirstOrDefault(k => k.Key == filter.Key);
            var chip = Pill(ChipLabel(kind, filter) + "  ×");
            chip.Tag = filter.Key;
            var key = filter.Key;
            chip.Click += (_, _) => _model.RemoveFilter(key);
            _chips.Children.Add(chip);
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
            var suggestion = Pill(name);
            suggestion.HorizontalContentAlignment = HorizontalAlignment.Left;
            suggestion.Click += (_, _) => { _model.Search(name); _model.ClearSuggestions(); };
            _suggestions.Children.Add(suggestion);
        }
        _suggestions.IsVisible = _suggestions.Children.Count > 0;
    }

    private void RebuildResults()
    {
        _resultsHost.Children.Clear();
        // narrow with a detail open: the detail replaces the list
        _resultsScroll.IsVisible = !(_mode == MarketLayoutMode.Narrow && _model.SelectedId != null);
        if (_model.View == BrowseView.Cards)
        {
            var grid = new UniformGrid { Columns = CardColumns[_mode] };
            foreach (var card in _model.Results) grid.Children.Add(CardControl(card));
            _resultsHost.Children.Add(grid);
        }
        else
        {
            _resultsHost.Children.Add(TableControl());
        }
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

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var back = Pill($"‹ Back to {_model.Results.Count:N0} results");
        back.Click += (_, _) => _model.Back();
        back.IsVisible = _mode == MarketLayoutMode.Narrow;
        header.Children.Add(back);
        // medium: the detail is over the results, so it closes from here
        var close = Pill("✕");
        close.Click += (_, _) => _model.Back();
        close.IsVisible = _mode == MarketLayoutMode.Medium;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        _detail.Children.Add(header);

        var listing = _model.Detail?.Listing ?? _model.Results.FirstOrDefault(c => c.Id == _model.SelectedId);
        if (listing == null)
        {
            _detail.Children.Add(Text("Loading the listing…", DerethPalette.MutedBrush, 12));
            return;
        }

        _detail.Children.Add(Header(listing, large: true));

        var sellerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sellerRow.Children.Add(Text(listing.Own ? "Your listing" : listing.Seller, listing.Own ? DerethPalette.TealBrush : DerethPalette.TextBrush, 12));
        _detail.Children.Add(sellerRow);
        if (!listing.Own)
        {
            var seller = listing.Seller;
            _tellSeller = seller;
            var tell = Pill("Tell");
            tell.Click += (_, _) => _tellBox.IsVisible = !_tellBox.IsVisible;
            _tellBox.Watermark = $"Tell {seller}…";
            sellerRow.Children.Add(tell);
            var more = Pill($"More from {seller}");
            more.Click += (_, _) => _model.ShowSeller(seller);
            sellerRow.Children.Add(more);
            _detail.Children.Add(_tellBox);
        }

        if (_model.Detail is { } detail && detail.Listing.Id == listing.Id)
        {
            _detail.Children.Add(Section("Appraisal"));
            foreach (var line in detail.Lines) _detail.Children.Add(Text(line, DerethPalette.TextBrush, 12));
            if (detail.Spells.Count > 0)
            {
                _detail.Children.Add(Section("Spells"));
                foreach (var spell in detail.Spells) _detail.Children.Add(Text(spell.Name, spell.Cantrip ? DerethPalette.MutedBrush : DerethPalette.TextBrush, 12));
            }
        }

        var buy = Pill("Buy");
        buy.Tag = "buy";
        buy.IsEnabled = !listing.Own;
        var buyId = listing.Id;
        buy.Click += (_, _) => _model.OpenBuy(buyId);
        var price = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        price.Children.Add(Text(Mmd(listing.Price), DerethPalette.GoldBrush, 14));
        price.Children.Add(Text($"whole stack × {listing.Quantity:N0}", DerethPalette.MutedBrush, 11));
        _detail.Children.Add(price);
        _detail.Children.Add(buy);
    }

    private Control CardControl(ListingCard card)
    {
        var selected = _model.SelectedId == card.Id;
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Text(card.Name, DerethPalette.CreamBrush, 13, bold: true));
        text.Children.Add(Text(Subtitle(card), DerethPalette.MutedBrush, 11));
        var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (card.Workmanship is int work) tags.Children.Add(Text($"Wk {work}", DerethPalette.GoldBrush, 11));
        if (card.Level is int level) tags.Children.Add(Text($"Lvl {level}", DerethPalette.TextBrush, 11));
        if (card.Summary.Length > 0) tags.Children.Add(Text(card.Summary, DerethPalette.TextBrush, 11));
        text.Children.Add(tags);

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        bottom.Children.Add(Text(Mmd(card.Price), DerethPalette.GoldBrush, 12));
        var quantity = Text(card.Quantity > 1 ? $"× {card.Quantity:N0}" : string.Empty, DerethPalette.MutedBrush, 11);
        Grid.SetColumn(quantity, 1);
        bottom.Children.Add(quantity);
        var seller = Text(card.Own ? "Your listing" : $"{card.Seller} · {Ago(card.Listed)}", card.Own ? DerethPalette.TealBrush : DerethPalette.MutedBrush, 11);
        seller.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(seller, 2);
        bottom.Children.Add(seller);
        text.Children.Add(bottom);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(Icon(card));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var border = new Border
        {
            Padding = new Thickness(8), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
            BorderBrush = selected ? DerethPalette.GoldBrush : DerethPalette.GrooveEdgeBrush,
            Background = selected ? DerethPalette.GoldWashBrush : DerethPalette.GrooveBrush,
            Child = row,
        };
        var id = card.Id;
        border.PointerPressed += (_, _) => _model.Select(id);
        return border;
    }

    private Control TableControl()
    {
        var columns = Columns;
        var widths = string.Join(",", columns.Select(c => c.Width));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths) };
        for (var i = 0; i < columns.Count; i++)
        {
            var sort = columns[i].Sort;
            Control header;
            if (sort == null)
            {
                header = Text(columns[i].Header, DerethPalette.MutedBrush, 11);
            }
            else
            {
                var arrow = _model.Sort == sort ? (_model.Descending ? " ↓" : " ↑") : string.Empty;
                var button = Pill(columns[i].Header + arrow);
                var value = sort;
                button.Click += (_, _) => _model.SetSort(value);
                header = button;
            }
            Grid.SetColumn(header, i);
            grid.Children.Add(header);
        }
        var table = new StackPanel { Spacing = 2 };
        table.Children.Add(grid);
        foreach (var card in _model.Results)
            table.Children.Add(TableRow(card, columns, widths));
        return table;
    }

    private Control TableRow(ListingCard card, IReadOnlyList<Column> columns, string widths)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths) };
        for (var i = 0; i < columns.Count; i++)
        {
            Control cell = columns[i].Name
                ? NameCell(card)
                : Text(columns[i].Cell(card), columns[i].Brush(card), 12);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        var selected = _model.SelectedId == card.Id;
        var border = new Border { Child = grid, Padding = new Thickness(4, 3), Background = selected ? DerethPalette.GoldWashBrush : Brushes.Transparent };
        var id = card.Id;
        border.PointerPressed += (_, _) => _model.Select(id);
        return border;
    }

    /// <summary>The table's name cell: the name, then the quantity and the summary, as the portal's name cell shows them</summary>
    private static Control NameCell(ListingCard card)
    {
        var cell = new StackPanel { Spacing = 0 };
        cell.Children.Add(Text(card.Name, DerethPalette.CreamBrush, 12));
        var detail = string.Join("  ", new[] { card.Quantity > 1 ? $"× {card.Quantity:N0}" : null, card.Summary.Length > 0 ? card.Summary : null }
            .Where(part => part != null));
        if (detail.Length > 0) cell.Children.Add(Text(detail, DerethPalette.MutedBrush, 11));
        return cell;
    }

    /// <summary>The table's columns: the portal's, with a width each and the sort it asks for. The name is drawn by <see cref="NameCell"/>.</summary>
    private static IReadOnlyList<Column> Columns { get; } = new[]
    {
        new Column("Name", "name", "2*", c => c.Name, _ => DerethPalette.CreamBrush, Name: true),
        new Column("Type", null, "*", c => c.TypeLabel, _ => DerethPalette.MutedBrush),
        new Column("Workmanship", "workmanship", "90", c => c.Workmanship?.ToString() ?? "—", _ => DerethPalette.TextBrush),
        new Column("Wield level", "level", "90", c => c.Level?.ToString() ?? "—", _ => DerethPalette.TextBrush),
        new Column("Arcane lore", "arcane", "90", c => c.ArcaneLore?.ToString() ?? "—", _ => DerethPalette.TextBrush),
        new Column("Price", "price", "100", c => Mmd(c.Price), _ => DerethPalette.GoldBrush),
        new Column("Seller", "seller", "110", c => c.Own ? "You" : c.Seller, c => c.Own ? DerethPalette.TealBrush : DerethPalette.TextBrush),
        new Column("Listed time", "newest", "80", c => Ago(c.Listed), _ => DerethPalette.MutedBrush),
    };

    private sealed record Column(string Header, string? Sort, string Width, Func<ListingCard, string> Cell, Func<ListingCard, IBrush> Brush, bool Name = false);

    private Control Header(ListingCard card, bool large)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(Icon(card, large ? 48 : 36));
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Text(card.Name, DerethPalette.CreamBrush, large ? 15 : 13, bold: true));
        text.Children.Add(Text(Subtitle(card), DerethPalette.MutedBrush, 11));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private Control Icon(ListingCard card, double size = 40)
    {
        var holder = new Border { Width = size, Height = size, Margin = new Thickness(0, 0, 8, 0) };
        var key = (card.Plate, card.Underlay, card.Icon, card.Overlay, card.OverlaySecondary, card.UiEffects);
        if (!_icons.TryGetValue(key, out var bitmap))
        {
            bitmap = GameArtImageExtension.CreateBitmap(ItemIcon.Draw(_art, card.Underlay, card.Icon, card.Overlay, card.OverlaySecondary, card.UiEffects, card.Plate));
            _icons[key] = bitmap;
        }
        holder.Child = DerethItemCells.Icon(bitmap);
        return holder;
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

    private int CategoryIndex(MarketFacets facets)
    {
        if (_model.Filters.FirstOrDefault(f => f.Key == MarketBrowse.CategoryKey) is not { } category) return 0;
        var index = facets.Categories.ToList().FindIndex(c => c.Value == category.Low);
        return index < 0 ? 0 : index + 1;
    }

    private string? CategoryAt(int index)
    {
        if (_model.Facets is not { } facets || index <= 0 || index > facets.Categories.Count) return null;
        return facets.Categories[index - 1].Value;
    }

    private static string Subtitle(ListingCard card) =>
        string.IsNullOrEmpty(card.Material) ? card.TypeLabel : $"{card.TypeLabel} · {card.Material}";

    private static string Mmd(long price) => $"{price:N0} MMD";

    private static string Ago(DateTimeOffset listed)
    {
        var age = DateTimeOffset.UtcNow - listed;
        if (age.TotalMinutes < 60) return $"{Math.Max(1, (int)age.TotalMinutes)} m";
        if (age.TotalHours < 48) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }

    private static string ChipText(Button button) => button.Content is TextBlock text ? text.Text ?? string.Empty : button.Content?.ToString() ?? string.Empty;

    private static Button Pill(string text) => new()
    {
        Height = 26, Content = Text(text, DerethPalette.TextBrush, 12), Padding = new Thickness(8, 0)
    };

    private static TextBox Input(string watermark) => new() { Watermark = watermark, MinWidth = 90, Height = 26 };

    private static TextBlock Text(string text, IBrush brush, double size, bool bold = false) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontFamily = DerethPalette.Body, VerticalAlignment = VerticalAlignment.Center,
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, TextWrapping = TextWrapping.NoWrap,
    };

    private static TextBlock Section(string text) => Text(text.ToUpperInvariant(), DerethPalette.MutedBrush, 10);
}
