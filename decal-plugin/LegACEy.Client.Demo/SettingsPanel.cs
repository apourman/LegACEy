using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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

/// <summary>The water the World tab shows: on or off, and the strength in percent, and the change that sets both. The runtime keeps the strength within the bounds.</summary>
public sealed record WaterOption(bool On, int Percent, Action<bool, int> Set)
{
    /// <summary>The smallest and largest strength, and the step the slider moves in, in percent.</summary>
    public const int Smallest = 0, Largest = 200, Step = 5;
}

/// <summary>Retail's Lock UI for this character: whether it is on now, whether it can be set, and the change that sets it. Greyed out when it can't.</summary>
public sealed record LockUiChoice(bool Locked, bool Enabled, Action<bool> Set);

/// <summary>The tabs of the Settings window, in the order they show.</summary>
public enum SettingsTab { General, Windows, World, Plugins }

/// <summary>
/// The settings the Settings window shows, built by the runtime. Each is a current value and the change it asks for. <see cref="Changed"/> is
/// raised when a value moves, so an open window redraws from it.
/// </summary>
public sealed class ClientSettings
{
    private UiSizeChoice? _uiSize;
    private WaterOption? _water;
    private LockUiChoice? _lockUi;
    private OptionChoice<VitalsLayout>? _vitals;
    private OptionChoice<bool>? _shortcutRow2;
    private OptionChoice<int>? _chatTextSize;
    private OptionChoice<int>? _chatFade;
    private OptionChoice<RadarOptions>? _radar;

    /// <param name="uiSize">The UI size, for all characters. Null where the UI cannot scale, which hides the row.</param>
    /// <param name="update">The version and the check. Null hides the row.</param>
    /// <param name="windows">The windows the Windows tab switches, one card each.</param>
    /// <param name="water">The water, for all characters. Null hides the World tab's rows.</param>
    /// <param name="lockUi">Retail's Lock UI, for this character. Null hides its row.</param>
    public ClientSettings(UiSizeChoice? uiSize, UpdateChoice? update, IReadOnlyList<UiWindowChoice> windows, WaterOption? water = null, LockUiChoice? lockUi = null)
    {
        _uiSize = uiSize;
        Update = update;
        Windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _water = water;
        _lockUi = lockUi;
    }

    public UiSizeChoice? UiSize { get => _uiSize; set => Moved(ref _uiSize, value); }

    public WaterOption? Water { get => _water; set => Moved(ref _water, value); }

    public LockUiChoice? LockUi { get => _lockUi; set => Moved(ref _lockUi, value); }

    public UpdateChoice? Update { get; }

    public IReadOnlyList<UiWindowChoice> Windows { get; }

    /// <summary>The Vitals window's layout, for this character. Null hides its row.</summary>
    public OptionChoice<VitalsLayout>? Vitals { get => _vitals; set => Moved(ref _vitals, value); }

    /// <summary>Whether the toolbar's second shortcut row is shown, for this character. Null hides its row.</summary>
    public OptionChoice<bool>? ShortcutRow2 { get => _shortcutRow2; set => Moved(ref _shortcutRow2, value); }

    /// <summary>The chat's text size in percent, for this character. Null hides its row.</summary>
    public OptionChoice<int>? ChatTextSize { get => _chatTextSize; set => Moved(ref _chatTextSize, value); }

    /// <summary>The chat's idle fade delay in seconds, for this character; <see cref="RetailChatWindows.NeverFades"/> (0) never fades. Null hides its row.</summary>
    public OptionChoice<int>? ChatFade { get => _chatFade; set => Moved(ref _chatFade, value); }

    /// <summary>The radar's options for this character. Null hides its rows.</summary>
    public OptionChoice<RadarOptions>? Radar { get => _radar; set => Moved(ref _radar, value); }

