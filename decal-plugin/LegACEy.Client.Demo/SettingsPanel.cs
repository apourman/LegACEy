using System;
using System.Collections.Generic;
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

    /// <param name="uiSize">The UI size, for all characters. Null where the UI cannot scale, which hides the row.</param>
    /// <param name="update">The version and the check. Null hides the row.</param>
    /// <param name="windows">The windows the Windows tab switches, one row each.</param>
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

    public UiSizeChoice? UiSize
    {
        get => _uiSize;
        set
        {
            _uiSize = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public WaterOption? Water
    {
        get => _water;
        set
        {
            _water = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public LockUiChoice? LockUi
    {
        get => _lockUi;
        set
        {
            _lockUi = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public UpdateChoice? Update { get; }

    public IReadOnlyList<UiWindowChoice> Windows { get; }

    /// <summary>A value moved, from the window or from elsewhere.</summary>
    public event EventHandler? Changed;
}

/// <summary>
/// The Settings window's content: tabs for General, Windows, World and Plugins over one <see cref="ClientSettings"/>. General holds the UI size
/// slider and the update check, Windows the window switches, World the water, and Plugins each plugin's menu entries, which run when picked.
/// A change applies at once, and <paramref name="tabChosen"/> hears each tab the player picks, so the window opens on it next time.
/// </summary>
public sealed class SettingsPanel : UserControl
{
    public const int WindowWidth = 440;
    public const int WindowHeight = 300;

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
        _tabs = new DerethSegmented(Enum.GetNames(typeof(SettingsTab)));
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
        if (_settings.Update != null) pane.Children.Add(UpdateRow(_settings.Update));
        return pane;
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

    /// <summary>A label with the tag that says whether it applies to all characters, or to this character.</summary>
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

    /// <summary>A row per window with a button that shows Custom or Native and switches it. The switches are the same for every character.</summary>
    private Control WindowsPane()
    {
        var pane = new StackPanel { Spacing = 6, Children = { ScopedLabel("Window switches") } };
        foreach (var window in _settings.Windows)
        {
            var button = new DerethButton { Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            void Show() => button.Content = window.IsCustom() ? "Custom" : "Native";
            _redraws.Add(Show);
            Show();
            button.Click += (_, _) =>
            {
                window.SetCustom(!window.IsCustom());
                Show();
            };
            var row = new DockPanel();
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(new TextBlock { Text = window.Title, FontFamily = DerethPalette.Body, Foreground = DerethPalette.TextBrush, VerticalAlignment = VerticalAlignment.Center });
            pane.Children.Add(row);
        }
        return pane;
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

    /// <summary>The plugins' menu entries, each plugin's under its name, as the launcher lists them. A picked entry runs.</summary>
    private Control PluginsPane()
    {
        RebuildPlugins();
        return _pluginList;
    }

    private void RebuildPlugins()
    {
        _pluginList.Children.Clear();
        if (_plugins is not { } registry) return;
        var entries = registry.VisibleMenuEntries;
        if (entries.Count == 0)
        {
            _pluginList.Children.Add(new TextBlock { Text = "No plugin menu entries.", FontFamily = DerethPalette.Body, Foreground = DerethPalette.MutedBrush });
            return;
        }
        foreach (var plugin in entries.GroupBy(entry => entry.Owner))
        {
            _pluginList.Children.Add(new TextBlock { Text = plugin.Key.Name, FontFamily = DerethPalette.Title, FontSize = 13, Foreground = DerethPalette.GoldBrush, Margin = new Thickness(0, 4, 0, 0) });
            foreach (var item in plugin)
            {
                var button = new DerethButton { Content = item.Title, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                button.Click += (_, _) => registry.RunMenuEntry(item);
                _pluginList.Children.Add(button);
            }
        }
    }
}
