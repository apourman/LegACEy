using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>The UI size the settings show, in percent, and the change that sets it. The runtime keeps a size within the bounds and in steps.</summary>
public sealed record UiSizeChoice(int Percent, Action<int> Set)
{
    /// <summary>The smallest and largest UI size, and the step sizes move in, in percent.</summary>
    public const int Smallest = 60, Largest = 150, Step = 5;
}

/// <summary>The update row: the installed version (with why updates are off, if they are), and the check, when they are on.</summary>
public sealed record UpdateChoice(string Label, bool Enabled, Action Check);

/// <summary>A window the player can have as LegACEy's (custom) or retail's (native): whether it is custom now, and the switch.</summary>
public sealed record UiWindowChoice(string Title, Func<bool> IsCustom, Action<bool> SetCustom);

/// <summary>A per-character option the Settings window shows: its current value, and the change that sets it.</summary>
public sealed record OptionChoice<T>(T Value, Action<T> Set);

/// <summary>The tabs of the Settings window, in the order they show.</summary>
public enum SettingsTab { General, Windows, World, Plugins }

/// <summary>
/// The settings the Settings window shows, built by the runtime. Each is a current value and the change it asks for. <see cref="Changed"/> is
/// raised when a value moves, so an open window redraws from it.
/// </summary>
public sealed class ClientSettings
{
    private UiSizeChoice? _uiSize;
    private OptionChoice<VitalsLayout>? _vitals;
    private OptionChoice<bool>? _shortcutRow2;
    private OptionChoice<int>? _chatTextSize;
    private OptionChoice<RadarOptions>? _radar;

