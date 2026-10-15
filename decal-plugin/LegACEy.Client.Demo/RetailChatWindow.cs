using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The Dereth chat window that replaces retail's main chat and floating chat windows: one set of tabs, each a filter with its own talk
/// target, the coloured log, and the input row with the target chip, the box and Send. It draws what the port's lines say and sends what the
/// player types through the port.
/// </summary>
public sealed class RetailChatWindow : Decorator, IDisposable
{
    // The talk-target menu's height limit inside the window: it scrolls only in a window smaller than the default size.
    private const double MenuMaxHeight = 158;
    // How far a press may move before it is a drag (which selects text) rather than a click on a name.
    private const double ClickSlop = 4;
    // How far a tab press must move outside the tab strip before the tab is dragged out into a window of its own.
    private const double TearOffSlop = 6;
    // The dark shadow under each line while the window is idle, so pale lines read over bright ground once the chrome has faded.
    private static readonly IEffect LineShadow = new DropShadowDirectionEffect { Color = Colors.Black, Direction = 90, ShadowDepth = 1, BlurRadius = 1, Opacity = 1 };
    private readonly IRetailChatPort _port;
    private readonly RetailChatHistory _history;
    private readonly IRetailChatWindowHost _host;
    private readonly bool _tornOff;
    private readonly List<RetailChatTab> _tabs;
    // Each tab's unread lines and whether one of them is a new tell from someone, by tab index.
    private readonly List<(int Missed, bool Tell)> _unread;
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Height = 19 };
    private readonly Grid _tabRow;
    // The tab a press started on, until the press is a click (which shows the tab) or a drag out of the strip.
    private int _pressedTab = -1;
    private readonly StackPanel _lines = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _pill;
    private readonly TextBlock _pillText = new() { FontFamily = DerethPalette.Title, FontSize = 10, Foreground = DerethPalette.CreamBrush };
    private readonly ContentControl _chipHost = new();
    private readonly TextBox _input;
    private readonly Border _menu = new() { IsVisible = false, ZIndex = 5 };
    private readonly Border _editor = new() { IsVisible = false, ZIndex = 6 };
    private readonly DerethFrame _frame = new(DerethFrameArt.Window);
    private readonly Border _groove;
    private readonly List<Control> _chrome = new();
    private readonly RetailChatFade _fade;
    private readonly Func<bool> _uiUnlocked;
    private readonly Func<DateTime> _clock;
    private ScrollBar? _scrollBar;
    // The line loop in Refresh can stop once the lines show their final opacities; any change of state or of the lines clears this.
    private bool _linesSettled;
    private bool _awake = true;
    private int _tab;
    private RetailTalkTarget _target = RetailTalkTargets.Local;
    private int _missed;
    private bool _timestamps;
    private bool _disposed;

    /// <summary>
    /// The window's first tab that can be dragged, edited and moved: the main window's All tab is fixed at index 0, so the rest start at 1.
    /// A torn-off window has no All tab, so every tab in it moves.
    /// </summary>
    private int First => _tornOff ? 0 : 1;

    /// <param name="port">Retail's chat, as the client reaches it.</param>
    /// <param name="history">The lines kept for every window; the window shows its tabs' lines from it and takes each new one.</param>
    /// <param name="host">The windows' host: it keeps the tab lists, opens tabs dragged out, closes torn-off windows and moves windows.</param>
    /// <param name="tabs">The window's tabs; null for the six defaults.</param>
    /// <param name="tornOff">Whether the window is torn off the main window: it has no All tab and no add button, and a close medallion merges it back.</param>
    /// <param name="uiUnlocked">Whether retail's UI is unlocked for moving windows; the window does not fade while it is. Null for never.</param>
    /// <param name="clock">The time, for the idle fade; null for the system's.</param>
    public RetailChatWindow(IRetailChatPort port, RetailChatHistory history, IRetailChatWindowHost host, IReadOnlyList<RetailChatTab>? tabs = null,
        bool tornOff = false, Func<bool>? uiUnlocked = null, Func<DateTime>? clock = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _tornOff = tornOff;
        _tabs = (tabs ?? RetailChatTab.Defaults).ToList();
        _unread = _tabs.Select(_ => (0, false)).ToList();
        _timestamps = _port.TimestampsOn;
        _fade = new RetailChatFade();
        _clock = clock ?? (() => DateTime.Now);
        _uiUnlocked = uiUnlocked ?? (() => false);

        // The tab strip: the tabs, then an empty part that drags the window.
        _tabRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 19 };
        _tabRow.Children.Add(_tabStrip);
        var tabSpace = new Border { Background = Brushes.Transparent };
        tabSpace.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tabSpace).Properties.IsLeftButtonPressed) return;
            _host.MoveRequested(this);
            e.Handled = true;
        };
        Grid.SetColumn(tabSpace, 1);
        _tabRow.Children.Add(tabSpace);
        var tabBar = new Border { BorderBrush = DerethChatPalette.GoldDarkBrush, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 5), Child = _tabRow };

        // The log: a dark groove with a scroll bar, and the jump pill over its bottom edge while the player is scrolled up.
        // The groove fades with the chrome; the lines sit over it, inset by the groove's border and padding, and fade by their own age.
        _groove = new Border
        {
            Background = DerethPalette.Brush(Color.FromArgb(0x99, 0x03, 0x06, 0x09)),
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        _scroll = new ScrollViewer { Content = _lines, Margin = new Thickness(7, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _scroll.ScrollChanged += (_, _) => { if (AtEnd()) HidePill(); };
        _pill = DerethChatParts.JumpPill(_pillText);
        _pill.IsVisible = false;
        _pill.Margin = new Thickness(0, 0, 0, 6);
        _pill.PointerPressed += (_, e) => { JumpToEnd(); e.Handled = true; };
        var logArea = new Grid { Children = { _groove, _scroll, _pill }, Margin = new Thickness(0, 0, 0, 5) };

        // The input row: the chip opens the talk-target menu; the box takes text; Send sends it.
        _chipHost.PointerPressed += (_, e) => { if (e.GetCurrentPoint(_chipHost).Properties.IsLeftButtonPressed) ToggleMenu(); };
        _input = new TextBox
        {
            Watermark = "Say something…",
            Height = 19,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
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

        // The frame's edge is inside its padding, so the content keeps the frame's inset that the window had when the frame wrapped it.
        var inset = _frame.Padding;
        var content = new Grid { Margin = new Thickness(inset.Left + 9, inset.Top + 7, inset.Right + 9, inset.Bottom + 9), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        content.Children.Add(tabBar);
        Grid.SetRow(logArea, 1);
        content.Children.Add(logArea);
        Grid.SetRow(inputRow, 2);
        content.Children.Add(inputRow);
        // The menu lies over the tabs and the log, above the input row.
        Grid.SetRow(_menu, 0);
        Grid.SetRowSpan(_menu, 2);
        _menu.VerticalAlignment = VerticalAlignment.Bottom;
        _menu.HorizontalAlignment = HorizontalAlignment.Left;
        _menu.Margin = new Thickness(0, 0, 0, 26);
        content.Children.Add(_menu);
        // The tab dialog covers the whole window while it is open, centred.
        Grid.SetRow(_editor, 0);
        Grid.SetRowSpan(_editor, 3);
        content.Children.Add(_editor);
        // The frame is a sibling of the content, not its parent, so it fades without the lines fading with it. The root is hit-testable
        // all over, so the pointer counts as over the window wherever it is in the frame.
        // The jump pill keeps its own visibility: it is the cue for lines that arrived while the log was scrolled up.
        _chrome.AddRange(new Control[] { _frame, tabBar, _groove, inputRow });
        Child = new Grid { Background = Brushes.Transparent, Children = { _frame, content } };

        // A tab press that moves out of the strip is a drag; a press released on the tab is a click. Both are read from the window's own
        // events, so a press that moves outside the tab (where the tab itself gets no moves) still reaches the window.
        AddHandler(PointerMovedEvent, (_, e) => TabPointerMoved(e), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => TabPointerReleased(), RoutingStrategies.Bubble, handledEventsToo: true);

        RebuildTabs();
        // The chip shows Local until a tab picks its own target; All has none.
        SetTarget(_target);
        PickTab(0);

        _history.LineAdded += OnLineAdded;
        _port.TimestampsChanged += OnTimestampsChanged;
    }

    /// <summary>The window's tabs, in strip order. The host keeps them; a torn-off window's list is its own.</summary>
    public IReadOnlyList<RetailChatTab> Tabs => _tabs.ToArray();

    /// <summary>Whether the window is torn off the main window.</summary>
    public bool TornOff => _tornOff;

    /// <summary>Gives the box the keyboard: Enter in the game does this.</summary>
    public void FocusInput() => _input.Focus();

    /// <summary>Picks a tab, as a click does: the log shows its lines, its talk target is set and its unread count is cleared.</summary>
    public void PickTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _tab = index;
        _unread[index] = (0, false);
        RebuildTabs();
        CloseMenu();
        RebuildLog();
        var id = _tabs[index].TargetId;
        if (id == RetailChatTab.LastTellId)
        {
            if (_history.LastTellSender() is { } last) SetTarget(RetailTalkTarget.Tell(last));
        }
        else if (id != null)
        {
            // An id the window does not know falls back to Local, so a damaged tab can never throw.
            SetTarget(RetailTalkTargets.All.FirstOrDefault(target => target.Id == id) ?? RetailTalkTargets.Local);
        }
    }

    /// <summary>Sets the talk target and shows it on the chip.</summary>
    public void SetTarget(RetailTalkTarget target)
    {
        _target = target;
        _chipHost.Content = DerethChatParts.Chip(target.Label, ColourOf(target.Colour));
    }

    /// <summary>
    /// Retail's line, kept in the history: shown on this tab if the tab takes it, and counted on every other tab that takes it. A new tell
    /// from someone turns that tab's count gold. If the log is scrolled up, the line raises the jump pill.
    /// </summary>
    private void OnLineAdded(RetailChatLine line, RetailChatLine? dropped)
    {
        if (_disposed) return;
        _linesSettled = false;
        if (dropped != null && _tabs[_tab].Shows(dropped) && _lines.Children.Count > 0) _lines.Children.RemoveAt(0);
        var counted = false;
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (i == _tab || !_tabs[i].Shows(line)) continue;
            var (missed, tell) = _unread[i];
            // Gold is for a new tell on a tab that shows only tells (mockup B); Speech and All count the tell plain.
            _unread[i] = (missed + 1, tell || (line.Kind == RetailChatKind.TellIn && _tabs[i].TellsOnly));
            counted = true;
        }
        if (counted) RebuildTabs();
        if (!_tabs[_tab].Shows(line)) return;
        var atEnd = AtEnd();
        _lines.Children.Add(LineView(line));
        if (atEnd) JumpToEnd();
        else
        {
            _missed++;
            _pillText.Text = $"{_missed} new ↓";
            _pill.IsVisible = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _history.LineAdded -= OnLineAdded;
        _port.TimestampsChanged -= OnTimestampsChanged;
    }

    /// <summary>Adds tabs at the end, as a drop or a merge does, and shows the first of them. The host keeps the list.</summary>
    public void AddTabs(IReadOnlyList<RetailChatTab> tabs)
    {
        if (_disposed || tabs.Count == 0) return;
        var first = _tabs.Count;
        foreach (var tab in tabs)
        {
            _tabs.Add(tab);
            _unread.Add((0, false));
        }
        KeepTabs();
        PickTab(first);
    }

    /// <summary>Takes a tab out of the window, as a drag out does, and returns it. The host keeps the list.</summary>
    public RetailChatTab TakeTab(int index)
    {
        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        _unread.RemoveAt(index);
        CloseEditor();
        KeepTabs();
        PickTab(0);
        return tab;
    }

    /// <summary>Whether a point, in the window's own units, lies on the tab strip.</summary>
    public bool StripContains(Point point) => TabStripDistance(point) <= 0;

    /// <summary>How far a point is outside the tab strip, or infinity before the window has been laid out.</summary>
    private double TabStripDistance(Point point)
    {
        var origin = _tabRow.TranslatePoint(default, this);
        if (origin == null) return double.PositiveInfinity;
        var strip = new Rect(origin.Value, _tabRow.Bounds.Size);
        return Math.Max(Math.Max(strip.Top - point.Y, point.Y - strip.Bottom), Math.Max(strip.Left - point.X, point.X - strip.Right));
    }

    /// <summary>A tab press that moves past the slop outside the strip is a drag out: the host makes it a window of its own.</summary>
    private void TabPointerMoved(PointerEventArgs e)
    {
        if (_pressedTab < 0) return;
        // The button may be up without the window seeing its release (the pointer left the window); a move with no button held is not a drag.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressedTab = -1;
            return;
        }
        if (_pressedTab < First || TabStripDistance(e.GetPosition(this)) <= TearOffSlop) return;
        var index = _pressedTab;
        _pressedTab = -1;
        _host.TabDraggedOut(this, index);
    }

    /// <summary>A press released on its tab, without a drag out, shows the tab.</summary>
    private void TabPointerReleased()
    {
        if (_pressedTab < 0) return;
        var index = _pressedTab;
        _pressedTab = -1;
        PickTab(index);
    }

    /// <summary>
    /// Applies the idle fade to what the window shows now. The host calls it once a frame, inside its guard, since the unlock check reads
    /// retail's state; a test calls it after moving the clock.
    /// </summary>
    internal void Refresh()
    {
        if (_disposed) return;
        // The unlock check is read only when nothing else keeps the window awake. An open tab editor counts like an open menu.
        var awake = IsPointerOver || _input.IsFocused || _menu.IsVisible || _editor.IsVisible || _uiUnlocked();
        var now = _clock();
        _fade.Update(awake, now);
        if (awake != _awake)
        {
            _awake = awake;
            _linesSettled = false;
        }
        var chrome = _fade.ChromeOpacity(now);
        foreach (var part in _chrome) SetOpacity(part, chrome);
        _scrollBar ??= _scroll.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(bar => bar.Orientation == Orientation.Vertical);
        if (_scrollBar != null) SetOpacity(_scrollBar, chrome);
        if (!_linesSettled) _linesSettled = ApplyLines(now);
    }

    /// <summary>
    /// Sets each line's opacity, and its shadow, for the window's state. True when no line can change until the state or the lines do:
    /// awake, or idle with every line faded out.
    /// </summary>
    private bool ApplyLines(DateTime now)
    {
        var idle = _fade.Idle;
        var allGone = true;
        foreach (var line in _lines.Children)
        {
            if (line.Tag is not DateTime arrived) continue;
            var opacity = _fade.LineOpacity(arrived, now);
            SetOpacity(line, opacity);
            var effect = idle ? LineShadow : null;
            if (line.Effect != effect) line.Effect = effect;
            if (opacity > 0) allGone = false;
        }
        return !idle || allGone;
    }

    private static void SetOpacity(Control control, double opacity)
    {
        if (control.Opacity != opacity) control.Opacity = opacity;
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
            var (missed, tell) = _unread[i];
            var view = DerethChatParts.Tab(_tabs[i].Name, i == _tab, missed, tell);
            view.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(view).Properties.IsLeftButtonPressed)
                {
                    _pressedTab = index;
                    e.Handled = true;
                }
                // The All tab is fixed; every other tab is edited with a right click.
                else if (e.GetCurrentPoint(view).Properties.IsRightButtonPressed && index >= First)
                {
                    OpenEditor(index);
                    e.Handled = true;
                }
            };
            _tabStrip.Children.Add(view);
        }
        if (_tornOff)
        {
            // A torn-off window has no add button; its close medallion merges its tabs back into the main window.
            var close = DerethChatParts.CloseMedallion();
            close.Click += (_, _) => _host.CloseRequested(this);
            _tabStrip.Children.Add(close);
            return;
        }
        var add = DerethChatParts.AddTab();
        add.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(add).Properties.IsLeftButtonPressed) return;
            OpenEditor(-1);
            e.Handled = true;
        };
        _tabStrip.Children.Add(add);
    }

    /// <summary>
    /// The tab dialog for a new tab (<paramref name="index"/> -1) or an existing one. The main window's All tab (index 0) has none. A tab can
    /// be removed while the window keeps another, so a torn-off window is never left with no tabs.
    /// </summary>
    private void OpenEditor(int index)
    {
        var existing = index < 0 ? null : _tabs[index];
        var actions = new RetailChatTabEditActions(
            Save: tab => SaveTab(index, tab),
            Cancel: CloseEditor,
            Remove: index >= First && _tabs.Count > 1 ? (Action)(() => TakeTab(index)) : null,
            Move: index >= First ? (Action<int>)(step => MoveTab(index, step)) : null,
            CanMoveLeft: index > First,
            CanMoveRight: index >= First && index < _tabs.Count - 1);
        _editor.Child = RetailChatTabEditor.Create(existing, actions);
        _editor.IsVisible = true;
    }

    private void CloseEditor()
    {
        _editor.IsVisible = false;
        _editor.Child = null;
    }

    /// <summary>Adds the tab at the end, or replaces the one at <paramref name="index"/>, then shows it and keeps the list.</summary>
    private void SaveTab(int index, RetailChatTab tab)
    {
        if (index < 0)
        {
            _tabs.Add(tab);
            _unread.Add((0, false));
            index = _tabs.Count - 1;
        }
        else
        {
            _tabs[index] = tab;
        }
        CloseEditor();
        KeepTabs();
        PickTab(index);
    }

    private void MoveTab(int index, int step)
    {
        var to = index + step;
        (_tabs[index], _tabs[to]) = (_tabs[to], _tabs[index]);
        (_unread[index], _unread[to]) = (_unread[to], _unread[index]);
        CloseEditor();
        KeepTabs();
        PickTab(to);
    }

    private void KeepTabs() => _host.TabsChanged(this);

    private void RebuildLog()
    {
        _lines.Children.Clear();
        _linesSettled = false;
        foreach (var line in _history.Lines)
            if (_tabs[_tab].Shows(line)) _lines.Children.Add(LineView(line));
        HidePill();
        JumpToEnd();
    }

    private bool AtEnd() => _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 4;

    private void JumpToEnd()
    {
        _scroll.ScrollToEnd();
        HidePill();
    }

    private void HidePill()
    {
        _missed = 0;
        _pill.IsVisible = false;
    }

    /// <summary>
    /// One line as one selectable block: the time, the text before the sender's name, the name in its colour and the rest. A click on the
    /// name, without a drag, answers with a tell to that name; a drag selects text as usual.
    /// </summary>
    private Control LineView(RetailChatLine line)
    {
        var brush = DerethPalette.Brush(ColourOf(line.Kind));
        var italic = line.Kind == RetailChatKind.Emote;
        var view = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 15.6, Margin = new Thickness(0, 0, 0, 1), Tag = line.Time };
        var written = 0;
        void Add(Run run)
        {
            view.Inlines!.Add(run);
            written += run.Text!.Length;
        }

        if (_timestamps) Add(DerethChatParts.TimestampRun(line.Time.ToString("HH:mm") + " "));
        var rest = line.Text;
        var at = line.Sender == null ? -1 : line.Text.IndexOf(line.Sender, StringComparison.Ordinal);
        if (line.Sender != null && at >= 0)
        {
            if (at > 0) Add(DerethChatParts.TextRun(line.Text.Substring(0, at), brush, italic));
            var nameStart = written;
            Add(DerethChatParts.TextRun(line.Sender, brush, italic));
            WireNameClick(view, nameStart, written, line.Sender);
            rest = line.Text.Substring(at + line.Sender.Length);
        }
        if (rest.Length > 0) Add(DerethChatParts.TextRun(rest, brush, italic));
        return view;
    }

    /// <summary>A click on the characters from <paramref name="start"/> to <paramref name="end"/>, without a drag, tells the name.</summary>
    private void WireNameClick(SelectableTextBlock view, int start, int end, string name)
    {
        Point? pressed = null;
        // Handled events too: the text block marks its own presses handled, and the click still has to reach the name.
        view.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            pressed = e.GetCurrentPoint(view).Properties.IsLeftButtonPressed ? e.GetPosition(view) : null,
            RoutingStrategies.Bubble, handledEventsToo: true);
        view.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            var from = pressed;
            pressed = null;
            if (from is not { } origin) return;
            var to = e.GetPosition(view);
            if (Math.Abs(to.X - origin.X) + Math.Abs(to.Y - origin.Y) > ClickSlop) return;
            var hit = view.TextLayout?.HitTestPoint(to);
            if (hit is { IsInside: true } result && result.TextPosition >= start && result.TextPosition < end) TellTo(name);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
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
        else if (e.Key == Key.Tab && string.IsNullOrEmpty(_input.Text) && _history.LastTellSender() is { } last)
        {
            SetTarget(RetailTalkTarget.Tell(last));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseMenu();
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Sends the box's text as the target says it, then clears the box. Blank text, or a selection tell with nothing selected, sends nothing;
    /// so does a send retail's chat could not take, and then the text stays in the box.
    /// </summary>
    private void SendTyped()
    {
        var command = RetailChatCommands.Compose(_target, _input.Text ?? string.Empty, _port.SelectedName);
        if (command == null || !_port.Send(command)) return;
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
    /// The talk-target menu: retail's targets grouped, with the unusable ones dimmed and their reason beside them, and recent tells last. Three
    /// columns, so every group and the recent tells show at the default window size: Nearby, Fellowship and the recent tells; Allegiance;
    /// Channels.
    /// </summary>
    private Control BuildMenu()
    {
        var membership = _port.Membership;
        var selected = _port.SelectedName;
        var first = new StackPanel { Spacing = 2 };
        var allegiance = new StackPanel { Spacing = 0, Margin = new Thickness(8, 0, 0, 0) };
        var channels = new StackPanel { Spacing = 0, Margin = new Thickness(8, 0, 0, 0) };
        for (var group = 0; group < RetailTalkTargets.Groups.Count; group++)
        {
            var column = group switch { 0 or 1 => first, 2 => allegiance, _ => channels };
            var (name, targets) = RetailTalkTargets.Groups[group];
            column.Children.Add(Heading(name));
            foreach (var target in targets) column.Children.Add(MenuItem(target, membership, selected));
            if (group == 1) first.Children.Add(RecentTellsSection(membership, selected));
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { first, allegiance, channels } };
        return new Border
        {
            Padding = new Thickness(6, 5),
            CornerRadius = new CornerRadius(3),
            BorderBrush = DerethChatPalette.GoldDarkBrush,
            BorderThickness = new Thickness(1),
            Background = DerethPalette.Brush(Color.FromArgb(0xFF, 0x08, 0x10, 0x16)),
            Child = new ScrollViewer
            {
                MaxHeight = MenuMaxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = row
            }
        };
    }

    private Control RecentTellsSection(RetailChatMembership membership, string? selected)
    {
        var panel = new StackPanel { Spacing = 0 };
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
        Margin = new Thickness(6, 2, 6, 0)
    };

    private Control MenuItem(RetailTalkTarget target, RetailChatMembership membership, string? selected)
    {
        var reason = RetailTalkTargets.WhyUnusable(target, membership, selected);
        // A dimmed target says why; the selected object's name shows beside Selected, as the mockup shows it.
        var note = reason ?? (target.IsSelected ? selected : null);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = DerethPalette.Brush(ColourOf(target.Colour)), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = target.Label, FontFamily = DerethPalette.Title, FontSize = 11, Foreground = DerethPalette.CreamBrush, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        if (note != null)
        {
            var noteView = new TextBlock { Text = note, FontFamily = DerethPalette.Body, FontSize = 8, Foreground = DerethPalette.MutedBrush, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(noteView, 2);
            row.Children.Add(noteView);
        }
        var item = new Border
        {
            Height = 15,
            Padding = new Thickness(4, 0),
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
