using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;
using static LegACEy.Client.Themes.DerethItemCells;

namespace LegACEy.Plugin.Market;

/// <summary>The narrow window's two tabs over the ledger and the balance chart.</summary>
public enum NarrowActivityTab { Ledger, Chart }

/// <summary>
/// The Activity screen: four tiles, the trade note withdrawal, and the ledger and balance chart. In a wide or medium window they sit side by side;
/// in a narrow one the tiles wrap two by two and the ledger and chart take tabs. The window tells it the <see cref="MarketLayoutMode"/>.
/// </summary>
public sealed class ActivityScreen : UserControl, IDisposable
{
    private const double ChartWidth = 340;
    private const double ChartHeight = 170;

    /// <summary>How close to the end of the ledger the window asks for the older page</summary>
    private const double NearEnd = 40;

    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush;
    private static readonly IBrush TextBrush = DerethPalette.TextBrush;
    private static readonly IBrush MutedBrush = DerethPalette.MutedBrush;
    private static readonly IBrush InvalidBrush = DerethPalette.InvalidBrush;

    private readonly MarketClient _client;
    private readonly ActivityViewModel _model;
    private MarketLayoutMode _mode = MarketLayoutMode.Medium;
    private NarrowActivityTab _narrowTab = NarrowActivityTab.Ledger;
    private bool _disposed;

    private readonly UniformGrid _tiles = new() { Columns = 4 };
    private readonly TileParts _balance = NewTile("Balance", "MMD in your account");
    private readonly TileParts _earned = NewTile("Earned this week", string.Empty);
    private readonly TileParts _spent = NewTile("Spent this week", string.Empty);
    private readonly TileParts _listed = NewTile("Listed now", "active listings");

    private readonly TextBox _amount = new() { Watermark = "MMD", Width = 110, Height = 26 };
    private readonly DerethButton _withdraw = new() { Height = 26, Content = Label("Withdraw trade notes", TextBrush, 12) };
    private readonly TextBlock _notice = new()
    {
        FontSize = 12, FontFamily = DerethPalette.Body, TextWrapping = TextWrapping.Wrap, Foreground = InvalidBrush, IsVisible = false,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly Dictionary<LedgerTab, TextBlock> _tabLabels = new();
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _empty = Label(string.Empty, MutedBrush, 12);
    private readonly ScrollViewer _ledgerScroll;
    private readonly Border _ledgerPanel;

    private readonly Canvas _chartCanvas = new() { Width = ChartWidth, Height = ChartHeight };
    private readonly Polyline _chartLine = new() { Stroke = GoldBrush, StrokeThickness = 2 };
    private readonly TextBlock _chartCaption = Label(string.Empty, TextBrush, 13);
    private readonly TextBlock _axisStart = Label(string.Empty, MutedBrush, 11);
    private readonly TextBlock _axisEnd = Label(string.Empty, MutedBrush, 11);
    private readonly Border _chartPanel;

    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
    private readonly StackPanel _narrowTabs = new() { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false };
    private readonly Dictionary<NarrowActivityTab, TextBlock> _narrowTabLabels = new();

    public ActivityScreen(MarketClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _model = new ActivityViewModel(client);

        _ledgerScroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _ledgerScroll.ScrollChanged += (_, _) => OnLedgerScrolled();

        _ledgerPanel = Framed(Stacked(LedgerHeader(), _ledgerScroll, _empty));
        _chartCanvas.Children.Add(_chartLine);
        _chartPanel = Framed(new StackPanel { Spacing = 6, Children = { _chartCaption, _chartCanvas, AxisRow() } });

        Content = BuildContent();
        _client.Changed += OnChanged;
        _model.Changed += OnChanged;
        ApplyMode(_mode);
        Refresh();
    }

    /// <summary>The layout the screen is drawn in. Set by the panel from the window's width, never by this screen.</summary>
    public void SetMode(MarketLayoutMode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        ApplyMode(mode);
    }

    /// <summary>Loads the newest page of history. Called each time the Activity screen opens.</summary>
    public void Load() => _model.Load();

    /// <summary>The ledger's rows as they are drawn, newest first.</summary>
    public IReadOnlyList<string> LedgerRowTexts => _rows.Children.OfType<Grid>().Select(row => ((TextBlock)row.Children[1]).Text ?? string.Empty).ToList();

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>
    /// The window scrolled. When the ledger is at its end, or has nothing to scroll, the older page is asked for, so the ledger keeps filling.
    /// </summary>
    private void OnLedgerScrolled()
    {
        // rows added but not yet laid out read as zero: that isn't a ledger to scroll, so it asks for nothing
        if (_ledgerScroll.Extent.Height <= 0 || _ledgerScroll.Viewport.Height <= 0) return;

        var room = _ledgerScroll.Extent.Height - _ledgerScroll.Viewport.Height;
        if (room <= 0 || _ledgerScroll.Offset.Y >= room - NearEnd) _model.LoadOlder();
    }

    private void Refresh()
    {
        if (_disposed) return;

        var account = _model.Account;
        _balance.Value.Text = account == null ? "—" : account.Balance.ToString("N0", CultureInfo.InvariantCulture);
        _listed.Value.Text = account == null ? "—" : $"{account.ListingCount:N0} / {account.ListingCap:N0}";
        _earned.Value.Text = "+" + _model.Earned.ToString("N0", CultureInfo.InvariantCulture);
        _spent.Value.Text = "−" + _model.Spent.ToString("N0", CultureInfo.InvariantCulture);
        _earned.Detail.Text = Items(_model.ItemsSold, "sold");
        _spent.Detail.Text = Items(_model.ItemsBought, "bought");

        foreach (var pair in _tabLabels) pair.Value.Foreground = pair.Key == _model.Tab ? GoldBrush : TextBrush;

        _rows.Children.Clear();
        foreach (var row in _model.Rows) _rows.Children.Add(LedgerRow(row));

        _empty.Text = _model.Requesting ? "Loading…" : _rows.Children.Count == 0 ? "Nothing here yet." : string.Empty;
        _empty.IsVisible = _empty.Text.Length > 0;

        _notice.Text = _model.Notice;
        _notice.IsVisible = _model.Notice.Length > 0;
        _withdraw.IsEnabled = !_model.Withdrawing;

        DrawChart();
    }

    private static Grid LedgerRow(ActivityRow row)
    {
        var text = Label(row.Text, TextBrush, 12);
        text.TextWrapping = TextWrapping.Wrap;
        var balance = Label(row.BalanceAfter is long after ? $"{after:N0}" : string.Empty, MutedBrush, 11);
        balance.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(Column(Label(row.Time.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.InvariantCulture), MutedBrush, 11), 0));
        grid.Children.Add(Column(text, 1));
        grid.Children.Add(Column(balance, 2));
        return grid;
    }

