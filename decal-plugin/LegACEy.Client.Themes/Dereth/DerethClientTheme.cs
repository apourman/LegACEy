using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace LegACEy.Client.Themes;

/// <summary>
/// The Dereth theme: navy panels, a gold frame, a slim gold scrollbar and navy tooltips, with the chrome drawn by the shared
/// Dereth parts. It does not use AC chrome, so a Dereth window is opened without the retail theme over it.
/// </summary>
public sealed class DerethClientTheme : IClientTheme
{
    private const double ScrollBarThickness = 9;
    private static readonly IBrush ThumbBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#7A5E30"), 0), new GradientStop(Color.Parse("#D9B66A"), 0.5), new GradientStop(Color.Parse("#7A5E30"), 1) }
    };
    private static readonly IBrush NavyBrush = DerethPalette.Brush(Color.Parse("#0D141C"));
    private static readonly ControlTheme ScrollBarControlTheme = new(typeof(ScrollBar))
    {
        Setters = { new Setter(TemplatedControl.TemplateProperty, ScrollBarTemplate()) }
    };
    private static readonly ControlTheme ToolTipControlTheme = new(typeof(ToolTip))
    {
        Setters =
        {
            new Setter(TemplatedControl.ForegroundProperty, DerethPalette.TextBrush),
            new Setter(TemplatedControl.FontFamilyProperty, DerethPalette.Body),
            new Setter(TemplatedControl.FontSizeProperty, 12.0),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 3)),
            new Setter(TemplatedControl.TemplateProperty, ToolTipTemplate())
        }
    };

    public string Name => "Dereth";
    public bool UsesAcChrome => false;

    public IStyle CreateStyles()
    {
        var styles = new Styles();
        // Every ScrollBar takes this ControlTheme, and with it the whole Dereth template. A style sets the theme, so it
        // beats the base theme's ScrollBar style: an implicit resource theme lost to it.
        var scrollBars = new Style(x => x.OfType<ScrollBar>());
        scrollBars.Setters.Add(new Setter(StyledElement.ThemeProperty, ScrollBarControlTheme));
        styles.Add(scrollBars);
        // The thickness runs across each bar: width on vertical bars, height on horizontal ones.
        var vertical = new Style(x => x.OfType<ScrollBar>().Class(":vertical"));
        vertical.Setters.Add(new Setter(Layoutable.WidthProperty, ScrollBarThickness));
        styles.Add(vertical);
        var horizontal = new Style(x => x.OfType<ScrollBar>().Class(":horizontal"));
        horizontal.Setters.Add(new Setter(Layoutable.HeightProperty, ScrollBarThickness));
        styles.Add(horizontal);
        // Tooltips use the navy face wherever their owner sits, as the AC and simple themes do.
        var tips = new Style(x => x.Is<Control>());
        tips.Setters.Add(new Setter(ClientToolTips.ThemeProperty, ToolTipControlTheme));
        styles.Add(tips);
        return styles;
    }

    /// <summary>
    /// A dark groove with a gold thumb and page buttons that fill the track. The parts are registered by name, so the
    /// ScrollBar finds the track and the page buttons, and a click on the groove pages the view.
    /// </summary>
    private static FuncControlTemplate<ScrollBar> ScrollBarTemplate() => new((owner, scope) =>
    {
        var vertical = owner.Orientation == Orientation.Vertical;
        var thumbMargin = vertical ? new Thickness(1, 0) : new Thickness(0, 1);
        var track = new Track
        {
            Name = "PART_Track",
            Orientation = owner.Orientation,
            // A vertical Track counts from the bottom unless reversed, as the stock vertical ScrollBar sets it.
            IsDirectionReversed = vertical,
            [!RangeBase.MinimumProperty] = new TemplateBinding(RangeBase.MinimumProperty),
            [!RangeBase.MaximumProperty] = new TemplateBinding(RangeBase.MaximumProperty),
            [!RangeBase.ValueProperty] = new Binding(nameof(RangeBase.Value)) { Mode = BindingMode.TwoWay, RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) },
            [!Track.ViewportSizeProperty] = new TemplateBinding(ScrollBar.ViewportSizeProperty)
        };
        track.Thumb = new Thumb
        {
            Template = new FuncControlTemplate<Thumb>((_, _) => new Border
            {
                Background = ThumbBrush, CornerRadius = new CornerRadius(3), Margin = thumbMargin
            })
        };
        track.DecreaseButton = PageButton("PART_PageUpButton", scope);
        track.IncreaseButton = PageButton("PART_PageDownButton", scope);
        scope.Register(track.Name, track);
        return new Border
        {
            Background = DerethPalette.GrooveBrush, BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = track
        };
    });

    private static RepeatButton PageButton(string name, INameScope scope)
    {
        var button = new RepeatButton
        {
            Name = name,
            Classes = { "repeattrack" },
            Template = new FuncControlTemplate<RepeatButton>((_, _) => new Border { Background = Brushes.Transparent })
        };
        scope.Register(name, button);
        return button;
    }

    private static FuncControlTemplate<ToolTip> ToolTipTemplate() => new((_, scope) =>
    {
        var presenter = new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            [!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty),
            [!ContentPresenter.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty)
        };
        scope.Register(presenter.Name, presenter);
        return new Border
        {
            Background = NavyBrush, BorderBrush = DerethPalette.GoldBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), Child = presenter
        };
    });
}
