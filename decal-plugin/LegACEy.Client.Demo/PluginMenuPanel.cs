using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>The UI size the menu shows, in percent, and the step it asks for (1 larger, -1 smaller).</summary>
public sealed record UiSizeChoice(int Percent, Action<int> Step);

/// <summary>The menu's update row: the installed version (with why updates are off, if they are), and the check, when they are on.</summary>
public sealed record UpdateChoice(string Label, bool Enabled, Action Check);

/// <summary>A window the player can have as LegACEy's (custom) or retail's (native): whether it is custom now, and the switch.</summary>
public sealed record UiWindowChoice(string Title, Func<bool> IsCustom, Action<bool> SetCustom);

/// <summary>
/// The LegACEy menu: the windows the player can switch between custom and native, one row per menu entry of a visible plugin, and the
/// UI size and update check at the bottom. Picking a plugin row runs that entry.
/// </summary>
public sealed class PluginMenuPanel : UserControl
{
    public const int WindowWidth = 220;
    public const int WindowHeight = 280;

    private readonly PluginRegistry _registry;
    private readonly IGameArtSource? _art;
    private readonly StackPanel _rows = new();
    private readonly List<WriteableBitmap> _icons = new();

    public PluginMenuPanel(PluginRegistry registry, IGameArtSource? art, UiSizeChoice? uiSize = null, IReadOnlyList<UiWindowChoice>? windows = null,
        UpdateChoice? update = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _art = art;
        var layout = new DockPanel();
        if (update != null)
        {
            var footer = UpdateRow(update);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
        }
        if (uiSize != null)
        {
            var footer = UiSizeRow(uiSize);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
        }
        if (windows is { Count: > 0 })
        {
            var section = WindowsSection(windows);
            DockPanel.SetDock(section, Dock.Top);
            layout.Children.Add(section);
        }
        layout.Children.Add(new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = layout;
        Rebuild();
        registry.MenuChanged += OnMenuChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            _registry.MenuChanged -= OnMenuChanged;
            DisposeIcons();
        };
    }

    private void OnMenuChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        _rows.Children.Clear();
        DisposeIcons();
        foreach (var item in _registry.VisibleMenuEntries)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var icon = GameArtImageExtension.CreateBitmap(_art, item.IconId);
            if (icon != null)
            {
                _icons.Add(icon);
                row.Children.Add(new Image { Source = icon, Width = 20, Height = 20, Stretch = Stretch.Uniform });
            }
            row.Children.Add(new TextBlock { Text = item.Title, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });

            var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 2) };
            button.Click += (_, _) => _registry.RunMenuEntry(item);
            _rows.Children.Add(button);
        }
    }

    /// <summary>"Windows": a row per window with a button that shows Custom or Native and switches it.</summary>
    private static Control WindowsSection(IReadOnlyList<UiWindowChoice> windows)
    {
        var section = new StackPanel { Margin = new Thickness(8, 4, 8, 6) };
        section.Children.Add(new TextBlock { Text = "Windows", Foreground = Brushes.White, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var window in windows)
        {
            var button = new Button { Width = 70, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1) };
            void Show() => button.Content = window.IsCustom() ? "Custom" : "Native";
            Show();
            button.Click += (_, _) =>
            {
                window.SetCustom(!window.IsCustom());
                Show();
            };
            var row = new DockPanel();
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(new TextBlock { Text = window.Title, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            section.Children.Add(row);
        }
        return section;
    }

    private static Control UiSizeRow(UiSizeChoice choice)
    {
        Button Step(string text, int steps)
        {
            var button = new Button { Content = text, Width = 26, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 0) };
            button.Click += (_, _) => choice.Step(steps);
            return button;
        }
        var row = new DockPanel { Margin = new Thickness(8, 4) };
        var label = new TextBlock { Text = "UI size", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(Step("-", -1));
        controls.Children.Add(new TextBlock { Text = $"{choice.Percent}%", Foreground = Brushes.White, Width = 40, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(Step("+", 1));
        DockPanel.SetDock(controls, Dock.Right);
        row.Children.Add(controls);
        row.Children.Add(label);
        return row;
    }

    private static Control UpdateRow(UpdateChoice choice)
    {
        var button = new Button { Content = "Check", IsEnabled = choice.Enabled, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 0) };
        button.Click += (_, _) => choice.Check();
        ToolTip.SetTip(button, "Check for updates. An update installs when the game closes.");
        var row = new DockPanel { Margin = new Thickness(8, 4) };
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(new TextBlock { Text = choice.Label, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void DisposeIcons()
    {
        foreach (var icon in _icons)
            icon.Dispose();
        _icons.Clear();
    }
}
