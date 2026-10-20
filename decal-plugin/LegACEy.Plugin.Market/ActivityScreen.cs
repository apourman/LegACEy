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

namespace LegACEy.Plugin.Market;

/// <summary>The narrow window's two tabs over the ledger and the balance chart.</summary>
public enum NarrowActivityTab { Ledger, Chart }

/// <summary>
/// The Activity screen: four tiles, the trade note withdrawal, and the ledger and balance chart. In a wide or medium window they sit side by side;
/// in a narrow one the tiles wrap two by two and the ledger and chart take tabs. The window tells it the <see cref="MarketLayoutMode"/>.
/// </summary>
public sealed class ActivityScreen : UserControl, IDisposable
{
    private const double ChartHeight = 170;

    /// <summary>How close to the end of the ledger the window asks for the older page</summary>
    private const double NearEnd = 40;

    private static readonly IBrush ChartFill = MarketInk.Vertical(DerethPalette.Gold.WithAlpha(0x40), DerethPalette.Gold.WithAlpha(0x00));

    private readonly MarketClient _client;
    private readonly ActivityViewModel _model;
    private MarketLayoutMode _mode = MarketLayoutMode.Medium;
    private NarrowActivityTab _narrowTab = NarrowActivityTab.Ledger;
    private bool _disposed;

    private readonly Grid _tiles = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly TileParts _balance = NewTile("Balance", "MMD in your account", coin: true);
    private readonly TileParts _earned = NewTile("Earned this week", string.Empty, ink: DerethPalette.RaisedBrush);
    private readonly TileParts _spent = NewTile("Spent this week", string.Empty, ink: MarketInk.BadBrush);
    private readonly TileParts _listed = NewTile("Listed now", "active listings");

    private readonly TextBox _amount = MarketParts.Field("MMD", 26, 90);
    private readonly MarketButton _withdraw = MarketParts.Button("Withdraw trade notes", MarketParts.Kind.Red, 26);
    private readonly TextBlock _notice = new()
    {
        FontSize = 12, FontFamily = DerethPalette.Body, TextWrapping = TextWrapping.Wrap, Foreground = MarketInk.BadBrush, IsVisible = false,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly MarketTabs _ledgerTabs = new("All", "MMD", "Items");
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty = MarketParts.Text(string.Empty, MarketInk.MutedBrush, 12.5);
    private readonly ScrollViewer _ledgerScroll;
    private readonly Border _ledgerPanel;

    private readonly Canvas _chartCanvas = new() { Height = ChartHeight };
    private readonly Polyline _chartLine = new() { Stroke = DerethPalette.GoldBrush, StrokeThickness = 2, StrokeJoin = PenLineJoin.Round };
    private readonly Polygon _chartArea = new() { Fill = ChartFill };
    private readonly Ellipse _chartEnd = new() { Width = 8, Height = 8, Fill = MarketInk.LitBrush, IsVisible = false };
    private readonly TextBlock _chartCaption = MarketParts.Head(string.Empty);
    private readonly TextBlock _axisStart = MarketParts.Text(string.Empty, MarketInk.MutedBrush, 10.5);
    private readonly TextBlock _axisEnd = MarketParts.Text(string.Empty, MarketInk.MutedBrush, 10.5);
    private readonly Border _chartPanel;

    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14 };
    private readonly MarketTabs _narrowTabs = new("Ledger", "Balance chart") { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left };

