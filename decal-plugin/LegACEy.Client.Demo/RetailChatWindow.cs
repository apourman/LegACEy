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
    private readonly ScrollViewer _tabScroll;
    // The strip's fixed end: the add button, or a torn-off window's close medallion. It stays in view however many tabs scroll.
    private readonly Border _tabEnd = new() { Margin = new Thickness(2, 0, 0, 0) };
    // Everything inside the frame: the tabs, the log and the input, drawn at the player's text size. The frame keeps the window's size.
    private readonly LayoutTransformControl _scaled = new();
    // The tab a press started on, until the press is a click (which shows the tab) or a drag out of the strip.
    private int _pressedTab = -1;
    // The log's lines. A transparent face, so a press in the gap between two lines still starts a selection.
    private readonly StackPanel _lines = new() { Background = Brushes.Transparent };
    // A selection across the log's lines: where the press was, and whether the drag has gone far enough to be one. The selected text is kept
    // for Ctrl+C and the right click.
    private (SelectableTextBlock Line, int Index)? _anchor;
    private Point _anchorPoint;
    private bool _dragging;
    private string _logSelection = string.Empty;
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
    private readonly Func<DateTime> _clock;
    private readonly Action<string> _copy;
    private readonly Func<string?> _paste;
    private ScrollBar? _scrollBar;
    // The line loop in Refresh can stop once the lines show their final opacities; any change of state or of the lines clears this.
    private bool _linesSettled;
    private bool _idle;
    private int _tab;
    private RetailTalkTarget _target = RetailTalkTargets.Local;
    private int _missed;
    // The sent line Up and Down have brought back, or null while the box holds the player's own text, kept in _draft while they browse.
    private int? _recall;
    private string _draft = string.Empty;
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
    /// <param name="clock">The time, for the idle fade; null for the system's.</param>
    /// <param name="copy">
    /// Puts text on the clipboard: text selected in the log is copied when the button goes up, and again on a right click over it; Ctrl+C and
    /// Ctrl+X copy the box's selection. Null copies nothing.
    /// </param>
    /// <param name="paste">Reads the clipboard's text, for Ctrl+V in the box. Null pastes nothing.</param>
    public RetailChatWindow(IRetailChatPort port, RetailChatHistory history, IRetailChatWindowHost host, IReadOnlyList<RetailChatTab>? tabs = null,
        bool tornOff = false, Func<DateTime>? clock = null, Action<string>? copy = null, Func<string?>? paste = null)
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
        _copy = copy ?? (_ => { });
        _paste = paste ?? (() => null);

        // The tab strip: the tabs, which scroll sideways (by the wheel) when they do not fit, and its fixed end. The strip's empty part
        // drags the window.
        _tabScroll = new ScrollViewer
        {
            Content = _tabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _tabScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            ScrollTabs(e.Delta.Y + e.Delta.X);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        var tabSpace = new Border { Background = Brushes.Transparent, Child = _tabScroll };
        tabSpace.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tabSpace).Properties.IsLeftButtonPressed) return;
            _host.MoveRequested(this);
            e.Handled = true;
        };
        _tabRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 19 };
        _tabRow.Children.Add(tabSpace);
        Grid.SetColumn(_tabEnd, 1);
        _tabRow.Children.Add(_tabEnd);
        var tabBar = new Border { BorderBrush = DerethChatPalette.GoldDarkBrush, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 5), Child = _tabRow };

        // The log: a dark groove with a scroll bar, and the jump pill over its bottom edge while the player is scrolled up.
        // The groove fades with the chrome; the lines sit over it, inset by the groove's border and padding, and fade by their own age.
        _groove = new Border
        {
            Background = DerethPalette.Brush(Color.FromArgb(0x99, 0x07, 0x07, 0x05)),
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        _scroll = new ScrollViewer { Content = _lines, Margin = new Thickness(7, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _scroll.ScrollChanged += (_, _) => { if (AtEnd()) HidePill(); };
        // The log selects as one text: a drag runs from line to line. Tunnel handlers, ahead of each line's own selection.
        _lines.AddHandler(PointerPressedEvent, OnLogPressed, RoutingStrategies.Tunnel);
        _lines.AddHandler(PointerMovedEvent, OnLogMoved, RoutingStrategies.Tunnel);
        _lines.AddHandler(PointerReleasedEvent, OnLogReleased, RoutingStrategies.Tunnel);
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
            // The chip's height, centred in the row: Send's gold line sits 1 DIP inside its 21 DIP frame, so all three edges line up.
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
        // Up and Down before the box's own caret keys, as retail's chat brings back what was sent.
        _input.AddHandler(KeyDownEvent, OnInputRecall, RoutingStrategies.Tunnel);
        // The game's clipboard, not Avalonia's: an in-game panel has none of its own.
        _input.AddHandler(KeyDownEvent, OnInputClipboard, RoutingStrategies.Tunnel);
        // As tall as the row, so no half pixel is split above and below it at a small UI size, and its word centred on its capital.
        var send = new DerethButton
        {
            Width = 40, Padding = new Thickness(0), Foreground = DerethPalette.CreamBrush,
            Content = DerethPalette.CentreInk(new TextBlock
            {
                Text = "Send", FontFamily = DerethPalette.Title, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            })
        };
        send.Click += (_, _) => SendTyped();
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4, Height = 21 };
        inputRow.Children.Add(_chipHost);
        Grid.SetColumn(_input, 1);
        inputRow.Children.Add(_input);
        Grid.SetColumn(send, 2);
        inputRow.Children.Add(send);

        // The frame's edge is inside its padding, so the content keeps the frame's inset that the window had when the frame wrapped it.
        var inset = _frame.Padding;
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        _scaled.Margin = new Thickness(inset.Left + 9, inset.Top + 7, inset.Right + 9, inset.Bottom + 9);
        _scaled.Child = content;
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
        Child = new Grid { Background = Brushes.Transparent, Children = { _frame, _scaled } };
        // Ctrl and the wheel change the text size, anywhere over the window.
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
            _host.TextSizeStepped(Math.Sign(e.Delta.Y));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

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

    /// <summary>Puts text into the box at the caret, apart from any word beside it, and gives the box the keyboard to finish the line.</summary>
    public void Insert(string text)
    {
        var typed = _input.Text ?? string.Empty;
        var at = Math.Min(Math.Max(_input.CaretIndex, 0), typed.Length);
        var with = (at > 0 && typed[at - 1] != ' ' ? " " : string.Empty) + text + (at < typed.Length && typed[at] != ' ' ? " " : string.Empty);
        _input.Text = typed.Insert(at, with);
        _input.SelectionStart = _input.SelectionEnd = _input.CaretIndex = at + with.Length;
        _input.Focus();
    }

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
            // With no tell yet, the target waits for a name: the previous tab's target must not take the line.
            SetTarget(_history.LastTell() ?? RetailTalkTarget.UnnamedTell);
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
        // Both corners, since the strip is drawn at the text size.
        var origin = _tabRow.TranslatePoint(default, this);
        var corner = _tabRow.TranslatePoint(new Point(_tabRow.Bounds.Width, _tabRow.Bounds.Height), this);
        if (origin == null || corner == null) return double.PositiveInfinity;
        var strip = new Rect(origin.Value, corner.Value);
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
    /// Applies the idle fade to what the window shows now. The host calls it once a frame; a test calls it after moving the clock.
    /// </summary>
    /// <param name="keepAwake">Whether the host keeps the window awake: it is being moved or resized.</param>
    internal void Refresh(bool keepAwake = false)
    {
        if (_disposed) return;
        // An open tab editor counts like an open menu.
        var awake = keepAwake || IsPointerOver || _input.IsFocused || _menu.IsVisible || _editor.IsVisible;
        var now = _clock();
        _fade.Update(awake, now);
        if (_fade.Idle != _idle)
        {
            _idle = _fade.Idle;
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
            _tabEnd.Child = WithTextSize(close);
            return;
        }
        var add = DerethChatParts.AddTab();
        add.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(add).Properties.IsLeftButtonPressed) return;
            OpenEditor(-1);
            e.Handled = true;
        };
        _tabEnd.Child = WithTextSize(add);
    }

    /// <summary>How long the window is left alone before it fades, in seconds; null keeps it awake. A change applies on the next frame.</summary>
    public double? IdleAfterSeconds
    {
        get => _fade.IdleAfterSeconds;
        set
        {
            _fade.IdleAfterSeconds = value;
            _linesSettled = false;
        }
    }

    /// <summary>The text size the window's content is drawn at, 1 for its design size.</summary>
    public double TextScale
    {
        get => _scaled.LayoutTransform is ScaleTransform scale ? scale.ScaleX : 1;
        set => _scaled.LayoutTransform = new ScaleTransform(value, value);
    }

    /// <summary>The strip's end with the text size button before it: a click makes the text larger, a right click smaller.</summary>
    private Control WithTextSize(Control end)
    {
        var size = DerethChatParts.TextSizeTab();
        ToolTip.SetTip(size, "Text size: click for larger, right-click for smaller (or Ctrl and the wheel)");
        size.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(size).Properties;
            if (point.IsLeftButtonPressed) _host.TextSizeStepped(1);
            else if (point.IsRightButtonPressed) _host.TextSizeStepped(-1);
            else return;
            e.Handled = true;
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { size, end } };
    }

    /// <summary>Scrolls the tabs sideways by a wheel's notches: up or left shows the earlier tabs.</summary>
    private void ScrollTabs(double notches)
    {
        var max = Math.Max(0, _tabScroll.Extent.Width - _tabScroll.Viewport.Width);
        _tabScroll.Offset = new Vector(Math.Min(max, Math.Max(0, _tabScroll.Offset.X - notches * 40)), 0);
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
        ClearLogSelection();
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
        var brush = DerethPalette.Brush(line.Type is { } type ? DerethChatPalette.OfType(type) : ColourOf(line.Kind));
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
            WireNameClick(view, nameStart, written, line.TellBack!);
            rest = line.Text.Substring(at + line.Sender.Length);
        }
        if (rest.Length > 0) Add(DerethChatParts.TextRun(rest, brush, italic));
        return view;
    }

    /// <summary>The text selected in the log, its lines joined by line breaks; empty when nothing is.</summary>
    public string LogSelection => _logSelection;

    /// <summary>Lets go of the log's selection, as a click elsewhere does.</summary>
    public void ClearLogSelection()
    {
        _anchor = null;
        _dragging = false;
        _logSelection = string.Empty;
        foreach (var child in _lines.Children)
            if (child is SelectableTextBlock line && line.SelectionStart != line.SelectionEnd) line.SelectionStart = line.SelectionEnd = 0;
    }

    private void OnLogPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_lines);
        if (point.Properties.IsRightButtonPressed)
        {
            // A right click over the log copies what is selected; the game's own right click never sees it.
            if (_logSelection.Length == 0) return;
            _copy(_logSelection);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        ClearLogSelection();
        _anchor = LogHit(point.Position);
        _anchorPoint = point.Position;
        // The lines' own selection would stop at their edge; a name's click handler still sees the press.
        e.Handled = true;
    }

    private void OnLogMoved(object? sender, PointerEventArgs e)
    {
        if (_anchor is not { } anchor || !e.GetCurrentPoint(_lines).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(_lines);
        if (!_dragging)
        {
            if (Math.Abs(at.X - _anchorPoint.X) + Math.Abs(at.Y - _anchorPoint.Y) <= ClickSlop) return;
            _dragging = true;
            // Held from here on, so the drag goes on over the scroll bar or past the log's edge.
            e.Pointer.Capture(_lines);
        }
        Select(anchor, LogHit(at));
    }

    private void OnLogReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_anchor == null || e.InitialPressMouseButton != MouseButton.Left) return;
        var dragged = _dragging;
        _anchor = null;
        _dragging = false;
        if (!dragged) return;
        e.Pointer.Capture(null);
        // Copied as the button goes up, as classic chat clients do, so it can be pasted into the box.
        if (_logSelection.Length > 0) _copy(_logSelection);
    }

    /// <summary>The line and character under a point in the log: above the first line its start, below the last its end.</summary>
    private (SelectableTextBlock Line, int Index)? LogHit(Point point)
    {
        SelectableTextBlock? last = null;
        foreach (var child in _lines.Children)
        {
            if (child is not SelectableTextBlock line) continue;
            var bounds = line.Bounds;
            if (point.Y < bounds.Top) return (line, 0);
            // A line owns the gap under it, up to the next line.
            if (point.Y < bounds.Bottom + line.Margin.Bottom)
                return (line, line.TextLayout.HitTestPoint(new Point(point.X - bounds.X, point.Y - bounds.Y)).TextPosition);
            last = line;
        }
        return last == null ? null : (last, LineText(last).Length);
    }

    /// <summary>Selects from <paramref name="from"/> to <paramref name="to"/>, either way round, across the lines between.</summary>
    private void Select((SelectableTextBlock Line, int Index) from, (SelectableTextBlock Line, int Index)? to)
    {
        if (to is not { } end) return;
        int first = _lines.Children.IndexOf(from.Line), last = _lines.Children.IndexOf(end.Line);
        // A line gone from the log, as the oldest goes when the history is full, ends the selection.
        if (first < 0 || last < 0)
        {
            ClearLogSelection();
            return;
        }
        var (start, finish) = first < last || first == last && from.Index <= end.Index ? (from, end) : (end, from);
        (first, last) = (Math.Min(first, last), Math.Max(first, last));
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < _lines.Children.Count; i++)
        {
            if (_lines.Children[i] is not SelectableTextBlock line) continue;
            if (i < first || i > last)
            {
                if (line.SelectionStart != line.SelectionEnd) line.SelectionStart = line.SelectionEnd = 0;
                continue;
            }
            var whole = LineText(line);
            var head = i == first ? Math.Min(start.Index, whole.Length) : 0;
            var tail = i == last ? Math.Min(finish.Index, whole.Length) : whole.Length;
            line.SelectionStart = head;
            line.SelectionEnd = tail;
            if (i > first) text.Append("\r\n");
            text.Append(whole, head, tail - head);
        }
        _logSelection = text.ToString();
    }

    private static string LineText(SelectableTextBlock line) => line.Inlines?.Text ?? line.Text ?? string.Empty;

    /// <summary>A click on the characters from <paramref name="start"/> to <paramref name="end"/>, without a drag, tells the name.</summary>
    private void WireNameClick(SelectableTextBlock view, int start, int end, RetailTalkTarget tell)
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
            if (hit is { IsInside: true } result && result.TextPosition >= start && result.TextPosition < end) TellTo(tell);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>A click on a name, or Tab in an empty box: the next line answers with a tell to that name, and the box takes the keyboard.</summary>
    private void TellTo(RetailTalkTarget tell)
    {
        SetTarget(tell);
        _input.Focus();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && string.IsNullOrWhiteSpace(_input.Text))
        {
            // Enter in a blank box leaves it, as retail's chat key toggles its entry.
            LeaveInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SendTyped();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && string.IsNullOrEmpty(_input.Text) && _history.LastTell() is { } last)
        {
            SetTarget(last);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            LeaveInput();
            e.Handled = true;
        }
    }

    private void OnInputClipboard(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Key is not (Key.C or Key.X or Key.V)) return;
        e.Handled = true;
        var text = _input.Text ?? string.Empty;
        int start = Math.Min(_input.SelectionStart, _input.SelectionEnd), end = Math.Max(_input.SelectionStart, _input.SelectionEnd);
        if (e.Key != Key.V)
        {
            // Nothing selected in the box: Ctrl+C copies what is selected in the log.
            if (end == start)
            {
                if (e.Key == Key.C && _logSelection.Length > 0) _copy(_logSelection);
                return;
            }
            _copy(text.Substring(start, end - start));
            if (e.Key == Key.C) return;
            Replace(string.Empty);
            return;
        }
        // One line: a pasted line break or tab becomes a space.
        if (_paste() is { Length: > 0 } pasted) Replace(pasted.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '));

        void Replace(string with)
        {
            _input.Text = text.Substring(0, start) + with + text.Substring(end);
            _input.SelectionStart = _input.SelectionEnd = _input.CaretIndex = start + with.Length;
        }
    }

    private void OnInputRecall(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Up && e.Key != Key.Down) return;
        e.Handled = true;
        var sent = _history.Sent;
        var at = Math.Min(_recall ?? sent.Count, sent.Count);
        var to = e.Key == Key.Up ? Math.Max(0, at - 1) : Math.Min(sent.Count, at + 1);
        if (to == at) return;
        if (_recall == null) _draft = _input.Text ?? string.Empty;
        _recall = to == sent.Count ? null : to;
        _input.Text = _recall is { } line ? sent[line] : _draft;
        _input.CaretIndex = _input.Text.Length;
    }

    private void LeaveInput()
    {
        CloseMenu();
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
    }

    /// <summary>
    /// Sends the box's text as the target says it, then clears the box and leaves it, as retail's chat entry does: Enter or a click starts
    /// the next line. Blank text, or a selection tell with nothing selected, sends nothing;
    /// so does a send retail's chat could not take, and then the text stays in the box.
    /// </summary>
    private void SendTyped()
    {
        var typed = _input.Text ?? string.Empty;
        var direct = RetailChatCommands.DirectTo(_target, typed, _port.SelectedId);
        if (direct != 0)
        {
            if (!_port.TellDirect(direct, typed.Trim())) return;
        }
        else if (RetailChatCommands.Compose(_target, typed, _port.SelectedName) is not { } command || !_port.Send(command)) return;
        _history.AddSent(typed.Trim());
        _recall = null;
        _input.Text = string.Empty;
        LeaveInput();
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
            Background = DerethPalette.Brush(Color.FromArgb(0xFF, 0x18, 0x16, 0x0F)),
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
        foreach (var tell in _history.RecentTells(3)) panel.Children.Add(MenuItem(tell, membership, selected));
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

    /// <summary>The colour of a kind of line, for a line without retail's chat type: one per kind, from the Dereth chat palette.</summary>
    internal static Color ColourOf(RetailChatKind kind) => kind switch
    {
        RetailChatKind.Local => DerethChatPalette.Say,
        RetailChatKind.Emote => DerethChatPalette.Emote,
        RetailChatKind.TellIn => DerethChatPalette.Tell,
        RetailChatKind.TellOut => DerethChatPalette.TellOut,
        RetailChatKind.Fellowship => DerethChatPalette.Fellowship,
        RetailChatKind.Allegiance => DerethChatPalette.Allegiance,
        RetailChatKind.Channel => DerethChatPalette.Channel,
        RetailChatKind.Plugin => DerethChatPalette.Plugin,
        RetailChatKind.Combat => DerethChatPalette.Combat,
        _ => DerethChatPalette.System
    };
}