    /// <summary>Puts this character's LegACEy windows back where they first opened, once the player confirms. Null hides its row.</summary>
    public Action? ResetPositions { get; init; }

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
/// slider and the update check, Windows a card per window with its switch and options, World the water, and Plugins each plugin's menu
/// entries, which run when picked. A change applies at once, and <paramref name="tabChosen"/> hears each tab the player picks, so the window
/// opens on it next time.
/// </summary>
public sealed class SettingsPanel : UserControl
{
    public const int WindowWidth = 440;
    public const int WindowHeight = 396;

    private readonly ClientSettings _settings;
    private readonly Action<SettingsTab>? _tabChosen;
    private readonly PluginRegistry? _plugins;
    private readonly DerethSegmented _tabs;
    private readonly Control[] _panes;
    private readonly StackPanel _pluginList = new() { Spacing = 6 };
    private readonly List<Action> _redraws = new();

    public SettingsPanel(ClientSettings settings, SettingsTab tab = SettingsTab.General, Action<SettingsTab>? tabChosen = null, PluginRegistry? plugins = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tabChosen = tabChosen;
        _plugins = plugins;
        // Inset like the cards below it, clear of the window frame.
        _tabs = new DerethSegmented(Enum.GetNames(typeof(SettingsTab))) { Margin = new Thickness(12, 8, 12, 2) };
        _tabs.Chosen += index => ChooseTab((SettingsTab)index);
        _panes = new Control[] { GeneralPane(), WindowsPane(), WorldPane(), PluginsPane() };

        var stack = new Grid { Margin = new Thickness(12, 10) };
        foreach (var pane in _panes) stack.Children.Add(pane);
        var layout = new DockPanel();
        DockPanel.SetDock(_tabs, Dock.Top);
        layout.Children.Add(_tabs);
        layout.Children.Add(new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = layout;
        ShowTab(tab);

        _settings.Changed += OnChanged;
        if (_plugins != null) _plugins.MenuChanged += OnMenuChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            _settings.Changed -= OnChanged;
            if (_plugins != null) _plugins.MenuChanged -= OnMenuChanged;
        };
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        foreach (var redraw in _redraws) redraw();
    }

    private void OnMenuChanged(object? sender, EventArgs e) => RebuildPlugins();

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
        if (_settings.LockUi != null) pane.Children.Add(LockRow());
        if (_settings.ResetPositions != null) pane.Children.Add(ResetPositionsRow(_settings.ResetPositions));
        if (_settings.Update != null) pane.Children.Add(UpdateRow(_settings.Update));
        return pane;
    }