    public ActivityScreen(MarketClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _model = new ActivityViewModel(client);

        _ledgerScroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _ledgerScroll.ScrollChanged += (_, _) => OnLedgerScrolled();

        _ledgerPanel = MarketParts.Card(Stacked(LedgerHeader(), _ledgerScroll, _empty), 10, new Thickness(16, 14));
        _chartCanvas.Children.Add(_chartArea);
        _chartCanvas.Children.Add(_chartLine);
        _chartCanvas.Children.Add(_chartEnd);
        _chartCanvas.SizeChanged += (_, _) => DrawChart();
        _chartPanel = MarketParts.Card(new StackPanel { Spacing = 10, Children = { ChartHeader(), _chartCanvas, AxisRow(), _notice } }, 10, new Thickness(16, 14));

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
    public IReadOnlyList<string> LedgerRowTexts => _rows.Children.OfType<Control>().Select(row => row.Tag as string ?? string.Empty).ToList();

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

        _ledgerTabs.Selected = (int)_model.Tab;

        _rows.Children.Clear();
        foreach (var row in _model.Rows) _rows.Children.Add(LedgerRow(row));

        _empty.Text = _model.Requesting ? "Loading…" : _rows.Children.Count == 0 ? "Nothing here yet." : string.Empty;
        _empty.IsVisible = _empty.Text.Length > 0;

        _notice.Text = _model.Notice;
        _notice.IsVisible = _model.Notice.Length > 0;
        _withdraw.IsEnabled = !_model.Withdrawing;

        DrawChart();
    }

