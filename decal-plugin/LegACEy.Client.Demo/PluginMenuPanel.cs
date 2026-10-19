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

/// <summary>
/// The LegACEy launcher: one row per menu entry of a visible plugin, and a Settings… button at its foot. Picking a plugin row runs that entry;
/// Settings… runs <paramref name="openSettings"/>.
/// </summary>
public sealed class PluginMenuPanel : UserControl
{
    public const int WindowWidth = 220;
    public const int WindowHeight = 280;

    private readonly PluginRegistry _registry;
    private readonly IGameArtSource? _art;
    private readonly StackPanel _rows = new();
    private readonly List<WriteableBitmap> _icons = new();

    public PluginMenuPanel(PluginRegistry registry, IGameArtSource? art, Action openSettings)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _art = art;
        var settings = new DerethButton { Content = "Settings…", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 4) };
        settings.Click += (_, _) => openSettings();
        var layout = new DockPanel();
        DockPanel.SetDock(settings, Dock.Bottom);
        layout.Children.Add(settings);
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
            row.Children.Add(new TextBlock { Text = item.Title, Foreground = DerethPalette.TextBrush, VerticalAlignment = VerticalAlignment.Center });

            var button = new DerethButton { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 2) };
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