    /// <summary>
    /// "Window positions", for this character: a button that shows an inline confirm row, Reset and Cancel, instead of a dialog. Cancel hides
    /// it and changes nothing; Reset hides it and calls <paramref name="reset"/> once.
    /// </summary>
    private static Control ResetPositionsRow(Action reset)
    {
        var question = new TextBlock { Text = "Put every window back where it first opened?", FontFamily = DerethPalette.Body, FontSize = 12.5, Foreground = DerethPalette.TextBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var cancel = new DerethButton { Content = "Cancel" };
        var confirmReset = new DerethButton { Content = "Reset" };
        var confirm = new DockPanel { IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(confirmReset, Dock.Right);
        confirm.Children.Add(cancel);
        confirm.Children.Add(confirmReset);
        confirm.Children.Add(question);
        cancel.Click += (_, _) => confirm.IsVisible = false;
        confirmReset.Click += (_, _) =>
        {
            confirm.IsVisible = false;
            reset();
        };

        var open = new DerethButton { Content = "Reset window positions" };
        open.Click += (_, _) => confirm.IsVisible = true;
        var row = new DockPanel();
        DockPanel.SetDock(open, Dock.Right);
        row.Children.Add(open);
        row.Children.Add(ScopedLabel("Window positions", thisCharacter: true));
        return new StackPanel { Children = { row, confirm } };
    }

    /// <summary>
    /// "UI size": a slider from 60 to 150 in steps of 5, for all characters. The readout follows the thumb as it is dragged, and the size is set
    /// when the pointer lets go.
    /// </summary>
    private Control UiSizeRow() => SliderRow("UI size", UiSizeChoice.Smallest, UiSizeChoice.Largest, UiSizeChoice.Step,
        () => _settings.UiSize?.Percent, percent => _settings.UiSize?.Set(percent));

    /// <summary>
    /// A labelled slider for all characters, with the percent beside it. The readout follows the thumb as it is dragged, and <paramref name="commit"/>
    /// gets the value when the pointer lets go. <paramref name="current"/> is the value to show, or null when the setting is gone.
    /// </summary>
    private Control SliderRow(string label, int smallest, int largest, int step, Func<int?> current, Action<int> commit)
    {
        var slider = new DerethSlider(smallest, largest, step);
        var readout = new TextBlock { FontFamily = DerethPalette.Body, FontSize = 12.5, Foreground = DerethPalette.CreamBrush, VerticalAlignment = VerticalAlignment.Center };
        void ShowReadout(int percent) => readout.Text = $"{percent}%";
        void Show()
        {
            if (current() is not { } percent) return;
            slider.Value = percent;
            ShowReadout(percent);
        }
        slider.ValueChanged += ShowReadout;
        slider.Committed += commit;
        _redraws.Add(Show);
        Show();

        var head = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right);
        head.Children.Add(readout);
        head.Children.Add(ScopedLabel(label));
        return new StackPanel { Spacing = 4, Children = { head, slider } };
    }

    /// <summary>A label with the tag that says whether it applies to all characters, or to this one.</summary>
    private static Control ScopedLabel(string text, bool thisCharacter = false) => ScopedLabel(
        new TextBlock { Text = text, FontFamily = DerethPalette.Body, FontSize = 12.5, Foreground = DerethPalette.TextBrush, VerticalAlignment = VerticalAlignment.Center },
        thisCharacter);

    /// <summary>The label as the caller styled it, with the tag that says whether it applies to all characters, or to this one.</summary>
    private static Control ScopedLabel(TextBlock label, bool thisCharacter = false) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { label, new DerethScopeTag(thisCharacter ? "This character" : "All characters", thisCharacter) }
    };