    /// <summary>
    /// One ledger line: the time, what happened (the balance after it under that, for an MMD line), and the signed amount on the right in
    /// green or red. The row's tag is its full text.
    /// </summary>
    private static Control LedgerRow(ActivityRow row)
    {
        var (what, amount) = SplitAmount(row.Text);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = MarketParts.Text(what, DerethPalette.CreamBrush, 12.5);
        title.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(title);
        if (row.BalanceAfter is long after) text.Children.Add(MarketParts.Text($"balance {after:N0}", MarketInk.MutedBrush, 11));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(MarketParts.Text(When(row.Time.ToLocalTime()), MarketInk.MutedBrush, 11.5));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var money = MarketParts.Text(amount, amount.StartsWith("+", StringComparison.Ordinal) ? DerethPalette.RaisedBrush : MarketInk.BadBrush, 13);
        Grid.SetColumn(money, 2);
        grid.Children.Add(money);
        return new Border
        {
            Tag = row.Text, Child = grid, Padding = new Thickness(0, 7), BorderBrush = MarketInk.LineSoftBrush, BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>"Sold Sword to Bob · +50 MMD" as "Sold Sword to Bob" and "+50"; a line with no amount keeps all its words.</summary>
    private static (string What, string Amount) SplitAmount(string text)
    {
        var dot = text.LastIndexOf(" · ", StringComparison.Ordinal);
        if (dot < 0 || !text.EndsWith(" MMD", StringComparison.Ordinal)) return (text, string.Empty);
        var amount = text.Substring(dot + 3, text.Length - dot - 3 - 4);
        return amount.Length > 0 && (amount[0] == '+' || amount[0] == '−') ? (text.Substring(0, dot), amount) : (text, string.Empty);
    }

    /// <summary>A ledger time: the clock today, "Yest." yesterday, the date before that</summary>
    private static string When(DateTimeOffset time)
    {
        var today = DateTimeOffset.Now.Date;
        if (time.Date == today) return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (time.Date == today.AddDays(-1)) return "Yest.";
        return time.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>The balance over the loaded ledger lines, placed by time, with the dates of its first and last points</summary>
    private void DrawChart()
    {
        var series = _model.BalanceSeries;
        // a shape redraws when it is given a new list, not when its list changes
        _chartLine.Points = new List<Point>();
        _chartArea.Points = new List<Point>();
        _chartEnd.IsVisible = false;

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

        var width = _chartCanvas.Bounds.Width;
        if (series.Count < 2 || width <= 0) return;

        var low = series.Min(point => point.Balance);
        var balanceSpan = Math.Max(1, series.Max(point => point.Balance) - low);
        var start = series[0].Time;
        var timeSpan = Math.Max(1, (series[series.Count - 1].Time - start).TotalSeconds);
        // a little room above and below the line, as the mockup leaves
        const double pad = 10;
        var line = series.Select(point => new Point(
            (point.Time - start).TotalSeconds * width / timeSpan,
            pad + (ChartHeight - 2 * pad) * (1 - (double)(point.Balance - low) / balanceSpan))).ToList();
        var end = line[line.Count - 1];
        _chartLine.Points = line;
        _chartArea.Points = line.Concat(new[] { new Point(width, ChartHeight), new Point(0, ChartHeight) }).ToList();
        Canvas.SetLeft(_chartEnd, end.X - 4);
        Canvas.SetTop(_chartEnd, end.Y - 4);
        _chartEnd.IsVisible = true;
    }

    private void ApplyMode(MarketLayoutMode mode)
    {
        var narrow = mode == MarketLayoutMode.Narrow;

        // the tiles read Balance, Listed / Earned, Spent in a narrow window, and Balance, Earned, Spent, Listed otherwise, as the mockups draw them
        var columns = narrow ? 2 : 4;
        _tiles.Children.Clear();
        _tiles.ColumnDefinitions.Clear();
        _tiles.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++) _tiles.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var order = narrow ? new[] { _balance, _listed, _earned, _spent } : new[] { _balance, _earned, _spent, _listed };
        for (var i = 0; i < order.Length; i++)
        {
            if (i % columns == 0) _tiles.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(order[i].Frame, i % columns);
            Grid.SetRow(order[i].Frame, i / columns);
            _tiles.Children.Add(order[i].Frame);
        }

        _narrowTabs.IsVisible = narrow;
        // wide and medium: the chart on the left and the ledger on the right; narrow: one cell, and the tab shows one of them
        _body.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_chartPanel, 0);
        Grid.SetColumn(_ledgerPanel, narrow ? 0 : 1);
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
        _narrowTabs.Selected = (int)_narrowTab;
    }

    private Control BuildContent()
    {
        _withdraw.Click += (_, _) => _model.WithdrawNotes(_amount.Text ?? string.Empty);
        _narrowTabs.Chosen += index => ChooseNarrowTab((NarrowActivityTab)index);
        _ledgerTabs.Chosen += index => _model.SelectTab((LedgerTab)index);

        // rows: the body takes what is left, so the ledger's scroll viewer has a height to scroll in
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 12, Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(Row(_tiles, 0));
        root.Children.Add(Row(_narrowTabs, 1));
        _body.Children.Add(_ledgerPanel);
        _body.Children.Add(_chartPanel);
        root.Children.Add(Row(_body, 2));
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
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(Column(MarketParts.Head("Ledger"), 0));
        header.Children.Add(Column(_ledgerTabs, 2));
        return header;
    }

    /// <summary>The chart's head: its dates, then the trade note withdrawal on the right</summary>
    private Control ChartHeader()
    {
        var withdraw = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _amount, _withdraw } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(Column(_chartCaption, 0));
        header.Children.Add(Column(withdraw, 1));
        return header;
    }

    /// <summary>The chart's dates under it: the first point's on the left, the last point's on the right</summary>
    private Control AxisRow()
    {
        var axis = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        axis.Children.Add(Column(_axisStart, 0));
        axis.Children.Add(Column(_axisEnd, 2));
        return axis;
    }

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

    /// <summary>A figure tile: a small-caps name, the value in large Palatino (with the coin for the balance), and a muted line under it.</summary>
    private static TileParts NewTile(string label, string detail, bool coin = false, IBrush? ink = null)
    {
        var value = MarketParts.Text(string.Empty, ink ?? DerethPalette.CreamBrush, 26, title: true);
        value.TextTrimming = TextTrimming.None;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (coin) line.Children.Add(MarketParts.Coin(18));
        line.Children.Add(value);
        var detailText = MarketParts.Text(detail, MarketInk.MutedBrush, 12);
        var frame = MarketParts.Card(new StackPanel { Spacing = 4, Children = { MarketParts.Caps(label), line, detailText } }, 10, new Thickness(16, 12));
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
