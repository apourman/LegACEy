using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>Interactive catalogue of the controls covered by the client themes.</summary>
public sealed class ThemeGalleryControl : UserControl
{
    public event EventHandler? ThemeSwitchRequested;

    public ThemeGalleryControl(IGameArtSource art)
    {
        Content = new ScrollViewer
        {
            Width = 560,
            Height = 520,
            Content = BuildContent(art)
        };
    }

    private Control BuildContent(IGameArtSource art)
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock { Text = "LegACEy theme gallery", FontSize = 20, FontWeight = FontWeight.Bold });
        stack.Children.Add(new TextBlock { Text = "Button samples show normal, hover, pressed and disabled states. Hover or click the interactive controls below." });
        var chrome = new Grid
        {
            Width = 510,
            Height = 70,
            ColumnDefinitions = new ColumnDefinitions("*,32")
        };
        chrome.Children.Add(new NineSliceBorder(art, fallback: new SolidColorBrush(Color.FromRgb(0x23, 0x20, 0x19))));
        chrome.Children.Add(new TextBlock { Text = "AC portal.dat window chrome", Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center });
        var closeButton = new Button { Content = "×", Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(closeButton, 1);
        chrome.Children.Add(closeButton);
        stack.Children.Add(chrome);

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 128, ItemHeight = 38 };
        buttons.Children.Add(new Button { Content = "Normal" });
        var hover = new Button { Content = "Hover sample" };
        hover.Classes.Add("sample-hover");
        buttons.Children.Add(hover);
        var pressed = new Button { Content = "Pressed sample" };
        pressed.Classes.Add("sample-pressed");
        buttons.Children.Add(pressed);
        var disabled = new Button { Content = "Disabled", IsEnabled = false };
        buttons.Children.Add(disabled);
        stack.Children.Add(Section("Buttons", buttons));

        stack.Children.Add(Section("Text entry", new TextBox { Text = "Search game data…", Width = 300 }));
        var rows = new ListBox
        {
            Height = 96,
            ItemsSource = new[] { "Normal list row", "Hover or select this row", "Third sample row" },
            SelectedIndex = 0
        };
        stack.Children.Add(Section("List rows and scrolling", rows));
        stack.Children.Add(Section("Tabs", new TabControl
        {
            Width = 300,
            ItemsSource = new[]
            {
                new TabItem { Header = "General", Content = new TextBlock { Text = "Tab content" } },
                new TabItem { Header = "Details", Content = new TextBlock { Text = "More content" } }
            }
        }));
        stack.Children.Add(Section("Other controls", BuildOtherControls()));
        var switchButton = new Button { Content = "Switch theme while this gallery stays open" };
        switchButton.Click += (_, _) => ThemeSwitchRequested?.Invoke(this, EventArgs.Empty);
        stack.Children.Add(switchButton);
        return stack;
    }

    private static Control Section(string title, Control child)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(child);
        return stack;
    }

    private static Control BuildOtherControls()
    {
        var tooltip = new TextBlock { Text = "Vital meter · 72%" };
        ToolTip.SetTip(tooltip, "Game-style tooltip");
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new CheckBox { Content = "Enable feature", IsChecked = true },
                new ProgressBar { Width = 300, Minimum = 0, Maximum = 100, Value = 72 },
                tooltip
            }
        };
    }
}
