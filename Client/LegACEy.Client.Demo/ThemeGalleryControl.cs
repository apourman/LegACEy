using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Demo;

/// <summary>Interactive catalogue of themed controls and the visual states relevant to each.</summary>
public sealed class ThemeGalleryControl : UserControl
{
    private static readonly string[] MatrixStates = { "Normal", "Hover", "Pressed", "Disabled" };

    public event EventHandler? ThemeSwitchRequested;

    public ThemeGalleryControl()
    {
        Content = new ScrollViewer { Width = 560, Height = 500, Content = BuildContent() };
    }

    private Control BuildContent()
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock { Text = "LegACEy theme gallery", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold });
        stack.Children.Add(new TextBlock { Text = "Normal, hover, pressed, selected, focused and disabled samples are shown where those states apply." });
        stack.Children.Add(Section("Button · normal / hover / pressed / disabled", ButtonSamples()));
        stack.Children.Add(Section("TextBox · normal / hover / focused / disabled", TextBoxSamples()));
        stack.Children.Add(Section("ListBox rows · normal / hover / selected / disabled", ListRows()));
        stack.Children.Add(Section("TabControl · normal / hover / selected / disabled", Tabs()));
        stack.Children.Add(Section("ScrollViewer · normal / disabled; ScrollBar · normal / hover / pressed / disabled", ScrollSample()));
        stack.Children.Add(Section("CheckBox · normal / hover / checked / disabled", CheckBoxSamples()));
        stack.Children.Add(Section("ProgressBar · normal / disabled", ProgressSamples()));
        stack.Children.Add(Section("ToolTip · hover the label", ToolTipSample()));
        stack.Children.Add(Section("TextBlock · normal / disabled", TextSamples()));
        stack.Children.Add(Section("Visual state matrix · hover and pressed samples pin their appearance for comparison", BuildStateMatrix()));
        var switchButton = new Button { Content = "Switch theme while this gallery stays open" };
        switchButton.Click += (_, _) => ThemeSwitchRequested?.Invoke(this, EventArgs.Empty);
        stack.Children.Add(switchButton);
        return stack;
    }

    private static Control ButtonSamples()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Button { Content = "Normal", Width = 128, Height = 36, Margin = new Thickness(2) });
        row.Children.Add(SampleButton("Hover", "sample-hover"));
        row.Children.Add(SampleButton("Pressed", "sample-pressed"));
        row.Children.Add(new Button { Content = "Disabled", IsEnabled = false, Width = 128, Height = 36, Margin = new Thickness(2) });
        return row;
    }

    private static Button SampleButton(string text, string stateClass)
    {
        var button = new Button { Content = text, Width = 128, Height = 36, Margin = new Thickness(2) };
        button.Classes.Add(stateClass);
        return button;
    }

    private static Control TextBoxSamples()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBox { Text = "Normal", Width = 132, Margin = new Thickness(2) });
        row.Children.Add(SampleTextBox("Hover", "sample-hover"));
        row.Children.Add(SampleTextBox("Focused", "sample-focused"));
        row.Children.Add(new TextBox { Text = "Disabled", IsEnabled = false, Width = 132, Margin = new Thickness(2) });
        return row;
    }

    private static TextBox SampleTextBox(string text, string stateClass)
    {
        var control = new TextBox { Text = text, Width = 132, Margin = new Thickness(2) };
        control.Classes.Add(stateClass);
        return control;
    }

    private static Control ListRows()
    {
        var list = new ListBox { Height = 100, Width = 300 };
        list.Items.Add(new ListBoxItem { Content = "Normal row" });
        list.Items.Add(new ListBoxItem { Content = "Hover row", Classes = { "sample-hover" } });
        list.Items.Add(new ListBoxItem { Content = "Selected row", Classes = { "sample-selected" } });
        list.Items.Add(new ListBoxItem { Content = "Disabled row", IsEnabled = false });
        return list;
    }

    private static Control Tabs()
    {
        var tabs = new TabControl { Width = 380, Height = 90 };
        tabs.Items.Add(new TabItem { Header = "Selected", Content = new TextBlock { Text = "Selected tab content" }, Classes = { "sample-selected" } });
        tabs.Items.Add(new TabItem { Header = "Hover", Content = new TextBlock { Text = "Hover this tab" }, Classes = { "sample-hover" } });
        tabs.Items.Add(new TabItem { Header = "Disabled", IsEnabled = false });
        tabs.SelectedIndex = 0;
        return tabs;
    }

    private static Control ScrollSample()
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new ScrollViewer
        {
            Width = 300,
            Height = 82,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "ScrollViewer sample" },
                    new TextBlock { Text = "Use the scrollbar thumb to see its hover and pressed states." },
                    new TextBlock { Text = "Additional content keeps the scrollbar visible." },
                    new TextBlock { Text = "Scroll to the fourth row." },
                    new TextBlock { Text = "End of sample." }
                }
            }
        });
        var states = new WrapPanel { Orientation = Orientation.Horizontal };
        states.Children.Add(SampleScrollBar("Normal", null));
        states.Children.Add(SampleScrollBar("Hover", "sample-hover"));
        states.Children.Add(SampleScrollBar("Pressed", "sample-pressed"));
        states.Children.Add(SampleScrollBar("Disabled", null, enabled: false));
        stack.Children.Add(states);
        stack.Children.Add(new ScrollViewer { Content = new TextBlock { Text = "Disabled ScrollViewer", Margin = new Thickness(4) }, Width = 300, Height = 36, IsEnabled = false });
        return stack;
    }

    private static Control SampleScrollBar(string label, string? stateClass, bool enabled = true)
    {
        var scrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Minimum = 0,
            Maximum = 100,
            Value = 42,
            IsEnabled = enabled,
            Width = 128,
            Margin = new Thickness(2)
        };
        if (stateClass != null)
            scrollBar.Classes.Add(stateClass);
        var sample = new StackPanel { Spacing = 2, Margin = new Thickness(2) };
        sample.Children.Add(new TextBlock { Text = label });
        sample.Children.Add(scrollBar);
        return sample;
    }

    private static Control CheckBoxSamples()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new CheckBox { Content = "Normal", Margin = new Thickness(4) });
        row.Children.Add(new CheckBox { Content = "Hover", Margin = new Thickness(4), Classes = { "sample-hover" } });
        row.Children.Add(new CheckBox { Content = "Pressed", Margin = new Thickness(4), Classes = { "sample-pressed" } });
        row.Children.Add(new CheckBox { Content = "Checked", IsChecked = true, Margin = new Thickness(4) });
        row.Children.Add(new CheckBox { Content = "Disabled", IsEnabled = false, Margin = new Thickness(4) });
        return row;
    }

    private static Control ProgressSamples()
    {
        var stack = new StackPanel { Spacing = 5, Width = 300 };
        stack.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = 72 });
        stack.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = 42, IsEnabled = false });
        return stack;
    }

    private static Control ToolTipSample()
    {
        var label = new TextBlock { Text = "Hover for the game-style tooltip" };
        ToolTip.SetTip(label, "Game-style tooltip");
        return label;
    }

    private static Control TextSamples()
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock { Text = "Standard text" });
        row.Children.Add(new TextBlock { Text = "Disabled text", IsEnabled = false });
        return row;
    }

    private static Control BuildStateMatrix()
    {
        var matrix = new StackPanel { Spacing = 5 };
        matrix.Children.Add(new TextBlock { Text = "Disabled specimens are disabled. Hover and pressed specimens show fixed state styling; interactive examples above respond to input." });
        AddMatrixRow(matrix, "Button", state => new Button { Content = state, Width = 100 });
        AddMatrixRow(matrix, "TextBox", state => new TextBox { Text = state, Width = 100 });
        AddMatrixRow(matrix, "ListBox", state => new ListBox { Items = { state }, Width = 100, Height = 34 });
        AddStateRow(matrix, "ListBox row", ListRowMatrix());
        AddMatrixRow(matrix, "TabControl", CreateTabSample);
        AddStateRow(matrix, "TabItem", TabItemMatrix());
        AddMatrixRow(matrix, "ScrollViewer", state => new ScrollViewer { Content = new TextBlock { Text = state }, Width = 100, Height = 34 });
        AddMatrixRow(matrix, "ScrollBar", _ => CreateScrollBar());
        AddMatrixRow(matrix, "CheckBox", state => new CheckBox { Content = state });
        AddMatrixRow(matrix, "ProgressBar", _ => CreateProgressBar());
        AddMatrixRow(matrix, "ToolTip", state => new ToolTip { Content = state });
        AddMatrixRow(matrix, "TextBlock", state => new TextBlock { Text = state, Margin = new Thickness(4) });
        return matrix;
    }

    private static void AddMatrixRow<T>(StackPanel matrix, string name, Func<string, T> createSample) where T : Control
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = name, Width = 100, VerticalAlignment = VerticalAlignment.Center });
        foreach (var state in MatrixStates)
            row.Children.Add(StateSample(createSample(state), state));
        matrix.Children.Add(row);
    }

    private static void AddStateRow(StackPanel matrix, string name, Control sample)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = name, Width = 100, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(sample);
        matrix.Children.Add(row);
    }

    private static T StateSample<T>(T control, string state) where T : Control
    {
        control.Classes.Add($"sample-{state.ToLowerInvariant()}");
        if (state == "Disabled")
            control.IsEnabled = false;
        return control;
    }

    private static TabControl CreateTabSample(string text)
    {
        var tabs = new TabControl { Width = 100, Height = 48 };
        tabs.Items.Add(new TabItem { Header = text, Content = new TextBlock { Text = text } });
        return tabs;
    }

    private static Control TabItemMatrix()
    {
        var tabs = new TabControl { Width = 420, Height = 56 };
        foreach (var state in MatrixStates)
        {
            var item = StateSample(new TabItem { Header = state, Content = new TextBlock { Text = state } }, state);
            tabs.Items.Add(item);
        }
        tabs.SelectedIndex = 0;
        return tabs;
    }

    private static Control ListRowMatrix()
    {
        var list = new ListBox { Width = 420, Height = 100 };
        foreach (var state in MatrixStates)
            list.Items.Add(StateSample(new ListBoxItem { Content = state }, state));
        return list;
    }

    private static ScrollBar CreateScrollBar() => new()
    {
        Orientation = Orientation.Horizontal,
        Minimum = 0,
        Maximum = 100,
        Value = 50,
        Width = 100,
        Height = 18,
        Margin = new Thickness(2)
    };

    private static ProgressBar CreateProgressBar() => new() { Minimum = 0, Maximum = 100, Value = 50, Width = 100, Height = 18, Margin = new Thickness(2) };

    private static Control Section(string title, Control child)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        stack.Children.Add(child);
        return stack;
    }
}