    /// <summary>The balance over the loaded ledger lines, placed by time, with the dates of its first and last points</summary>
    private void DrawChart()
    {
        var series = _model.BalanceSeries;
        _chartLine.Points.Clear();

        if (series.Count == 0)
        {
            _chartCaption.Text = "Balance";
            _axisStart.Text = string.Empty;
            _axisEnd.Text = string.Empty;
            return;
        }

        var first = series[0].Time.ToLocalTime();
        var last = series[series.Count - 1].Time.ToLocalTime();
        var from = first.ToString("d MMM", CultureInfo.InvariantCulture);
        var to = last.ToString("d MMM", CultureInfo.InvariantCulture);
        _chartCaption.Text = from == to ? $"Balance, {from}" : $"Balance, {from} – {to}";
        _axisStart.Text = from;
        _axisEnd.Text = to;

        if (series.Count < 2) return;

        var low = series.Min(point => point.Balance);
        var balanceSpan = Math.Max(1, series.Max(point => point.Balance) - low);
        var start = series[0].Time;
        var timeSpan = Math.Max(1, (series[series.Count - 1].Time - start).TotalSeconds);
        foreach (var point in series)
        {
            var x = (point.Time - start).TotalSeconds * ChartWidth / timeSpan;
            var y = ChartHeight - (point.Balance - low) * ChartHeight / balanceSpan;
            _chartLine.Points.Add(new Point(x, y));
        }
    }

    private void ApplyMode(MarketLayoutMode mode)
    {
        var narrow = mode == MarketLayoutMode.Narrow;

        // the tiles read Balance, Listed / Earned, Spent in a narrow window, and Balance, Earned, Spent, Listed otherwise, as the mockups draw them
        _tiles.Columns = narrow ? 2 : 4;
        _tiles.Children.Clear();
        foreach (var tile in narrow ? new[] { _balance, _listed, _earned, _spent } : new[] { _balance, _earned, _spent, _listed })
            _tiles.Children.Add(tile.Frame);

        _narrowTabs.IsVisible = narrow;
        _body.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        // narrow: the ledger and the chart share the one cell, and the tab shows one of them
        Grid.SetColumn(_ledgerPanel, 0);
        Grid.SetColumn(_chartPanel, narrow ? 0 : 1);
        ApplyNarrowTab();
    }

