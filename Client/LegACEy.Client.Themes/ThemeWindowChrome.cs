using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Themes;

/// <summary>A reusable titled window frame with AC art, a close action and replaceable content.</summary>
public sealed class ThemeWindowChrome : UserControl
{
    private readonly NineSliceBorder _acFrame;
    private readonly Border _simpleFrame;

    public ThemeWindowChrome(IGameArtSource art, string title, Control content)
    {
        if (art == null) throw new ArgumentNullException(nameof(art));
        if (content == null) throw new ArgumentNullException(nameof(content));

        _acFrame = new NineSliceBorder(art, fallback: new SolidColorBrush(Color.FromRgb(0x23, 0x20, 0x19)));
        _simpleFrame = new Border { Classes = { "theme-window-frame" } };
        var titleText = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        CloseButton = new Button
        {
            Content = "×",
            Width = 25,
            Height = 23,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var header = new Border
        {
            Classes = { "theme-window-titlebar" },
            Height = 26,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,32"),
                Children = { titleText, CloseButton }
            }
        };
        Grid.SetColumn(CloseButton, 1);
        var layout = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(new ContentControl { Content = content });

        var root = new Grid();
        root.Children.Add(_acFrame);
        root.Children.Add(_simpleFrame);
        root.Children.Add(layout);
        Content = root;
        ApplyTheme(new AcClientTheme(art));
    }

    public event EventHandler? CloseRequested;

    public Button CloseButton { get; }

    public void ApplyTheme(IClientTheme theme)
    {
        if (theme is AcClientTheme acTheme)
            _acFrame.SetArtSource(acTheme.ArtSource);
        _acFrame.IsVisible = theme.UsesAcChrome;
        _simpleFrame.IsVisible = !theme.UsesAcChrome;
    }
}
