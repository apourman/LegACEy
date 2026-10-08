using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace LegACEy.Client.Themes;

/// <summary>A neutral fallback theme, used when the game's art is unavailable.</summary>
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
