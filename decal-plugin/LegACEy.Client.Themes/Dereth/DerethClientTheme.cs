using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace LegACEy.Client.Themes;

/// <summary>
/// The Dereth theme: navy panels, a gold frame and a slim gold scrollbar, with the chrome drawn by the shared Dereth parts.
/// It does not use AC chrome, so a Dereth window is opened without the retail theme over it.
/// </summary>
public sealed class DerethClientTheme : IClientTheme
{
    public string Name => "Dereth";
    public bool UsesAcChrome => false;

    public IStyle CreateStyles()
    {
        var styles = new Styles();
        // Every ScrollBar in the window takes this ControlTheme, and with it the whole Dereth template.
        var scrollBars = new Style(x => x.OfType<ScrollBar>());
        scrollBars.Setters.Add(new Setter(StyledElement.ThemeProperty, ScrollBarTheme()));
        styles.Add(scrollBars);
        return styles;
    }

    private static ControlTheme ScrollBarTheme() => new(typeof(ScrollBar))
    {
        Setters =
        {
            new Setter(Layoutable.WidthProperty, 9.0),
            new Setter(Layoutable.MinWidthProperty, 0.0),
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ScrollBar>((owner, _) => ScrollBarVisual(owner)))
        }
    };

    /// <summary>
    /// A dark groove with a gold thumb and no arrow buttons. The Track is the stock part; the line buttons are kept
    /// (empty and zero-sized) because the ScrollBar wires its parts by name.
    /// </summary>
    private static Control ScrollBarVisual(ScrollBar owner)
    {
        var track = new Track
        {
            Name = "PART_Track", Orientation = owner.Orientation,
            // A vertical Track counts from the bottom unless reversed, as the stock vertical ScrollBar sets it.
            IsDirectionReversed = owner.Orientation == Orientation.Vertical,
            [!RangeBase.MinimumProperty] = new TemplateBinding(RangeBase.MinimumProperty),
            [!RangeBase.MaximumProperty] = new TemplateBinding(RangeBase.MaximumProperty),
            [!RangeBase.ValueProperty] = new Binding(nameof(RangeBase.Value)) { Mode = BindingMode.TwoWay, RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) },
            [!Track.ViewportSizeProperty] = new TemplateBinding(ScrollBar.ViewportSizeProperty)
        };
        track.Thumb = new Thumb { Template = new FuncControlTemplate<Thumb>((_, _) => new Border
        {
            Background = DerethPalette.Brush(DerethPalette.Gold), CornerRadius = new CornerRadius(3), Margin = new Thickness(1, 0)
        }) };
        track.DecreaseButton = PageButton();
        track.IncreaseButton = PageButton();
        var root = new Border
        {
            Background = DerethPalette.Brush(DerethPalette.Groove), BorderBrush = DerethPalette.Brush(DerethPalette.GrooveEdge),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = track
        };
        var up = new RepeatButton { Name = "PART_LineUpButton", Width = 0, Height = 0, IsVisible = false };
        var down = new RepeatButton { Name = "PART_LineDownButton", Width = 0, Height = 0, IsVisible = false };
        var panel = new Grid();
        panel.Children.Add(root);
        panel.Children.Add(up);
        panel.Children.Add(down);
        return panel;
    }

    private static RepeatButton PageButton() => new()
    {
        Classes = { "repeattrack" },
        Template = new FuncControlTemplate<RepeatButton>((_, _) => new Border { Background = Brushes.Transparent })
    };
}
