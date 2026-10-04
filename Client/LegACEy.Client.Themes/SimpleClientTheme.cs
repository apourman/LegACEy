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
        var textBlock = new Style(x => x.OfType<TextBlock>());
        textBlock.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush(text)));
        textBlock.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans")));
        styles.Add(textBlock);
    }

    private static void AddControl<T>(Styles styles, Color surface, Color text, Color accent) where T : TemplatedControl
    {
        var style = new Style(x => x.OfType<T>());
        style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(surface)));
        style.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(text)));
        style.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Brush(accent)));
        styles.Add(style);
    }

    internal static SolidColorBrush Brush(Color color) => new(color);
}
