using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Demo;

/// <summary>A small panel used to exercise real Avalonia click, text, keyboard, and wheel input.</summary>
public sealed class InputTestPanel : Border
{
    public InputTestPanel(int width, int height)
    {
        Width = width;
        Height = height;
        Padding = new Thickness(8);
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2a));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x8a, 0x78, 0x54));
        BorderThickness = new Thickness(1);

        var clickCount = 0;
        var button = new Button { Content = "Click count: 0", HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => button.Content = $"Click count: {++clickCount}";
        var throwingButton = new Button { Content = "Trigger UI failure", HorizontalAlignment = HorizontalAlignment.Left };
        throwingButton.Click += (_, _) => throw new System.InvalidOperationException("Deliberate test failure from the input panel.");

        var textBox = new TextBox { Watermark = "Type here", HorizontalAlignment = HorizontalAlignment.Stretch };
        var label = new TextBlock { Name = "InputTextLabel", Foreground = Brushes.White, Text = "Text appears here" };
        label.Bind(TextBlock.TextProperty, new Binding("Text") { Source = textBox });

        var list = new ListBox
        {
            Height = 150,
            ItemsSource = Enumerable.Range(1, 40).Select(item => $"Scrollable item {item}"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Child = new StackPanel
        {
            Spacing = 6,
            Children = { button, throwingButton, textBox, label, list }
        };
    }
}
