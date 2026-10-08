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

/// <summary>The LegACEy menu: one row per menu entry of a visible plugin. Picking a row runs that entry.</summary>
public sealed class PluginMenuPanel : UserControl
{
    public const int WindowWidth = 220;
    public const int WindowHeight = 240;

    private readonly PluginRegistry _registry;
    private readonly IGameArtSource? _art;
    private readonly StackPanel _rows = new();
    private readonly List<WriteableBitmap> _icons = new();

    public PluginMenuPanel(PluginRegistry registry, IGameArtSource? art)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _art = art;
        Content = _rows;
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

    private void DisposeIcons()
    {
        foreach (var icon in _icons)
            icon.Dispose();
        _icons.Clear();
    }
}
