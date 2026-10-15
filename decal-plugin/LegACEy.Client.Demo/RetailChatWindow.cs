using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Dereth chat window that replaces retail's main chat and floating chat windows: one set of tabs, each a filter with its own talk
/// target, the coloured log, and the input row with the target chip, the box and Send. It draws what the port's lines say and sends what the
/// player types through the port.
/// </summary>
public sealed class RetailChatWindow : Decorator, IDisposable
{
    // The talk-target menu's height limit inside the window: the log's height, so a long list scrolls.
    private const double MenuMaxHeight = 150;
    private readonly IRetailChatPort _port;
    private readonly Action? _startMove;
    private readonly IReadOnlyList<RetailChatTab> _tabs;
    private readonly RetailChatHistory _history = new();
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Height = 19 };
    private readonly StackPanel _lines = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _pill;
    private readonly TextBlock _pillText = new() { FontFamily = DerethPalette.Title, FontSize = 10, Foreground = DerethPalette.CreamBrush };
    private readonly ContentControl _chipHost = new();
    private readonly TextBox _input;
    private readonly Border _menu = new() { IsVisible = false, ZIndex = 5 };
    private int _tab;
    private RetailTalkTarget _target = RetailTalkTargets.Local;
    private string? _lastTell;
    private int _missed;
    private bool _timestamps;
    private bool _disposed;

    /// <param name="port">Retail's chat, as the client reaches it.</param>
    /// <param name="startMove">Run on a press on the tab strip's empty part, which starts the window's move; null for none.</param>
    /// <param name="tabs">The tabs; null for the six defaults.</param>
    public RetailChatWindow(IRetailChatPort port, Action? startMove = null, IReadOnlyList<RetailChatTab>? tabs = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _startMove = startMove;
        _tabs = tabs ?? RetailChatTab.Defaults;
        _timestamps = port.TimestampsOn;

        // The tab strip: the tabs, then an empty part that drags the window.
        var tabRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 19 };
        tabRow.Children.Add(_tabStrip);
        var tabSpace = new Border { Background = Brushes.Transparent };
        tabSpace.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tabSpace).Properties.IsLeftButtonPressed) return;
            _startMove?.Invoke();
            e.Handled = true;
        };
        Grid.SetColumn(tabSpace, 1);
        tabRow.Children.Add(tabSpace);
        var tabBar = new Border { BorderBrush = DerethChatPalette.GoldDarkBrush, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 5), Child = tabRow };

        // The log: a dark groove with a scroll bar, and the jump pill over its bottom edge while the player is scrolled up.
        _scroll = new ScrollViewer { Content = _lines, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _scroll.ScrollChanged += (_, _) => { if (AtEnd()) HidePill(); };
        var logBox = new Border
        {
            Background = DerethPalette.Brush(Color.FromArgb(0x99, 0x03, 0x06, 0x09)),
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3),
            Child = _scroll
        };
        _pill = DerethChatParts.JumpPill(string.Empty);
        _pill.IsVisible = false;
        _pill.Margin = new Thickness(0, 0, 0, 6);
        _pill.PointerPressed += (_, e) => { JumpToEnd(); e.Handled = true; };
        var logArea = new Grid { Children = { logBox, _pill } };
        logArea.Margin = new Thickness(0, 0, 0, 5);

        // The input row: the chip opens the talk-target menu; the box takes text; Send sends it.
        _chipHost.PointerPressed += (_, e) => { if (e.GetCurrentPoint(_chipHost).Properties.IsLeftButtonPressed) ToggleMenu(); };
        _input = new TextBox
        {
            Watermark = "Say something…",
            Height = 19,
            MinHeight = 0,
            Padding = new Thickness(6, 0),
            FontFamily = DerethPalette.Body,
            FontSize = 11,
            Foreground = DerethPalette.TextBrush,
            CaretBrush = DerethPalette.GoldBrush,
            Background = DerethPalette.GrooveBrush,
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _input.KeyDown += OnInputKeyDown;
        var send = new DerethButton { Content = "Send", Width = 40, Height = 20, Padding = new Thickness(0), FontSize = 11, Foreground = DerethPalette.CreamBrush };
        send.Click += (_, _) => SendTyped();
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4, Height = 21 };
        inputRow.Children.Add(_chipHost);
        Grid.SetColumn(_input, 1);
        inputRow.Children.Add(_input);
        Grid.SetColumn(send, 2);
        inputRow.Children.Add(send);

        var content = new Grid { Margin = new Thickness(9, 7, 9, 9), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        content.Children.Add(tabBar);
        Grid.SetRow(logArea, 1);
        content.Children.Add(logArea);
        Grid.SetRow(inputRow, 2);
        content.Children.Add(inputRow);
        // The menu lies over the log, above the input row.
        Grid.SetRow(_menu, 1);
        Grid.SetRowSpan(_menu, 2);
        _menu.VerticalAlignment = VerticalAlignment.Bottom;
        _menu.HorizontalAlignment = HorizontalAlignment.Left;
        _menu.Margin = new Thickness(0, 0, 0, 26);
        content.Children.Add(_menu);
        Child = new DerethFrame(DerethFrameArt.Window) { Child = content };

        RebuildTabs();
        SetTarget(_target);
        // Start on the first tab, which sets its target; the log is filled from the port's history, which starts empty.
        PickTab(0);

        _port.LineReceived += Receive;
        _port.TimestampsChanged += OnTimestampsChanged;
    }

    /// <summary>The input box, for the runtime's focus and for tests.</summary>
    public TextBox Input => _input;

    /// <summary>The talk target now in force.</summary>
    public RetailTalkTarget Target => _target;

    /// <summary>How many lines arrived since the log was last at its end; the jump pill shows it.</summary>
    public int MissedLines => _missed;

    /// <summary>Gives the box the keyboard: Enter in the game does this.</summary>
    public void FocusInput() => _input.Focus();

    /// <summary>Picks a tab, as a click does: the log shows its lines and its talk target is set.</summary>
    public void PickTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _tab = index;
        RebuildTabs();
        CloseMenu();
        RebuildLog();
        var id = _tabs[index].TargetId;
        if (id == RetailChatTab.LastTellId)
        {
            if (_lastTell != null) SetTarget(RetailTalkTarget.Tell(_lastTell));
        }
        else if (id != null)
        {
            SetTarget(RetailTalkTargets.All.First(target => target.Id == id));
        }
    }

    /// <summary>Sets the talk target and shows it on the chip.</summary>
    public void SetTarget(RetailTalkTarget target)
    {
        _target = target;
        _chipHost.Content = DerethChatParts.Chip(target.Label, ColourOf(target.Colour));
    }

    /// <summary>Retail's line, from the port: kept, shown on this tab if the tab takes it, and counted if the log is scrolled up.</summary>
    public void Receive(RetailChatLine line)
    {
        if (_disposed) return;
        var dropped = _history.Add(line);
        if (line.Sender != null && (line.Kind == RetailChatKind.TellIn || line.Kind == RetailChatKind.TellOut)) _lastTell = line.Sender;
        if (dropped != null && _tabs[_tab].Shows(dropped) && _lines.Children.Count > 0) _lines.Children.RemoveAt(0);
        if (!_tabs[_tab].Shows(line)) return;
        var atEnd = AtEnd();
        _lines.Children.Add(LineView(line));
        if (atEnd) JumpToEnd();
        else
        {
            _missed++;
            _pillText.Text = $"{_missed} new ↓";
            _pill.Child = _pillText;
            _pill.IsVisible = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _port.LineReceived -= Receive;
        _port.TimestampsChanged -= OnTimestampsChanged;
    }

    private void OnTimestampsChanged()
    {
        _timestamps = _port.TimestampsOn;
        RebuildLog();
    }

    private void RebuildTabs()
    {
        _tabStrip.Children.Clear();
        for (var i = 0; i < _tabs.Count; i++)
        {
            var index = i;
            var view = DerethChatParts.Tab(_tabs[i].Name, i == _tab);
            view.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(view).Properties.IsLeftButtonPressed) return;
                PickTab(index);
                e.Handled = true;
            };
            _tabStrip.Children.Add(view);
        }
    }

    private void RebuildLog()
    {
        _lines.Children.Clear();
        foreach (var line in _history.Lines)
            if (_tabs[_tab].Shows(line)) _lines.Children.Add(LineView(line));
        _missed = 0;
        HidePill();
        JumpToEnd();
    }

    private bool AtEnd() => _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 4;

    private void JumpToEnd()
    {
        _scroll.ScrollToEnd();
        _missed = 0;
        HidePill();
    }

    private void HidePill()
    {
        _missed = 0;
        _pill.IsVisible = false;
    }

    /// <summary>One line: the time, the text before the sender's name, the name (a click answers with a tell) and the rest, wrapped.</summary>
    private Control LineView(RetailChatLine line)
    {
        var colour = ColourOf(line.Kind);
        var italic = line.Kind == RetailChatKind.Emote;
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (_timestamps) head.Children.Add(DerethChatParts.Timestamp(line.Time.ToString("HH:mm")));
        var rest = line.Text;
        var at = line.Sender == null ? -1 : line.Text.IndexOf(line.Sender, StringComparison.Ordinal);
        if (at >= 0)
        {
            if (at > 0) head.Children.Add(DerethChatParts.LogText(line.Text.Substring(0, at), colour, italic));
            var name = line.Sender!;
            var nameView = new TextBlock
            {
                Text = name,
                FontFamily = DerethPalette.Body,
                FontSize = 11,
                // The log text's line height, so a name sits on the same baseline as the words around it.
                LineHeight = 15.6,
                Foreground = DerethPalette.Brush(colour),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Top
            };
            nameView.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(nameView).Properties.IsLeftButtonPressed) return;
                TellTo(name);
                e.Handled = true;
            };
            head.Children.Add(nameView);
            rest = line.Text.Substring(at + name.Length);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 1) };
        grid.Children.Add(head);
        if (rest.Length > 0)
        {
            var body = DerethChatParts.LogText(rest, colour, italic);
            body.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
        }
        return grid;
    }

    /// <summary>A click on a name, or Tab in an empty box: the next line answers with a tell to that name, and the box takes the keyboard.</summary>
    private void TellTo(string name)
    {
        SetTarget(RetailTalkTarget.Tell(name));
        _input.Focus();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendTyped();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && string.IsNullOrEmpty(_input.Text) && _lastTell != null)
        {
            SetTarget(RetailTalkTarget.Tell(_lastTell));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseMenu();
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    /// <summary>Sends the box's text as the target says it, then clears the box. Blank text, or a selection tell with nothing selected, sends nothing.</summary>
    private void SendTyped()
    {
        var command = RetailChatCommands.Compose(_target, _input.Text ?? string.Empty, _port.SelectedName);
        if (command == null) return;
        _port.Send(command);
        _input.Text = string.Empty;
    }

    private void ToggleMenu()
    {
        if (_menu.IsVisible)
        {
            CloseMenu();
            return;
        }
        _menu.Child = BuildMenu();
        _menu.IsVisible = true;
    }

    private void CloseMenu()
    {
        _menu.IsVisible = false;
        _menu.Child = null;
    }

    /// <summary>
    /// The talk-target menu: retail's targets grouped, with the unusable ones dimmed and their reason beside them, and recent tells last.
    /// Two columns, as the mockup lays them out: Nearby, Fellowship and Allegiance with the tells, then Channels.
    /// </summary>
    private Control BuildMenu()
    {
        var membership = _port.Membership;
        var selected = _port.SelectedName;
        var first = new StackPanel { Spacing = 1, Margin = new Thickness(0) };
        var second = new StackPanel { Spacing = 1, Margin = new Thickness(12, 0, 0, 0) };
        for (var group = 0; group < RetailTalkTargets.Groups.Count; group++)
        {
            var column = group == 3 ? second : first;
            var (name, targets) = RetailTalkTargets.Groups[group];
            column.Children.Add(Heading(name));
            foreach (var target in targets) column.Children.Add(MenuItem(target, membership, selected));
            if (group == 2) first.Children.Add(RecentTellsSection(membership, selected));
        }
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        columns.Children.Add(first);
        Grid.SetColumn(second, 1);
        columns.Children.Add(second);
        // Taller than the window's log when every target is listed: it scrolls inside the window rather than running past its edge.
        return new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(3),
            BorderBrush = DerethChatPalette.GoldDarkBrush,
            BorderThickness = new Thickness(1),
            Background = DerethPalette.Brush(Color.FromArgb(0xFF, 0x08, 0x10, 0x16)),
            Child = new ScrollViewer
            {
                MaxHeight = MenuMaxHeight,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = columns
            }
        };
    }

    private Control RecentTellsSection(RetailChatMembership membership, string? selected)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(Heading("Recent tells"));
        var names = _history.Lines
            .Where(line => line.Sender != null && (line.Kind == RetailChatKind.TellIn || line.Kind == RetailChatKind.TellOut))
            .Select(line => line.Sender!)
            .Reverse()
            .Distinct()
            .Take(3);
        foreach (var name in names) panel.Children.Add(MenuItem(RetailTalkTarget.Tell(name), membership, selected));
        return panel;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontFamily = DerethPalette.Body,
        FontSize = 8,
        FontWeight = FontWeight.SemiBold,
        Foreground = DerethPalette.MutedBrush,
        Margin = new Thickness(6, 4, 6, 1)
    };

    private Control MenuItem(RetailTalkTarget target, RetailChatMembership membership, string? selected)
    {
        var reason = RetailTalkTargets.WhyUnusable(target, membership, selected);
        // A dimmed target says why; the selected object's name shows beside Selected, as the mockup shows it.
        var note = reason ?? (target.IsSelected ? selected : null);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = DerethPalette.Brush(ColourOf(target.Colour)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = target.Label, FontFamily = DerethPalette.Title, FontSize = 11, Foreground = DerethPalette.CreamBrush, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        if (note != null)
        {
            var noteView = new TextBlock { Text = note, FontFamily = DerethPalette.Body, FontSize = 9, Foreground = DerethPalette.MutedBrush, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(noteView, 2);
            row.Children.Add(noteView);
        }
        var item = new Border
        {
            Height = 17,
            Padding = new Thickness(6, 0),
            CornerRadius = new CornerRadius(3),
            BorderBrush = DerethPalette.TealBrush,
            BorderThickness = new Thickness(target.Id == _target.Id ? 1 : 0),
            Opacity = reason == null ? 1 : 0.45,
            Child = row,
            Cursor = new Cursor(reason == null ? StandardCursorType.Hand : StandardCursorType.Arrow)
        };
        if (reason == null)
        {
            item.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
                SetTarget(target);
                CloseMenu();
                _input.Focus();
                e.Handled = true;
            };
        }
        return item;
    }

    /// <summary>The colour of a kind of line: one per kind, from the Dereth chat palette.</summary>
    internal static Color ColourOf(RetailChatKind kind) => kind switch
    {
        RetailChatKind.Local => DerethChatPalette.Say,
        RetailChatKind.Emote => DerethChatPalette.Emote,
        RetailChatKind.TellIn => DerethChatPalette.Tell,
        RetailChatKind.TellOut => DerethChatPalette.Tell,
        RetailChatKind.Fellowship => DerethChatPalette.Fellowship,
        RetailChatKind.Allegiance => DerethChatPalette.Allegiance,
        RetailChatKind.Channel => DerethChatPalette.Channel,
        RetailChatKind.Plugin => DerethChatPalette.Plugin,
        RetailChatKind.Combat => DerethChatPalette.Combat,
        _ => DerethChatPalette.System
    };
}