    /// <summary>The installed version, and Check for updates, greyed out with the reason on an install that never updates.</summary>
    private static Control UpdateRow(UpdateChoice choice)
    {
        var button = new DerethButton { Content = "Check for updates", IsEnabled = choice.Enabled };
        button.Click += (_, _) => choice.Check();
        ToolTip.SetTip(button, "Nothing installs until you choose to update.");
        var row = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(new TextBlock { Text = choice.Label, FontFamily = DerethPalette.Body, Foreground = DerethPalette.TextBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>
    /// A card per window, in the order the runtime lists them. Each has its Dereth / Retail switch, for all characters. On Dereth the card shows
    /// the window's options, for this character. On retail its frame is dashed, its title muted and its Retail choice lit blue, and it shows
    /// only the switch.
    /// </summary>
    private Control WindowsPane()
    {
        var pane = new StackPanel { Spacing = 10 };
        foreach (var window in _settings.Windows) pane.Children.Add(WindowCard(window));
        return pane;
    }

    private Control WindowCard(UiWindowChoice window)
    {
        var switchOfWindow = new DerethSegmented(new[] { "Dereth", "Retail" }, blueOption: 1);
        var head = new DockPanel();
        DockPanel.SetDock(switchOfWindow, Dock.Right);
        head.Children.Add(switchOfWindow);
        var title = new TextBlock
        {
            Text = window.Title, FontFamily = DerethPalette.Title, FontSize = 15, Foreground = DerethPalette.CreamBrush, VerticalAlignment = VerticalAlignment.Center
        };
        head.Children.Add(ScopedLabel(title));

        var options = new StackPanel { Spacing = 6 };
        foreach (var row in OptionRows(window.Title)) options.Children.Add(row);
        var frame = new Rectangle
        {
            Fill = DerethPalette.Brush(Color.FromArgb(0x2E, 0, 0, 0)), Stroke = DerethPalette.Brush(DerethPalette.Line), StrokeThickness = 1,
            RadiusX = 3, RadiusY = 3, Stretch = Stretch.Fill, IsHitTestVisible = false
        };
        var card = new Grid { Children = { frame, new StackPanel { Spacing = 6, Margin = new Thickness(10, 8), Children = { head, options } } } };

        void Show()
        {
            var onDereth = window.IsCustom();
            switchOfWindow.Selected = onDereth ? 0 : 1;
            options.IsVisible = onDereth;
            title.Foreground = onDereth ? DerethPalette.CreamBrush : DerethPalette.MutedBrush;
            frame.StrokeDashArray = onDereth ? null : new AvaloniaList<double> { 3, 2 };
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
        if (window == "Chat" && _settings.ChatFade != null) rows.Add(ChatFadeRow());
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
        layout.Chosen += index => _settings.Vitals?.Set(index == 1 ? VitalsLayout.SideBySide : VitalsLayout.Stacked);
        _redraws.Add(Show);
        Show();
        return OptionRow("Layout", layout);
    }

    private Control ShortcutRow2Row()
    {
        var toggle = new DerethToggle();
        void Show() => toggle.On = _settings.ShortcutRow2 is { Value: true };
        toggle.Changed += on => _settings.ShortcutRow2?.Set(on);
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
        void Show()
        {
            if (_settings.ChatTextSize is { } choice) size.Text = $"{choice.Value}%";
        }
        void Step(int steps)
        {
            if (_settings.ChatTextSize is not { } choice) return;
            var next = choice.Value + steps * RetailChatWindows.TextSizeStep;
            choice.Set(RetailChatWindows.ClampTextSize(next));
        }
        smaller.Click += (_, _) => Step(-1);
        larger.Click += (_, _) => Step(1);
        _redraws.Add(Show);
        Show();
        var stepper = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { smaller, size, larger } };
        return OptionRow("Text size", stepper);
    }

    /// <summary>How long the chat waits before it fades when left alone. Never is 0 seconds; Instant fades as soon as it is left.</summary>
    private Control ChatFadeRow()
    {
        // Labelled as the mockup does: only the longest choice carries its unit.
        var fade = new DerethSegmented(RetailChatWindows.FadeChoices.Select(seconds => seconds switch
        {
            RetailChatWindows.NeverFades => "Never",
            RetailChatWindows.FadesInstantly => "Instant",
            60 => "60 s",
            _ => seconds.ToString(CultureInfo.InvariantCulture),
        }).ToArray());
        void Show() => fade.Selected = _settings.ChatFade is { } choice ? RetailChatWindows.FadeChoices.ToList().IndexOf(choice.Value) : -1;
        fade.Chosen += index => _settings.ChatFade?.Set(RetailChatWindows.FadeChoices[index]);
        _redraws.Add(Show);
        Show();
        // Six choices leave no room beside the label, so they sit on their own line under it.
        fade.HorizontalAlignment = HorizontalAlignment.Right;
        return new StackPanel { Spacing = 6, Children = { ScopedLabel("Fade when idle", thisCharacter: true), fade } };
    }

    private IEnumerable<Control> RadarRows()
    {
        yield return RadarToggle("Show map", options => options.Map, (options, on) => options with { Map = on });
        yield return RadarToggle("North up", options => options.NorthUp, (options, on) => options with { NorthUp = on });
        yield return RadarToggle("Range rings", options => options.Rings, (options, on) => options with { Rings = on });
        yield return RadarRange();
    }

    /// <summary>A radar option switch: it shows the option's value and, when flipped, sets the options with the flipped one.</summary>
    private Control RadarToggle(string text, Func<RadarOptions, bool> isOn, Func<RadarOptions, bool, RadarOptions> change)
    {
        var toggle = new DerethToggle();
        void Show() => toggle.On = _settings.Radar is { } radar && isOn(radar.Value);
        toggle.Changed += on => _settings.Radar?.Set(change(_settings.Radar.Value, on));
        _redraws.Add(Show);
        Show();
        return OptionRow(text, toggle);
    }

    /// <summary>The radar's range, one of the zoom's presets. A range the wheel set between them lights the nearest preset.</summary>
    private Control RadarRange()
    {
        var ranges = RadarZoom.Presets;
        var range = new DerethSegmented(ranges.Select(metres => metres.ToString(CultureInfo.InvariantCulture)).ToArray());
        void Show() => range.Selected = _settings.Radar is { } radar
            ? Enumerable.Range(0, ranges.Count).OrderBy(i => Math.Abs(ranges[i] - radar.Value.Zoom)).First()
            : -1;
        range.Chosen += index => _settings.Radar?.Set(_settings.Radar.Value with { Zoom = ranges[index] });
        _redraws.Add(Show);
        Show();
        // Only the outdoor zoom: the indoor one is set from the radar's own options, beside the plan it zooms.
        return OptionRow("Outdoor range (m)", range);
    }

    /// <summary>
    /// A labelled switch with its scope tag. <paramref name="current"/> gives the state to show and whether the switch can be set, or null when
    /// the setting is gone; <paramref name="commit"/> gets each state the player clicks. The switch is greyed out where it can't be set.
    /// </summary>
    private Control SwitchRow(string label, bool thisCharacter, Func<(bool On, bool Enabled)?> current, Action<bool> commit)
    {
        var toggle = new DerethToggle();
        var row = new DockPanel();
        void Show()
        {
            if (current() is not { } state) return;
            toggle.On = state.On;
            toggle.IsEnabled = state.Enabled;
            row.Opacity = state.Enabled ? 1 : 0.5;
        }
        toggle.Changed += commit;
        _redraws.Add(Show);
        Show();

        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(ScopedLabel(label, thisCharacter));
        return row;
    }

    /// <summary>Lock UI, for this character: retail's own lock, shown as retail reads it and set as retail's radar sets it.</summary>
    private Control LockRow() => SwitchRow("Lock UI", thisCharacter: true,
        () => _settings.LockUi is { } lockUi ? (lockUi.Locked, lockUi.Enabled) : null,
        on => _settings.LockUi?.Set(on));

    /// <summary>
    /// The water, for all characters: a switch for on or off, and the strength from calm to 200%, with a readout that follows the thumb. The
    /// switch is the same setting as /water on and off; moving the strength turns the water on, as /water with a percent does.
    /// </summary>
    private Control WorldPane()
    {
        var pane = new StackPanel { Spacing = 12 };
        if (_settings.Water == null) return pane;
        pane.Children.Add(WaterSwitchRow());
        pane.Children.Add(WaterStrengthRow());
        return pane;
    }

    private Control WaterSwitchRow() => SwitchRow("Water", thisCharacter: false,
        () => _settings.Water is { } water ? (water.On, true) : null,
        on => { if (_settings.Water is { } water) water.Set(on, water.Percent); });

    private Control WaterStrengthRow() => SliderRow("Wave strength", WaterOption.Smallest, WaterOption.Largest, WaterOption.Step,
        () => _settings.Water?.Percent, percent => _settings.Water?.Set(true, percent));

    /// <summary>The visible plugins by name, each with its menu entries as the launcher lists them. A picked entry runs.</summary>
    private Control PluginsPane()
    {
        RebuildPlugins();
        return _pluginList;
    }

    private void RebuildPlugins()
    {
        _pluginList.Children.Clear();
        if (_plugins is not { } registry) return;
        var plugins = registry.VisiblePlugins;
        if (plugins.Count == 0)
        {
            _pluginList.Children.Add(new TextBlock { Text = "No plugins loaded.", FontFamily = DerethPalette.Body, Foreground = DerethPalette.MutedBrush });
            return;
        }
        foreach (var plugin in plugins)
        {
            _pluginList.Children.Add(new TextBlock { Text = plugin.Name, FontFamily = DerethPalette.Title, FontSize = 13, Foreground = DerethPalette.GoldBrush, Margin = new Thickness(0, 4, 0, 0) });
            foreach (var item in plugin.MenuEntries)
            {
                var button = new DerethButton { Content = item.Title, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                button.Click += (_, _) => registry.RunMenuEntry(item);
                _pluginList.Children.Add(button);
            }
        }
    }
}