    private void ChooseNarrowTab(NarrowActivityTab tab)
    {
        _narrowTab = tab;
        ApplyNarrowTab();
    }

    private void ApplyNarrowTab()
    {
        var narrow = _mode == MarketLayoutMode.Narrow;
        _ledgerPanel.IsVisible = !narrow || _narrowTab == NarrowActivityTab.Ledger;
        _chartPanel.IsVisible = !narrow || _narrowTab == NarrowActivityTab.Chart;
        foreach (var pair in _narrowTabLabels) pair.Value.Foreground = pair.Key == _narrowTab ? GoldBrush : TextBrush;
    }

    private Control BuildContent()
    {
        _withdraw.Click += (_, _) => _model.WithdrawNotes(_amount.Text ?? string.Empty);

        var withdrawRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        withdrawRow.Children.Add(_amount);
        withdrawRow.Children.Add(_withdraw);
        withdrawRow.Children.Add(_notice);

        foreach (var tab in new[] { NarrowActivityTab.Ledger, NarrowActivityTab.Chart })
        {
            var label = Label(tab == NarrowActivityTab.Ledger ? "Ledger" : "Balance chart", TextBrush, 12);
            _narrowTabLabels[tab] = label;
            var chosen = tab;
            var button = new DerethButton { Height = 26, Content = label };
            button.Click += (_, _) => ChooseNarrowTab(chosen);
            _narrowTabs.Children.Add(button);
        }

        // rows: the body takes what is left, so the ledger's scroll viewer has a height to scroll in
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), RowSpacing = 10 };
        root.Children.Add(Row(_tiles, 0));
        root.Children.Add(Row(withdrawRow, 1));
        root.Children.Add(Row(_narrowTabs, 2));
        _body.Children.Add(_ledgerPanel);
        _body.Children.Add(_chartPanel);
        root.Children.Add(Row(_body, 3));
        return root;
    }

    /// <summary>The ledger's column: its header, the scroll viewer taking the rest of the height, and the empty line. A stack would give the viewer no height to scroll in.</summary>
    private static Grid Stacked(Control header, ScrollViewer rows, Control footer)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 6 };
        grid.Children.Add(Row(header, 0));
        grid.Children.Add(Row(rows, 1));
        grid.Children.Add(Row(footer, 2));
        return grid;
    }

    private Control LedgerHeader()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (tab, text) in new[] { (LedgerTab.All, "All"), (LedgerTab.Mmd, "MMD"), (LedgerTab.Items, "Items") })
        {
            var label = Label(text, TextBrush, 12);
            _tabLabels[tab] = label;
            var chosen = tab;
            var button = new DerethButton { Height = 24, Content = label };
            button.Click += (_, _) => _model.SelectTab(chosen);
            tabs.Children.Add(button);
        }

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(Column(Label("Ledger", TextBrush, 13), 0));
        header.Children.Add(Column(new Border(), 1));
        header.Children.Add(Column(tabs, 2));
        return header;
    }

    /// <summary>The chart's dates under it: the first point's on the left, the last point's on the right</summary>
    private Control AxisRow()
    {
        var axis = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        axis.Children.Add(Column(_axisStart, 0));
        axis.Children.Add(Column(new Border(), 1));
        axis.Children.Add(Column(_axisEnd, 2));
        return axis;
    }

    private static Border Framed(Control content) => new()
    {
        Padding = new Thickness(10), BorderThickness = new Thickness(1), BorderBrush = DerethPalette.GrooveEdgeBrush, Child = content
    };

    /// <summary>One tile: the frame the grid places, and the value and detail lines the screen fills</summary>
    private sealed class TileParts
    {
        public TileParts(Border frame, TextBlock value, TextBlock detail)
        {
            Frame = frame; Value = value; Detail = detail;
        }

        public Border Frame { get; }
        public TextBlock Value { get; }
        public TextBlock Detail { get; }
    }

    private static TileParts NewTile(string label, string detail)
    {
        var value = Label(string.Empty, GoldBrush, 18);
        var detailText = Label(detail, MutedBrush, 11);
        var frame = new Border
        {
            Padding = new Thickness(10, 8), BorderThickness = new Thickness(1), BorderBrush = DerethPalette.GrooveEdgeBrush,
            Child = new StackPanel { Spacing = 2, Children = { Label(label, MutedBrush, 11), value, detailText } }
        };
        return new TileParts(frame, value, detailText);
    }

    private static string Items(int count, string verb) => $"{count} item{(count == 1 ? string.Empty : "s")} {verb}";

    private static T Row<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static T Column<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Changed -= OnChanged;
        _model.Changed -= OnChanged;
    }
}
