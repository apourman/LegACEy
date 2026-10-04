using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace LegACEy.Client.Themes;

/// <summary>A neutral second theme for previewing and verifying runtime theme changes.</summary>
public sealed class SimpleClientTheme : IClientTheme
{
    public string Name => "Simple";
    public bool UsesAcChrome => false;

    public IStyle CreateStyles()
    {
        var styles = new Styles();
        AddBase(styles, Color.FromRgb(0x20, 0x25, 0x2d), Color.FromRgb(0xe8, 0xec, 0xf2), Color.FromRgb(0x4c, 0x8d, 0xc9));
        return styles;
    }

    internal static void AddBase(Styles styles, Color surface, Color text, Color accent)
    {
        var button = new Style(x => x.OfType<Button>());
        button.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(surface)));
        button.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(text)));
        button.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Brush(accent)));
        button.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 4)));
        button.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(2)));
        styles.Add(button);

        var over = new Style(x => x.OfType<Button>().Class(":pointerover"));
        over.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(accent)));
        styles.Add(over);
        var pressed = new Style(x => x.OfType<Button>().Class(":pressed"));
        pressed.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(Color.FromRgb(0x18, 0x1b, 0x21))));
        styles.Add(pressed);
        var disabled = new Style(x => x.OfType<Button>().Class(":disabled"));
        disabled.Setters.Add(new Setter(TemplatedControl.OpacityProperty, 0.5));
        styles.Add(disabled);
        var sampleHover = new Style(x => x.OfType<Button>().Class("sample-hover"));
        sampleHover.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(accent)));
        styles.Add(sampleHover);
        var samplePressed = new Style(x => x.OfType<Button>().Class("sample-pressed"));
        samplePressed.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(Color.FromRgb(0x18, 0x1b, 0x21))));
        styles.Add(samplePressed);
        var sampleDisabled = new Style(x => x.OfType<Button>().Class("sample-disabled"));
        sampleDisabled.Setters.Add(new Setter(TemplatedControl.OpacityProperty, 0.5));
        styles.Add(sampleDisabled);

        AddControl<TextBox>(styles, surface, text, accent);
        AddControl<ListBox>(styles, surface, text, accent);
        AddControl<ListBoxItem>(styles, surface, text, accent);
        AddControl<TabControl>(styles, surface, text, accent);
        AddControl<ScrollViewer>(styles, surface, text, accent);
        AddControl<ScrollBar>(styles, surface, text, accent);
        AddControl<CheckBox>(styles, surface, text, accent);
        AddControl<ProgressBar>(styles, surface, text, accent);
        AddControl<ToolTip>(styles, surface, text, accent);
        AddControl<TabItem>(styles, surface, text, accent);
        AddBorderStyle(styles, "theme-window-frame", surface, accent);
        AddBorderStyle(styles, "theme-window-titlebar", accent, accent);
        var textBlock = new Style(x => x.OfType<TextBlock>());
        textBlock.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush(text)));
        textBlock.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans")));
        styles.Add(textBlock);
        var disabledText = new Style(x => x.OfType<TextBlock>().Class(":disabled"));
        disabledText.Setters.Add(new Setter(Control.OpacityProperty, 0.5));
        styles.Add(disabledText);

        AddStateStyle<ListBoxItem>(styles, ":pointerover", accent, text);
        AddStateStyle<ListBoxItem>(styles, ":selected", accent, text);
        AddStateStyle<TabItem>(styles, ":pointerover", accent, text);
        AddStateStyle<TabItem>(styles, ":selected", surface, text);
        AddStateStyle<TextBox>(styles, ":pointerover", surface, text, accent);
        AddStateStyle<TextBox>(styles, ":focus", surface, text, accent);
        AddStateStyle<CheckBox>(styles, ":pointerover", accent, text);
        AddStateStyle<CheckBox>(styles, ":pressed", accent, text);
        AddStateStyle<ScrollBar>(styles, ":pointerover", accent, text, accent);
        AddStateStyle<ScrollBar>(styles, ":pressed", accent, text, accent);
        AddDisabledStyle<ListBoxItem>(styles);
        AddDisabledStyle<TabItem>(styles);
        AddDisabledStyle<TextBox>(styles);
        AddDisabledStyle<ScrollViewer>(styles);
        AddDisabledStyle<CheckBox>(styles);
        AddDisabledStyle<ProgressBar>(styles);
        AddDisabledStyle<ScrollBar>(styles);
        AddStateStyle<Button>(styles, "sample-hover", accent, text);
        AddStateStyle<Button>(styles, "sample-pressed", Color.FromRgb(0x18, 0x1b, 0x21), text);
        AddStateStyle<TextBox>(styles, "sample-hover", surface, text, accent);
        AddStateStyle<TextBox>(styles, "sample-focused", surface, text, accent);
        AddStateStyle<ListBoxItem>(styles, "sample-hover", accent, text);
        AddStateStyle<ListBoxItem>(styles, "sample-selected", accent, text);
        AddStateStyle<TabItem>(styles, "sample-hover", accent, text);
        AddStateStyle<TabItem>(styles, "sample-selected", surface, text);
        AddStateStyle<ScrollBar>(styles, "sample-hover", accent, text, accent);
        AddStateStyle<ScrollBar>(styles, "sample-pressed", Color.FromRgb(0x18, 0x1b, 0x21), text, accent);
        AddStateStyle<CheckBox>(styles, "sample-hover", accent, text);
        AddStateStyle<CheckBox>(styles, "sample-pressed", Color.FromRgb(0x18, 0x1b, 0x21), text);
        AddMatrixStates<Button>(styles, surface, text, accent);
        AddMatrixStates<TextBox>(styles, surface, text, accent);
        AddMatrixStates<ListBox>(styles, surface, text, accent);
        AddMatrixStates<ListBoxItem>(styles, surface, text, accent);
        AddMatrixStates<TabControl>(styles, surface, text, accent);
        AddMatrixStates<TabItem>(styles, surface, text, accent);
        AddMatrixStates<ScrollViewer>(styles, surface, text, accent);
        AddMatrixStates<ScrollBar>(styles, surface, text, accent);
        AddMatrixStates<CheckBox>(styles, surface, text, accent);
        AddMatrixStates<ProgressBar>(styles, surface, text, accent);
        AddMatrixStates<ToolTip>(styles, surface, text, accent);
        AddTextMatrixStates(styles, surface, text, accent);
    }

    private static void AddControl<T>(Styles styles, Color surface, Color text, Color accent) where T : TemplatedControl
    {
        var style = new Style(x => x.OfType<T>());
        style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(surface)));
        style.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(text)));
        style.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Brush(accent)));
        styles.Add(style);
    }

    private static void AddStateStyle<T>(Styles styles, string state, Color background, Color foreground, Color? border = null) where T : TemplatedControl
    {
        var style = new Style(x => x.OfType<T>().Class(state));
        style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(background)));
        style.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(foreground)));
        if (border.HasValue)
            style.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Brush(border.Value)));
        styles.Add(style);
    }

    private static void AddDisabledStyle<T>(Styles styles) where T : TemplatedControl
    {
        var style = new Style(x => x.OfType<T>().Class(":disabled"));
        style.Setters.Add(new Setter(TemplatedControl.OpacityProperty, 0.5));
        styles.Add(style);
    }

    private static void AddMatrixStates<T>(Styles styles, Color surface, Color text, Color accent) where T : TemplatedControl
    {
        AddStateStyle<T>(styles, "sample-normal", surface, text, accent);
        AddStateStyle<T>(styles, "sample-hover", accent, text, accent);
        AddStateStyle<T>(styles, "sample-pressed", Color.FromRgb(0x18, 0x1b, 0x21), text, accent);
        var disabled = new Style(x => x.OfType<T>().Class("sample-disabled"));
        disabled.Setters.Add(new Setter(TemplatedControl.OpacityProperty, 0.5));
        styles.Add(disabled);
    }

    private static void AddTextMatrixStates(Styles styles, Color surface, Color text, Color accent)
    {
        AddTextSampleState(styles, "sample-normal", surface, text);
        AddTextSampleState(styles, "sample-hover", accent, text);
        AddTextSampleState(styles, "sample-pressed", Color.FromRgb(0x18, 0x1b, 0x21), text);
        var disabled = new Style(x => x.OfType<TextBlock>().Class("sample-disabled"));
        disabled.Setters.Add(new Setter(Control.OpacityProperty, 0.5));
        styles.Add(disabled);
    }

    private static void AddTextSampleState(Styles styles, string className, Color background, Color foreground)
    {
        var style = new Style(x => x.OfType<TextBlock>().Class(className));
        style.Setters.Add(new Setter(TextBlock.BackgroundProperty, Brush(background)));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush(foreground)));
        styles.Add(style);
    }

    private static void AddBorderStyle(Styles styles, string className, Color surface, Color border)
    {
        var style = new Style(x => x.OfType<Border>().Class(className));
        style.Setters.Add(new Setter(Border.BackgroundProperty, Brush(surface)));
        style.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(border)));
        style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        styles.Add(style);
    }

    internal static SolidColorBrush Brush(Color color) => new(color);
}
