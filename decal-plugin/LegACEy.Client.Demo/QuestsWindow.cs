using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>What the quests window reads from the game and asks of it. The client implements it over retail; tests fake it.</summary>
public interface IQuestsGame
{
    /// <summary>The wall clock in Unix seconds, which the journal's timers run on.</summary>
    double Time { get; }
    /// <summary>The player's map coordinates, north and east (negatives south and west), or null outside the world.</summary>
    (double North, double East)? Coordinates { get; }
    /// <summary>Sends retail's AbandonContract (game action 0x0316). False when it could not be sent.</summary>
    bool Abandon(uint contract);
}

/// <summary>
/// The Dereth quests window, retail's Contracts, Journal and Page List tabs laid out as retail lays them (LayoutDesc 0x21000069, 0x21000066 and
/// 0x21000067 in the installed client_local_English.dat): the same columns, lines and green text buttons in the same places, and the journal
/// on retail's own parchment art. One instance serves a session; <see cref="Refresh"/> redraws what changed.
/// </summary>
public sealed class QuestsWindow
{
    public const string WindowId = "quests";
    public const int Width = 310, Height = 600;
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(290, 480));
    private const double RowHeight = 20;
    private static readonly string[] TabNames = { "Contracts", "Journal", "Page List" };
    // Retail's journal parchment: the top and bottom strips, the left and right edges and the middle, which tile down the page.
    private const uint ParchmentTop = 0x06001273, ParchmentBottom = 0x06001270, ParchmentLeft = 0x06001271, ParchmentRight = 0x06001272, ParchmentMiddle = 0x0600126F;

    // Retail's green for its column headings and text buttons, and the dark ink on the parchment.
    private static readonly IBrush GreenBrush = DerethPalette.Brush(Color.Parse("#36E23A"));
    private static readonly IBrush InkBrush = DerethPalette.Brush(Color.Parse("#1E140A"));
    private static readonly IBrush PageEntryBrush = DerethPalette.Brush(Color.FromArgb(0x30, 0x4A, 0x30, 0x10));
    private static readonly IBrush PageLineBrush = DerethPalette.Brush(Color.FromArgb(0x70, 0x5A, 0x3C, 0x18));
    private static readonly IBrush ParchmentBrush = DerethPalette.Brush(Color.Parse("#C9A468"));
    private static readonly IBrush WellBrush = DerethPalette.Brush(Color.FromArgb(0xB0, 0x06, 0x05, 0x04));
    private static readonly IBrush LineBrush = DerethPalette.Brush(DerethPalette.Line);
    private static readonly IBrush HoverBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x12));
    private static readonly IBrush SelectedBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x26));

    private readonly QuestState _state;
    private readonly QuestJournal _journal;
    private readonly IQuestsGame _game;
    private readonly Func<IReadOnlyDictionary<uint, ContractInfo>> _contracts;
    private readonly DerethSegmented _tabs;
    private readonly ContentControl _page;
    private readonly Control[] _pages;

    // Contracts.
    private readonly StackPanel _contractList = new();
    private readonly TextBlock _status, _contact, _contactLocation, _questLocation, _description, _timed;
    private readonly GreenButton _abandon;
    // Journal.
    private readonly TextBox _label, _title, _notes, _days, _hours, _minutes;
    private readonly TextBlock _location, _running, _pageNumber;
    private readonly Control _timerEntries;
    private readonly GreenButton _record, _start, _first, _last, _previous, _next;
    // Page List.
    private readonly StackPanel _pageList = new();
    private readonly TextBox _search;
    private readonly GreenButton _delete;

    private int _tab;
    private uint _contract;
    private int _sortContracts, _sortPages;
    private bool _reverseContracts, _reversePages;
    private int _current, _listed = -1;
    private bool _loading;
    private readonly Dictionary<Control, string> _drawn = new();

    public QuestsWindow(QuestState state, QuestJournal journal, IQuestsGame game, Func<IReadOnlyDictionary<uint, ContractInfo>> contracts, IGameArtSource? art)
    {
        _state = state;
        _journal = journal;
        _game = game;
        _contracts = contracts;
        _tabs = new DerethSegmented(TabNames, cellPadding: new Thickness(4, 4)) { VerticalAlignment = VerticalAlignment.Center };
        _tabs.Chosen += ShowTab;
        _pages = new Control[3];

        // Contracts: the list under its two green headings, then the chosen contract's lines, its description, Timed and Abandon.
        _status = Value();
        _contact = Value();
        _contactLocation = Value();
        _questLocation = Value();
        _timed = Value();
        _description = Value();
        _description.TextWrapping = TextWrapping.Wrap;
        _description.VerticalAlignment = VerticalAlignment.Top;
        _abandon = new GreenButton("Abandon", Abandon) { HorizontalAlignment = HorizontalAlignment.Right };
        var details = new StackPanel { Spacing = 2, Margin = new Thickness(8, 4, 8, 0) };
        details.Children.Add(Line("Status:", _status, 56));
        details.Children.Add(Line("Contact:", _contact, 56));
        details.Children.Add(Line("Contact Location:", _contactLocation, 116));
        details.Children.Add(Line("Quest Location:", _questLocation, 116));
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*,Auto"), Margin = new Thickness(8, 2, 8, 6) };
        bottom.Children.Add(Label("Timed:", 13, DerethPalette.TextBrush));
        Grid.SetColumn(_timed, 1);
        bottom.Children.Add(_timed);
        Grid.SetColumn(_abandon, 2);
        bottom.Children.Add(_abandon);
        _pages[0] = Stack(
            (Headings(("Contract", 152), ("Status", 118)), false), (Well(_contractList, () => SelectContract(0)), true), (details, false),
            (new Border { Height = 56, Margin = new Thickness(8, 4, 8, 0), Child = _description, ClipToBounds = true }, false), (bottom, false));

        // Journal: retail's parchment page.
        _label = PageEntry(20);
        _title = PageEntry(60);
        _notes = PageEntry(4000);
        _notes.AcceptsReturn = true;
        _notes.TextWrapping = TextWrapping.Wrap;
        _notes.Height = double.NaN;
        _notes.VerticalContentAlignment = VerticalAlignment.Top;
        _notes.Padding = new Thickness(4, 3);
        _days = PageEntry(3);
        _hours = PageEntry(2);
        _minutes = PageEntry(2);
        // The text property itself, not TextChanged, which Avalonia raises later: a page turn must not carry the old page's words over.
        foreach (var box in new[] { _label, _title, _notes, _days, _hours, _minutes })
            box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Edited(); };
        _location = Ink("None");
        _running = Ink(string.Empty);
        _pageNumber = Ink("~ 1 ~");
        _pageNumber.HorizontalAlignment = HorizontalAlignment.Center;
        _record = new GreenButton("Record", Record);
        _start = new GreenButton("Start", StartTimer);
        _first = new GreenButton("First", () => ShowPage(0));
        _last = new GreenButton("Last", () => ShowPage(_journal.Pages.Count - 1));
        _previous = new GreenButton(Arrow(left: true), () => ShowPage(_current - 1));
        _next = new GreenButton(Arrow(left: false), () => ShowPage(_current + 1));
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("30,Auto,*,Auto,120"), ColumnSpacing = 6 };
        Put(top, _previous, 0);
        Put(top, new GreenButton("New", NewPage), 1);
        Put(top, Ink("Label:"), 3);
        Put(top, _label, 4);
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        Put(title, Ink("Title:"), 0);
        Put(title, _title, 1);
        var location = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        Put(location, Ink("Location:"), 0);
        _location.Margin = new Thickness(4, 0);
        Put(location, new Border { Background = PageEntryBrush, Height = 20, Margin = new Thickness(0, 0, 8, 0), Child = _location }, 1);
        Put(location, _record, 2);
        var entries = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var (box, unit) in new[] { (_days, "d"), (_hours, "h"), (_minutes, "m") })
        {
            box.Width = 22;
            entries.Children.Add(box);
            entries.Children.Add(Ink(unit));
        }
        _timerEntries = entries;
        var timer = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        Put(timer, Ink("Timer:"), 0);
        Put(timer, _timerEntries, 1);
        Put(timer, _running, 1);
        Put(timer, _start, 2);
        var turns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,30"), Margin = new Thickness(4, 6, 0, 0) };
        Put(turns, _first, 0);
        Put(turns, _pageNumber, 1);
        _last.HorizontalAlignment = HorizontalAlignment.Right;
        Put(turns, _last, 2);
        Put(turns, _next, 3);
        var notes = Ink("Notes:");
        notes.Margin = new Thickness(0, 6, 0, 2);
        var sheet = Stack((top, false), (title, false), (notes, false), (_notes, true), (location, false), (timer, false), (turns, false));
        sheet.Margin = new Thickness(16, 10, 16, 6);
        var parchment = Parchment(art);
        parchment.Children.Add(sheet);
        _pages[1] = parchment;

        // Page List: every page under four green headings, then Delete, Search and Reset.
        _search = Entry(40, () => { _listed = -1; Refresh(); });
        _delete = new GreenButton("Delete", DeletePage);
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(8, 6, 8, 6) };
        Put(tools, _delete, 0);
        var search = Label("Search:", 13, GreenBrush);
        search.Margin = new Thickness(8, 0, 0, 0);
        Put(tools, search, 1);
        Put(tools, _search, 2);
        Put(tools, new GreenButton("Reset", () => _search.Text = string.Empty), 3);
        _pages[2] = Stack(
            (Headings(("#", 20), ("Title", 90), ("Timer", 90), ("Label", 80)), false), (Well(_pageList, () => { _listed = -1; Refresh(); }), true), (tools, false));

        _page = new ContentControl { Margin = new Thickness(0, 5, 0, 0) };
        Window = new DerethWindow(string.Empty, null, _page, _tabs, stretchActions: true);
        Window.CloseRequested += (_, _) => Close?.Invoke();
        ShowPage(0);
        ShowTab(0);
    }

    public DerethWindow Window { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    public int Tab => _tab;

    /// <summary>The journal page the Journal tab shows.</summary>
    public int CurrentPage => _current;

    public void ShowTab(int tab)
    {
        _tab = tab;
        _tabs.Selected = tab;
        _page.Content = _pages[tab];
        Refresh();
    }

    /// <summary>Redraws what changed: the contracts' statuses and timers count down, so call it about once a second while open.</summary>
    public void Refresh()
    {
        switch (_tab)
        {
            case 0: DrawContracts(); break;
            case 1: DrawJournal(); break;
            default: DrawPageList(); break;
        }
    }

    // Contracts.

    /// <summary>The contracts the table knows, in the chosen order, with retail's status text. A contract the table lacks is left out, as retail does.</summary>
    public IReadOnlyList<(Contract Contract, ContractInfo Info, string Status)> ContractRows()
    {
        var table = _contracts();
        var now = _state.Now;
        var rows = _state.Contracts.Where(c => table.ContainsKey(c.Id)).Select(c => (c, table[c.Id], QuestState.Status(c, table[c.Id], now)));
        rows = _sortContracts == 0
            ? rows.OrderBy(r => r.Item2.Name, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(r => r.Item3, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Item2.Name, StringComparer.OrdinalIgnoreCase);
        var list = rows.ToList();
        if (_reverseContracts) list.Reverse();
        return list;
    }

    public void SelectContract(uint id)
    {
        _contract = id;
        Refresh();
    }

    /// <summary>A click on a heading: sorts by it, and a second click on the same heading reverses the order.</summary>
    public void SortContracts(int column)
    {
        _reverseContracts = column == _sortContracts && !_reverseContracts;
        _sortContracts = column;
        Refresh();
    }

    public void Abandon()
    {
        if (_contract != 0 && _game.Abandon(_contract)) Refresh();
    }

    private void DrawContracts()
    {
        var rows = ContractRows();
        if (rows.All(r => r.Contract.Id != _contract)) _contract = 0;
        Redraw(_contractList, $"{_contract}|{string.Join("|", rows.Select(r => $"{r.Contract.Id}:{r.Status}"))}", () =>
        {
            foreach (var (contract, info, status) in rows)
                _contractList.Children.Add(Row(contract.Id == _contract, _ => SelectContract(contract.Id == _contract ? 0 : contract.Id),
                    (info.Name, 152), (status, 118)));
        });
        var chosen = rows.FirstOrDefault(r => r.Contract.Id == _contract);
        _abandon.IsEnabled = chosen.Info != null;
        if (chosen.Info is not { } info)
        {
            foreach (var text in new[] { _status, _contact, _contactLocation, _questLocation, _description, _timed }) Set(text, string.Empty);
            return;
        }
        var (name, cell) = QuestState.Contact(chosen.Contract, info);
        Set(_status, chosen.Status);
        Set(_contact, name);
        Set(_contactLocation, QuestState.Location(cell));
        Set(_questLocation, QuestState.Location(info.QuestCell));
        Set(_description, info.Description);
        Set(_timed, QuestState.Timed(chosen.Contract, info, _state.Now));
    }

    // Journal.

    /// <summary>Shows a page of the journal, as the arrows, First, Last and a double click in the page list do.</summary>
    public void ShowPage(int index)
    {
        var pages = _journal.Pages;
        _current = Math.Max(0, Math.Min(index, pages.Count - 1));
        var page = pages[_current];
        _loading = true;
        _label.Text = page.Label;
        _title.Text = page.Title;
        _notes.Text = page.Notes;
        _days.Text = page.Days.ToString(CultureInfo.InvariantCulture);
        _hours.Text = page.Hours.ToString(CultureInfo.InvariantCulture);
        _minutes.Text = page.Minutes.ToString(CultureInfo.InvariantCulture);
        _loading = false;
        Refresh();
    }

    public void NewPage()
    {
        ShowPage(_journal.Add());
        ShowTab(1);
    }

    /// <summary>Records where the player stands on the page, or does nothing outside the world.</summary>
    public void Record()
    {
        if (_game.Coordinates is not { } here) return;
        var page = _journal.Pages[_current];
        (page.HasLocation, page.North, page.East) = (true, here.North, here.East);
        _journal.Dirty = true;
        Refresh();
    }

    /// <summary>Start runs the page's timer from its days, hours and minutes; on a running timer the button reads Reset and stops it.</summary>
    public void StartTimer()
    {
        var page = _journal.Pages[_current];
        if (page.TimerRunning)
        {
            (page.TimerRunning, page.TimerEnd) = (false, 0);
        }
        else
        {
            var seconds = page.Days * 86400.0 + page.Hours * 3600.0 + page.Minutes * 60.0;
            if (seconds <= 0) return;
            (page.TimerRunning, page.TimerEnd) = (true, _game.Time + seconds);
        }
        _journal.Dirty = true;
        Refresh();
    }

    /// <summary>The text boxes, for tests.</summary>
    public TextBox LabelBox => _label;
    public TextBox TitleBox => _title;
    public TextBox NotesBox => _notes;
    public TextBox DaysBox => _days;
    public TextBox HoursBox => _hours;
    public TextBox MinutesBox => _minutes;
    public TextBox SearchBox => _search;

    private void Edited()
    {
        if (_loading) return;
        var page = _journal.Pages[_current];
        page.Label = _label.Text ?? string.Empty;
        page.Title = _title.Text ?? string.Empty;
        page.Notes = _notes.Text ?? string.Empty;
        page.Days = Number(_days.Text);
        page.Hours = Number(_hours.Text);
        page.Minutes = Number(_minutes.Text);
        _journal.Dirty = true;
    }

    private void DrawJournal()
    {
        var page = _journal.Pages[_current];
        Set(_location, page.LocationText);
        Set(_pageNumber, $"~ {_current + 1} ~");
        _timerEntries.IsVisible = !page.TimerRunning;
        _running.IsVisible = page.TimerRunning;
        Set(_running, page.TimerText(_game.Time));
        _start.Text = page.TimerRunning ? "Reset" : "Start";
        _previous.IsEnabled = _first.IsEnabled = _current > 0;
        _next.IsEnabled = _last.IsEnabled = _current < _journal.Pages.Count - 1;
    }

    // Page List.

    /// <summary>The pages the search finds (in the title, label or notes), in the chosen order, by their index.</summary>
    public IReadOnlyList<int> ListedPages()
    {
        var search = (_search.Text ?? string.Empty).Trim();
        var pages = _journal.Pages;
        var now = _game.Time;
        var found = Enumerable.Range(0, pages.Count).Where(i => search.Length == 0
            || new[] { pages[i].Title, pages[i].Label, pages[i].Notes }.Any(t => t.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
        found = _sortPages switch
        {
            1 => found.OrderBy(i => pages[i].Title, StringComparer.OrdinalIgnoreCase),
            2 => found.OrderBy(i => pages[i].TimerRunning ? pages[i].TimerEnd - now : double.MaxValue),
            3 => found.OrderBy(i => pages[i].Label, StringComparer.OrdinalIgnoreCase),
            _ => found
        };
        var list = found.ToList();
        if (_reversePages) list.Reverse();
        return list;
    }

    public void SortPages(int column)
    {
        _reversePages = column == _sortPages && !_reversePages;
        _sortPages = column;
        Refresh();
    }

    /// <summary>Selects a page's row; a double click opens it in the Journal tab.</summary>
    public void SelectListed(int index, bool open = false)
    {
        _listed = index;
        if (!open)
        {
            Refresh();
            return;
        }
        ShowPage(index);
        ShowTab(1);
    }

    public void DeletePage()
    {
        if (_listed < 0) return;
        _journal.Delete(_listed);
        _listed = -1;
        ShowPage(Math.Min(_current, _journal.Pages.Count - 1));
    }

    private void DrawPageList()
    {
        var listed = ListedPages();
        if (!listed.Contains(_listed)) _listed = -1;
        var pages = _journal.Pages;
        var now = _game.Time;
        Redraw(_pageList, $"{_listed}|{string.Join("|", listed.Select(i => $"{i}:{pages[i].Title}:{pages[i].TimerText(now)}:{pages[i].Label}"))}", () =>
        {
            foreach (var i in listed)
                _pageList.Children.Add(Row(i == _listed, clicks => SelectListed(i, open: clicks > 1),
                    ((i + 1).ToString(CultureInfo.InvariantCulture), 20), (pages[i].Title, 90), (pages[i].TimerText(now), 90), (pages[i].Label, 80)));
        });
        _delete.IsEnabled = _listed >= 0;
    }

    // The parts.

    private void Redraw(Panel list, string key, Action fill)
    {
        if (_drawn.TryGetValue(list, out var drawn) && drawn == key) return;
        _drawn[list] = key;
        list.Children.Clear();
        fill();
    }

    /// <summary>The column headings in retail's green; a click sorts by that column.</summary>
    private Control Headings(params (string Text, int Width)[] columns)
    {
        var grid = new Grid { ColumnDefinitions = Columns(columns.Select(c => c.Width)), Margin = new Thickness(10, 4, 24, 2) };
        for (var i = 0; i < columns.Length; i++)
        {
            var column = i;
            var heading = new GreenButton(columns[i].Text, () => { if (_tab == 0) SortContracts(column); else SortPages(column); });
            Put(grid, heading, i);
        }
        return grid;
    }

    private static ColumnDefinitions Columns(IEnumerable<int> widths) => new(string.Join(",", widths.Select(w => $"{w}*")));

    private static Control Row(bool selected, Action<int> click, params (string Text, int Width)[] cells)
    {
        var grid = new Grid { ColumnDefinitions = Columns(cells.Select(c => c.Width)), Height = RowHeight };
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = Label(cells[i].Text, 13, DerethPalette.TextBrush);
            cell.Margin = new Thickness(0, 0, 4, 0);
            Put(grid, cell, i);
        }
        var border = new Border
        {
            Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(2), Child = grid, Background = selected ? SelectedBrush : Brushes.Transparent,
            BorderBrush = selected ? DerethPalette.GoldDarkBrush : Brushes.Transparent, BorderThickness = new Thickness(0, 1)
        };
        border.PointerEntered += (_, _) => { if (!selected) border.Background = HoverBrush; };
        border.PointerExited += (_, _) => { if (!selected) border.Background = Brushes.Transparent; };
        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            click(e.ClickCount);
        };
        return border;
    }

    /// <summary>A dark list well. A click on its empty space clears the selection, as in retail.</summary>
    private static Control Well(StackPanel list, Action clear)
    {
        var scroll = new ScrollViewer
        {
            Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Background = Brushes.Transparent
        };
        scroll.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(scroll).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            clear();
        };
        return new Border
        {
            Background = WellBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2), Margin = new Thickness(6, 0), Child = scroll
        };
    }

    /// <summary>A label and its value on one line, the label column as wide as retail's.</summary>
    private static Control Line(string label, TextBlock value, double labelWidth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*"), ColumnSpacing = 4, Height = 18 };
        grid.Children.Add(Label(label, 13, DerethPalette.TextBrush));
        Put(grid, value, 1);
        return grid;
    }

    /// <summary>Retail's parchment, assembled from its art: the top and bottom strips stretched across, the edges and middle tiled down.</summary>
    private static Grid Parchment(IGameArtSource? art)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("33,*,32"), ColumnDefinitions = new ColumnDefinitions("22,*,21") };
        IBrush Piece(uint id, bool tile)
        {
            var bitmap = GameArtImageExtension.CreateBitmap(art, id);
            if (bitmap == null) return ParchmentBrush;
            return tile
                ? new ImageBrush(bitmap) { TileMode = TileMode.Tile, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                    DestinationRect = new RelativeRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height, RelativeUnit.Absolute) }
                : new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        }
        void Add(uint id, bool tile, int row, int column, int columnSpan = 1)
        {
            var piece = new Border { Background = Piece(id, tile) };
            Grid.SetRow(piece, row);
            Grid.SetColumn(piece, column);
            Grid.SetColumnSpan(piece, columnSpan);
            grid.Children.Add(piece);
        }
        Add(ParchmentTop, false, 0, 0, 3);
        Add(ParchmentLeft, true, 1, 0);
        Add(ParchmentMiddle, true, 1, 1);
        Add(ParchmentRight, true, 1, 2);
        Add(ParchmentBottom, false, 2, 0, 3);
        return grid;
    }

    /// <summary>Rows top to bottom; the one marked true takes the spare height. On the parchment it spans the art's whole grid.</summary>
    private static Grid Stack(params (Control Child, bool Fill)[] rows)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions(string.Join(",", rows.Select(row => row.Fill ? "*" : "Auto"))) };
        for (var i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i].Child, i);
            grid.Children.Add(rows[i].Child);
        }
        Grid.SetRowSpan(grid, 3);
        Grid.SetColumnSpan(grid, 3);
        return grid;
    }

    private static void Put(Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static void Set(TextBlock text, string value)
    {
        if (text.Text != value) text.Text = value;
    }

    private static int Number(string? text) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? Math.Max(0, number) : 0;

    private static TextBlock Value() => Label(string.Empty, 13, DerethPalette.CreamBrush);

    private static TextBlock Ink(string text) => Label(text, 13, InkBrush);

    /// <summary>Retail letters this panel in its serif face throughout, so every label here is Palatino.</summary>
    private static TextBlock Label(string text, double size, IBrush brush) => DerethPalette.CentreInk(new TextBlock
    {
        Text = text, FontSize = size, Foreground = brush, FontFamily = DerethPalette.Title, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    });

    private static TextBox Entry(int maxLength, Action changed)
    {
        var box = new TextBox
        {
            Foreground = DerethPalette.CreamBrush, CaretBrush = DerethPalette.GoldBrush, FontFamily = DerethPalette.Title, FontSize = 13,
            Background = DerethPalette.Brush(Color.FromArgb(0xE0, 0x0A, 0x09, 0x07)), BorderBrush = LineBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), Padding = new Thickness(5, 0), MinHeight = 0, Height = 20, MaxLength = maxLength,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) changed(); };
        return box;
    }

    /// <summary>A box on the parchment: dark ink in a faint well, as retail's journal fields are.</summary>
    private static TextBox PageEntry(int maxLength) => new()
    {
        Foreground = InkBrush, CaretBrush = InkBrush, FontFamily = DerethPalette.Title, FontSize = 13, Background = PageEntryBrush,
        BorderBrush = PageLineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1), Padding = new Thickness(3, 0),
        MinHeight = 0, Height = 20, MaxLength = maxLength, VerticalContentAlignment = VerticalAlignment.Center
    };

    private static Control Arrow(bool left) => new Path
    {
        Data = Geometry.Parse(left ? "M 0,7 L 14,0 L 14,14 Z" : "M 14,7 L 0,0 L 0,14 Z"), Fill = GreenBrush, Stroke = Brushes.Black,
        StrokeThickness = 1, Width = 15, Height = 15, Stretch = Stretch.Uniform
    };

    /// <summary>Retail's green text button: brighter under the pointer, dimmed while it can do nothing.</summary>
    private sealed class GreenButton : Border
    {
        private readonly TextBlock? _text;

        // Retail's green letters carry a dark edge, which keeps them readable on the parchment.
        public GreenButton(string text, Action click) : this(Shadowed(Label(text, 13, GreenBrush)), click) { }

        private static TextBlock Shadowed(TextBlock text)
        {
            text.Effect = new DropShadowEffect { OffsetX = 1, OffsetY = 1, BlurRadius = 1, Color = Colors.Black, Opacity = 0.9 };
            return text;
        }

        public GreenButton(Control content, Action click)
        {
            _text = content as TextBlock;
            Child = content;
            Background = Brushes.Transparent;
            Cursor = new Cursor(StandardCursorType.Hand);
            VerticalAlignment = VerticalAlignment.Center;
            PointerEntered += (_, _) => Opacity = IsEnabled ? 0.8 : Opacity;
            PointerExited += (_, _) => Opacity = IsEnabled ? 1 : Opacity;
            PointerPressed += (_, e) =>
            {
                if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                click();
            };
            PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) Opacity = IsEnabled ? 1 : 0.45; };
        }

        public string Text
        {
            get => _text?.Text ?? string.Empty;
            set { if (_text != null && _text.Text != value) _text.Text = value; }
        }
    }
}
