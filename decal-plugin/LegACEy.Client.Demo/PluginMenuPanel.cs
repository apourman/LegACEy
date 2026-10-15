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

/// <summary>
/// The LegACEy menu: one row per menu entry of a visible plugin, and the UI size at the bottom. Picking a row runs that entry.
/// </summary>
public sealed class PluginMenuPanel : UserControl
{
    public const int WindowWidth = 220;
    public const int WindowHeight = 240;

    private readonly PluginRegistry _registry;
    private readonly IGameArtSource? _art;
    private readonly StackPanel _rows = new();
    private readonly List<WriteableBitmap> _icons = new();

    public PluginMenuPanel(PluginRegistry registry, IGameArtSource? art, UiSizeChoice? uiSize = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _art = art;
        var layout = new DockPanel();
        if (uiSize != null)
        {
            var footer = UiSizeRow(uiSize);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
        }
        layout.Children.Add(_rows);
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
        var entries = _registry.VisibleMenuEntries;
        if (entries.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "No LegACEy plugins are available on this server.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8)
            });
            return;
        }

        foreach (var item in entries)
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

    private void DisposeIcons()
    {
        foreach (var icon in _icons)
            icon.Dispose();
        _icons.Clear();
    }
}