    /// <param name="uiSize">The UI size, for all characters. Null where the UI cannot scale, which hides the row.</param>
    /// <param name="update">The version and the check. Null hides the row.</param>
    /// <param name="windows">The windows the Windows tab switches, one card each.</param>
    public ClientSettings(UiSizeChoice? uiSize, UpdateChoice? update, IReadOnlyList<UiWindowChoice> windows)
    {
        _uiSize = uiSize;
        Update = update;
        Windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public UiSizeChoice? UiSize
    {
        get => _uiSize;
        set
        {
            _uiSize = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public UpdateChoice? Update { get; }

    public IReadOnlyList<UiWindowChoice> Windows { get; }

    /// <summary>The Vitals window's layout, for this character. Null hides its row.</summary>
    public OptionChoice<VitalsLayout>? Vitals { get => _vitals; set => Moved(ref _vitals, value); }

    /// <summary>Whether the toolbar's second shortcut row is shown, for this character. Null hides its row.</summary>
    public OptionChoice<bool>? ShortcutRow2 { get => _shortcutRow2; set => Moved(ref _shortcutRow2, value); }

    /// <summary>The chat's text size in percent, for this character. Null hides its row.</summary>
    public OptionChoice<int>? ChatTextSize { get => _chatTextSize; set => Moved(ref _chatTextSize, value); }

    /// <summary>The radar's options for this character. Null hides its rows.</summary>
    public OptionChoice<RadarOptions>? Radar { get => _radar; set => Moved(ref _radar, value); }

    /// <summary>A value moved, from the window or from elsewhere.</summary>
    public event EventHandler? Changed;

    private void Moved<T>(ref T field, T value)
    {
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The Settings window's content: tabs for General, Windows, World and Plugins over one <see cref="ClientSettings"/>. General holds the UI size
/// slider and the update check, and Windows a card per window with its switch and options; World and Plugins are empty for now. A change
/// applies at once, and <paramref name="tabChosen"/> hears each tab the player picks, so the window opens on it next time.
/// </summary>
public sealed class SettingsPanel : UserControl
{
    public const int WindowWidth = 440;
    public const int WindowHeight = 300;

    private readonly ClientSettings _settings;
    private readonly Action<SettingsTab>? _tabChosen;
    private readonly DerethSegmented _tabs;
    private readonly Control[] _panes;
    private readonly List<Action> _redraws = new();
    // A window on retail is dimmed, so its card reads as not LegACEy's.
    private const double RetailOpacity = 0.55;

    public SettingsPanel(ClientSettings settings, SettingsTab tab = SettingsTab.General, Action<SettingsTab>? tabChosen = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tabChosen = tabChosen;
        _tabs = new DerethSegmented(Enum.GetNames(typeof(SettingsTab)));
        _tabs.Chosen += index => ChooseTab((SettingsTab)index);
        _panes = new Control[] { GeneralPane(), WindowsPane(), new StackPanel(), new StackPanel() };

        var stack = new Grid { Margin = new Thickness(12, 10) };
        foreach (var pane in _panes) stack.Children.Add(pane);
        var layout = new DockPanel();
        DockPanel.SetDock(_tabs, Dock.Top);
        layout.Children.Add(_tabs);
        layout.Children.Add(new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = layout;
        ShowTab(tab);

        _settings.Changed += OnChanged;
        DetachedFromVisualTree += (_, _) => _settings.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        foreach (var redraw in _redraws) redraw();
    }

    private void ChooseTab(SettingsTab tab)
    {
        ShowTab(tab);
        _tabChosen?.Invoke(tab);
    }

    private void ShowTab(SettingsTab tab)
    {
        _tabs.Selected = (int)tab;
        for (var i = 0; i < _panes.Length; i++) _panes[i].IsVisible = i == (int)tab;
    }

    private Control GeneralPane()
    {
        var pane = new StackPanel { Spacing = 12 };
        if (_settings.UiSize != null) pane.Children.Add(UiSizeRow());
        if (_settings.Update != null) pane.Children.Add(UpdateRow(_settings.Update));
        return pane;
    }

    /// <summary>
    /// "UI size": a slider from 60 to 150 in steps of 5, for all characters. The readout follows the thumb as it is dragged, and the size is set
    /// when the pointer lets go.
    /// </summary>
    private Control UiSizeRow()
    {
        var slider = new DerethSlider(UiSizeChoice.Smallest, UiSizeChoice.Largest, UiSizeChoice.Step);
        var readout = new TextBlock { FontFamily = DerethPalette.Body, FontSize = 12.5, Foreground = DerethPalette.CreamBrush, VerticalAlignment = VerticalAlignment.Center };
        void ShowReadout(int percent) => readout.Text = $"{percent}%";
        void Show()
        {
            if (_settings.UiSize is not { } size) return;
            slider.Value = size.Percent;
            ShowReadout(size.Percent);
        }
        slider.ValueChanged += ShowReadout;
        slider.Committed += percent => _settings.UiSize?.Set(percent);
        _redraws.Add(Show);
        Show();

        var head = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right);
        head.Children.Add(readout);
        head.Children.Add(ScopedLabel("UI size"));
        return new StackPanel { Spacing = 4, Children = { head, slider } };
    }

    /// <summary>A label with the tag that says whether it applies to all characters, or to this one.</summary>
    private static Control ScopedLabel(string text, bool thisCharacter = false) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children =
        {
            new TextBlock { Text = text, FontFamily = DerethPalette.Body, FontSize = 12.5, Foreground = DerethPalette.TextBrush, VerticalAlignment = VerticalAlignment.Center },
            new DerethScopeTag(thisCharacter ? "This character" : "All characters", thisCharacter)
        }
    };

    /// <summary>The installed version, and Check for updates, greyed out with the reason on an install that never updates.</summary>
    private static Control UpdateRow(UpdateChoice choice)
    {
        var button = new DerethButton { Content = "Check for updates", IsEnabled = choice.Enabled };
        button.Click += (_, _) => choice.Check();
        ToolTip.SetTip(button, "An update installs when the game closes.");
        var row = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(new TextBlock { Text = choice.Label, FontFamily = DerethPalette.Body, Foreground = DerethPalette.TextBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>
    /// A card per window, in the order the runtime lists them. Each has its Dereth / Retail switch, for all characters. On Dereth the card shows
    /// the window's options, for this character; on retail it is dimmed and shows only the switch.
    /// </summary>
    private Control WindowsPane()
    {
        var pane = new StackPanel { Spacing = 10 };
        foreach (var window in _settings.Windows) pane.Children.Add(WindowCard(window));
        return pane;
    }

    private Control WindowCard(UiWindowChoice window)
    {
        var switchOfWindow = new DerethSegmented(new[] { "Dereth", "Retail" });
        var head = new DockPanel();
        DockPanel.SetDock(switchOfWindow, Dock.Right);
        head.Children.Add(switchOfWindow);
        head.Children.Add(ScopedLabel(window.Title));

        var options = new StackPanel { Spacing = 6 };
        foreach (var row in OptionRows(window.Title)) options.Children.Add(row);
        var card = new StackPanel { Spacing = 6, Children = { head, options } };

        void Show()
        {
            var onDereth = window.IsCustom();
            switchOfWindow.Selected = onDereth ? 0 : 1;
            options.IsVisible = onDereth;
            card.Opacity = onDereth ? 1 : RetailOpacity;
        }
        switchOfWindow.Chosen += index =>
        {
            window.SetCustom(index == 0);
            Show();
        };
        _redraws.Add(Show);
        Show();
        return card;
    }

    /// <summary>The options a window shows on Dereth. A window has a row only for the options the settings carry.</summary>
    private List<Control> OptionRows(string window)
    {
        var rows = new List<Control>();
        if (window == "Toolbar" && _settings.ShortcutRow2 != null) rows.Add(ShortcutRow2Row());
        if (window == "Chat" && _settings.ChatTextSize != null) rows.Add(ChatTextSizeRow());
        if (window == "Vitals" && _settings.Vitals != null) rows.Add(VitalsRow());
        if (window == "Radar" && _settings.Radar != null) rows.AddRange(RadarRows());
        return rows;
    }

    /// <summary>A row for an option on Dereth: its label with the "This character" tag, and its control on the right.</summary>
    private static Control OptionRow(string text, Control control)
    {
        var row = new DockPanel();
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(ScopedLabel(text, thisCharacter: true));
        return row;
    }

    private Control VitalsRow()
    {
        var layout = new DerethSegmented(new[] { "Stacked", "Side by side" });
        void Show() => layout.Selected = _settings.Vitals is { Value: VitalsLayout.SideBySide } ? 1 : 0;
        layout.Chosen += index =>
        {
            _settings.Vitals?.Set(index == 1 ? VitalsLayout.SideBySide : VitalsLayout.Stacked);
        };
        _redraws.Add(Show);
        Show();
        return OptionRow("Layout", layout);
    }

    private Control ShortcutRow2Row()
    {
        var toggle = new DerethToggle();
        void Show() => toggle.On = _settings.ShortcutRow2 is { Value: true };
        toggle.Toggled += on =>
        {
            _settings.ShortcutRow2?.Set(on);
        };
        _redraws.Add(Show);
        Show();
        return OptionRow("Second shortcut row", toggle);
    }

    /// <summary>The chat text size, stepped down or up in the chat's steps and within its bounds.</summary>
    private Control ChatTextSizeRow()
    {
        var size = new TextBlock
        {
            FontFamily = DerethPalette.Body, FontSize = 12.5, Foreground = DerethPalette.CreamBrush, MinWidth = 44,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var smaller = new DerethButton { Content = "−", Width = 26 };
        var larger = new DerethButton { Content = "+", Width = 26 };
        void Show() => size.Text = $"{_settings.ChatTextSize?.Value ?? RetailChatWindows.DefaultTextSize}%";
        void Step(int steps)
        {
            if (_settings.ChatTextSize is not { } choice) return;
            var next = choice.Value + steps * RetailChatWindows.TextSizeStep;
            choice.Set(Math.Max(RetailChatWindows.SmallestTextSize, Math.Min(RetailChatWindows.LargestTextSize, next)));
        }
        smaller.Click += (_, _) => Step(-1);
        larger.Click += (_, _) => Step(1);
        _redraws.Add(Show);
        Show();
        var stepper = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { smaller, size, larger } };
        return OptionRow("Text size", stepper);
    }

    private IEnumerable<Control> RadarRows()
    {
        yield return RadarToggle("Ground map", options => options.Ground, (options, on) => options with { Ground = on });
        yield return RadarToggle("North up", options => options.NorthUp, (options, on) => options with { NorthUp = on });
        yield return RadarToggle("Range rings", options => options.Rings, (options, on) => options with { Rings = on });
        yield return RadarRange();
    }

    /// <summary>A radar option switch: it shows the option's value and, when flipped, sets the options with the flipped one.</summary>
    private Control RadarToggle(string text, Func<RadarOptions, bool> isOn, Func<RadarOptions, bool, RadarOptions> change)
    {
        var toggle = new DerethToggle();
        void Show() => toggle.On = _settings.Radar is { } radar && isOn(radar.Value);
        toggle.Toggled += on =>
        {
            if (_settings.Radar is { } radar) radar.Set(change(radar.Value, on));
        };
        _redraws.Add(Show);
        Show();
        return OptionRow(text, toggle);
    }

    /// <summary>The radar's range, in the zoom's presets: 25, 40, 70, 110 or 150 metres.</summary>
    private Control RadarRange()
    {
        var ranges = RadarZoom.Presets;
        var range = new DerethSegmented(ranges.Select(metres => metres.ToString(CultureInfo.InvariantCulture)).ToArray());
        void Show() => range.Selected = _settings.Radar is { } radar ? Array.IndexOf(ranges, radar.Value.Zoom) : -1;
        range.Chosen += index =>
        {
            if (_settings.Radar is { } radar) radar.Set(radar.Value with { Zoom = ranges[index] });
        };
        _redraws.Add(Show);
        Show();
        return OptionRow("Range (m)", range);
    }
}
