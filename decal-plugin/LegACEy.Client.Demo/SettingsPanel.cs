using System;
using System.Collections.Generic;
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

/// <summary>The tabs of the Settings window, in the order they show.</summary>
public enum SettingsTab { General, Windows, World, Plugins }

/// <summary>
/// The settings the Settings window shows, built by the runtime. Each is a current value and the change it asks for. <see cref="Changed"/> is
/// raised when a value moves, so an open window redraws from it.
/// </summary>
public sealed class ClientSettings
{
    private UiSizeChoice? _uiSize;

    /// <param name="uiSize">The UI size, for all characters. Null where the UI cannot scale, which hides the row.</param>
    /// <param name="update">The version and the check. Null hides the row.</param>
    /// <param name="windows">The windows the Windows tab switches, one row each.</param>
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

    /// <summary>A value moved, from the window or from elsewhere.</summary>
    public event EventHandler? Changed;
}

/// <summary>
/// The Settings window's content: tabs for General, Windows, World and Plugins over one <see cref="ClientSettings"/>. General holds the UI size
/// slider and the update check, and Windows the window switches; World and Plugins are empty for now. A change applies at once, and
/// <paramref name="tabChosen"/> hears each tab the player picks, so the window opens on it next time.
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

    /// <summary>A label with the tag that says it applies to all characters.</summary>
    private static Control ScopedLabel(string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children =
        {
            new TextBlock { Text = text, FontFamily = DerethPalette.Body, FontSize = 12.5, Foreground = DerethPalette.TextBrush, VerticalAlignment = VerticalAlignment.Center },
            new DerethScopeTag("All characters", thisCharacter: false)
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
}
