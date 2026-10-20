using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Market;

/// <summary>The Market mockup's colours (v4 template.html's :root), beyond the shared Dereth palette.</summary>
public static class MarketInk
{
    public static readonly Color Panel = Color.Parse("#1E1A13");
    public static readonly Color PanelHi = Color.Parse("#272219");
    public static readonly Color Line = Color.Parse("#3A3122");
    public static readonly Color LineSoft = Color.Parse("#2A241A");
    public static readonly Color Red = Color.Parse("#5A0E0A");
    public static readonly Color RedHi = Color.Parse("#8A2216");
    public static readonly Color GoldRim = Color.Parse("#C99A45");
    public static readonly Color Lit = Color.Parse("#3FCF5F");
    public static readonly Color LitText = Color.Parse("#5BE88F");
    public static readonly Color Warn = Color.Parse("#E7A94A");
    public static readonly Color Bad = Color.Parse("#E06A5C");
    public static readonly Color Text = Color.Parse("#ECE7DA");
    public static readonly Color Muted = Color.Parse("#9C937F");

    public static readonly IBrush LineBrush = DerethPalette.Brush(Line);
    public static readonly IBrush LineSoftBrush = DerethPalette.Brush(LineSoft);
    public static readonly IBrush TextBrush = DerethPalette.Brush(Text);
    public static readonly IBrush MutedBrush = DerethPalette.Brush(Muted);
    public static readonly IBrush LitBrush = DerethPalette.Brush(Lit);
    public static readonly IBrush LitTextBrush = DerethPalette.Brush(LitText);
    public static readonly IBrush WarnBrush = DerethPalette.Brush(Warn);
    public static readonly IBrush BadBrush = DerethPalette.Brush(Bad);
    public static readonly IBrush GoldRimBrush = DerethPalette.Brush(GoldRim);
    /// <summary>A panel or card: lighter at the top.</summary>
    public static readonly IBrush PanelBrush = Vertical(PanelHi, Panel);
    /// <summary>The red button's face.</summary>
    public static readonly IBrush RedBrush = Vertical(RedHi, Red);
    /// <summary>The green "go" button's face.</summary>
    public static readonly IBrush GoBrush = Vertical(Color.Parse("#1E5A26"), Color.Parse("#123A17"));
    /// <summary>A field's or the search's sunk face.</summary>
    public static readonly IBrush SunkBrush = DerethPalette.Brush(Color.FromArgb(0x59, 0, 0, 0));
    /// <summary>The selected wash: gold at about 14%.</summary>
    public static readonly IBrush GoldWashBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x24));
    /// <summary>The selected side bar row: a gold wash fading to the right.</summary>
    public static readonly IBrush NavOnBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(DerethPalette.Gold.WithAlpha(0x29), 0), new GradientStop(DerethPalette.Gold.WithAlpha(0x08), 1) }
    };
    /// <summary>The window's ground behind the screens.</summary>
    public static readonly IBrush GroundBrush = Vertical(Color.Parse("#17140F"), Color.Parse("#0D0B08"));
    public static readonly IBrush HoverBrush = DerethPalette.Brush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));

    public static IBrush Vertical(Color top, Color bottom) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) }
    };
}

/// <summary>The Market's line icons, the mockup's SVGs on a 20-unit grid, stroked in a colour that can change.</summary>
public enum MarketIcon { Scales, Search, Browse, Star, StarFilled, Sell, Ledger, List, Tell, Sort, Back, Close }

/// <summary>A <see cref="MarketIcon"/> drawn as large as the control's smaller side.</summary>
public sealed class MarketGlyph : Control
{
    private static readonly Dictionary<MarketIcon, (string Data, bool Filled, double Stroke)> Paths = new()
    {
        [MarketIcon.Scales] = ("M10 3v14M6 17h8M4 6h12M4 6l-2.5 5h5zM16 6l-2.5 5h5zM1.5 11a2.5 1.6 0 0 0 5 0M13.5 11a2.5 1.6 0 0 0 5 0", false, 1.4),
        [MarketIcon.Search] = ("M9 3.5a5.5 5.5 0 1 1 0 11a5.5 5.5 0 1 1 0-11zM13 13l4 4", false, 1.8),
        [MarketIcon.Browse] = ("M4 3h4a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1zM12 3h4a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1h-4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1zM4 11h4a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1zM12 11h4a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1h-4a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1z", false, 1.5),
        [MarketIcon.Star] = ("M10 2.5l2.3 4.8 5.2.6-3.9 3.6 1.1 5.2L10 14l-4.7 2.7 1.1-5.2-3.9-3.6 5.2-.6z", false, 1.5),
        [MarketIcon.StarFilled] = ("M10 2.5l2.3 4.8 5.2.6-3.9 3.6 1.1 5.2L10 14l-4.7 2.7 1.1-5.2-3.9-3.6 5.2-.6z", true, 0),
        [MarketIcon.Sell] = ("M3 10l7-7h7v7l-7 7zM13.5 5.2a1.3 1.3 0 1 1 0 2.6a1.3 1.3 0 1 1 0-2.6z", false, 1.5),
        [MarketIcon.Ledger] = ("M5 3h10v14H5zM8 7h4M8 10h4M8 13h2", false, 1.5),
        [MarketIcon.List] = ("M4 5h12M4 10h12M4 15h12", false, 1.5),
        [MarketIcon.Tell] = ("M3 5h14v8H9l-4 3v-3H3z", false, 1.5),
        [MarketIcon.Sort] = ("M6 4v12M3 13l3 3 3-3M14 16V4M11 7l3-3 3 3", false, 1.5),
        [MarketIcon.Back] = ("M12 4l-6 6 6 6", false, 1.8),
        [MarketIcon.Close] = ("M5 5l10 10M15 5L5 15", false, 2),
    };

    private static readonly Dictionary<MarketIcon, Geometry> Shapes = new();
    private readonly MarketIcon _icon;
    private Color _colour;

    public MarketGlyph(MarketIcon icon, Color colour, double size = 16)
    {
        _icon = icon;
        _colour = colour;
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public Color Colour
    {
        get => _colour;
        set
        {
            _colour = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var (data, filled, stroke) = Paths[_icon];
        if (!Shapes.TryGetValue(_icon, out var shape)) Shapes[_icon] = shape = Geometry.Parse(data);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 20;
        var brush = new SolidColorBrush(_colour);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            if (filled) context.DrawGeometry(brush, null, shape);
            else context.DrawGeometry(null, new Pen(brush, stroke, null, PenLineCap.Round, PenLineJoin.Round), shape);
        }
    }
}

/// <summary>
/// A button drawn from its own Background, BorderBrush, BorderThickness, CornerRadius and Padding, with a light wash under the pointer.
/// The base theme's button is light grey; this one never is.
/// </summary>
public sealed class MarketButton : Button
{
    private Border? _wash;

    static MarketButton()
    {
        IsEnabledProperty.Changed.AddClassHandler<MarketButton>((button, _) => button.Opacity = button.IsEnabled ? 1 : 0.45);
    }

    public MarketButton()
    {
        Background = Brushes.Transparent;
        BorderBrush = MarketInk.LineBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Foreground = MarketInk.TextBrush;
        FontFamily = DerethPalette.Body;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Template = new FuncControlTemplate<Button>((_, scope) =>
        {
            var presenter = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = new TemplateBinding(ContentProperty),
                [!ContentPresenter.PaddingProperty] = new TemplateBinding(PaddingProperty),
                [!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(HorizontalContentAlignmentProperty),
                [!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(VerticalContentAlignmentProperty),
            };
            scope.Register(presenter.Name, presenter);
            _wash = new Border
            {
                Background = MarketInk.HoverBrush, IsVisible = false, IsHitTestVisible = false,
                [!Border.CornerRadiusProperty] = new TemplateBinding(CornerRadiusProperty),
            };
            var layers = new Panel();
            layers.Children.Add(presenter);
            layers.Children.Add(_wash);
            return new Border
            {
                [!Border.BackgroundProperty] = new TemplateBinding(BackgroundProperty),
                [!Border.BorderBrushProperty] = new TemplateBinding(BorderBrushProperty),
                [!Border.BorderThicknessProperty] = new TemplateBinding(BorderThicknessProperty),
                [!Border.CornerRadiusProperty] = new TemplateBinding(CornerRadiusProperty),
                [!Border.BoxShadowProperty] = new TemplateBinding(Border.BoxShadowProperty),
                Child = layers,
            };
        });
        Classes.CollectionChanged += (_, _) => { if (_wash != null) _wash.IsVisible = Classes.Contains(":pointerover"); };
    }
}

/// <summary>The Market's drawing helpers: text, buttons, chips, tags, prices and panels, sized from the mockup's CSS.</summary>
public static class MarketParts
{
    public static TextBlock Text(string text, IBrush brush, double size, bool title = false) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontFamily = title ? DerethPalette.Title : DerethPalette.Body,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>A small-caps label: muted (a field's or a column's head) or gold (a section head in the detail panel).</summary>
    public static TextBlock Caps(string text, IBrush? brush = null, double size = 10.5) => new()
    {
        Text = text.ToUpperInvariant(), Foreground = brush ?? MarketInk.MutedBrush, FontSize = size, FontFamily = DerethPalette.Body,
        LetterSpacing = 1, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A gold small-caps head with a rule to its right, as the detail panel's APPRAISAL.</summary>
    public static Control SectionHead(string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(Caps(text, DerethPalette.GoldBrush));
        var rule = new Border { Height = 1, Background = MarketInk.LineSoftBrush, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rule, 1);
        row.Children.Add(rule);
        return row;
    }

    /// <summary>The Palatino screen head, as "Your items" and "Ledger".</summary>
    public static TextBlock Head(string text, double size = 17) => Text(text, DerethPalette.CreamBrush, size, title: true);

    public enum Kind { Ghost, Red, Go, Chip, ChipOn, Quiet }

    /// <summary>A button: Red is the Dereth action, Go the green confirm, Ghost an outline, Chip a pill, Quiet text only.</summary>
    public static MarketButton Button(string text, Kind kind = Kind.Ghost, double height = 26, IBrush? ink = null)
    {
        var label = Text(text, ink ?? kind switch
        {
            Kind.Go => DerethPalette.Brush(Color.Parse("#D9FFE4")),
            Kind.Red or Kind.ChipOn => DerethPalette.CreamBrush,
            Kind.Quiet => MarketInk.MutedBrush,
            _ => MarketInk.TextBrush,
        }, kind is Kind.Red or Kind.Go ? 14 : 12.5, title: kind is Kind.Red or Kind.Go || (kind == Kind.Ghost && height <= 26));
        label.TextTrimming = TextTrimming.None;
        var button = new MarketButton { Height = height, Content = label, Padding = new Thickness(height >= 32 ? 18 : 10, 0) };
        Style(button, kind);
        return button;
    }

    /// <summary>Applies a <see cref="Kind"/>'s face to a button.</summary>
    public static void Style(MarketButton button, Kind kind)
    {
        button.CornerRadius = new CornerRadius(kind is Kind.Chip or Kind.ChipOn ? button.Height / 2 : button.Height >= 32 ? 6 : 5);
        button.BorderThickness = new Thickness(kind == Kind.Quiet ? 0 : 1);
        (button.Background, button.BorderBrush) = kind switch
        {
            Kind.Red => (MarketInk.RedBrush, MarketInk.GoldRimBrush),
            Kind.Go => (MarketInk.GoBrush, MarketInk.LitBrush),
            Kind.ChipOn => (DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x1F)), DerethPalette.GoldDarkBrush),
            _ => ((IBrush)Brushes.Transparent, MarketInk.LineBrush),
        };
        button.SetValue(Border.BoxShadowProperty, kind == Kind.Go ? BoxShadows.Parse("0 0 10 0 #403FCF5F") : default);
    }

    /// <summary>A small tag: a stat (Wk 8), or gold, or a state (LISTED, ENDING) in small caps.</summary>
    public static Border Tag(string text, IBrush? ink = null, IBrush? wash = null, bool caps = false) => new()
    {
        Background = wash ?? DerethPalette.Brush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(3),
        Padding = new Thickness(caps ? 7 : 6, caps ? 2 : 1), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        Child = caps ? Caps(text, ink ?? MarketInk.TextBrush, 10) : Text(text, ink ?? MarketInk.TextBrush, 10.5),
    };

    public static Border GoldTag(string text) => Tag(text, DerethPalette.GoldBrush, DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x1A)));

    /// <summary>The MMD coin: a gold disc.</summary>
    public static Ellipse Coin(double size = 13) => new()
    {
        Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
        Fill = new RadialGradientBrush
        {
            Center = new RelativePoint(0.35, 0.35, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.35, 0.35, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#FFE9A8"), 0), new GradientStop(Color.Parse("#C99A45"), 0.55), new GradientStop(Color.Parse("#7A5E30"), 1) }
        },
        Stroke = DerethPalette.Brush(Color.Parse("#3A2A10")), StrokeThickness = 1,
    };

    /// <summary>A coin and an amount: 14 px body text, or the big Palatino price of the detail panel.</summary>
    public static StackPanel Price(string amount, bool big = false, IBrush? ink = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = big ? 8 : 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Coin(big ? 18 : 13));
        var text = Text(amount, ink ?? DerethPalette.CreamBrush, big ? 26 : 14, title: big);
        text.TextTrimming = TextTrimming.None;
        row.Children.Add(text);
        return row;
    }

    /// <summary>A panel or card: the gradient face, the line border and rounded corners.</summary>
    public static Border Card(Control child, double radius = 8, Thickness? padding = null) => new()
    {
        Background = MarketInk.PanelBrush, BorderBrush = MarketInk.LineBrush, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(12), Child = child,
    };

    /// <summary>An item icon in its rounded tile, <paramref name="size"/> across with the 32 px art scaled to fit.</summary>
    public static Border IconTile(Control art, double size = 40)
    {
        var inner = size - 8;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(6), BorderBrush = DerethPalette.Brush(Color.Parse("#5A4A2C")),
            BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#2E2719"), 0), new GradientStop(Color.Parse("#120F0A"), 1) }
            },
            Child = new Viewbox { Width = inner, Height = inner, Child = art },
        };
    }

    /// <summary>A thin horizontal rule.</summary>
    public static Border Rule(IBrush? brush = null) => new() { Height = 1, Background = brush ?? MarketInk.LineSoftBrush };

    /// <summary>A text box with the Market's sunk face: dark, with a line border that goes green while it has the keyboard.</summary>
    public static TextBox Field(string watermark, double height = 30, double width = double.NaN)
    {
        var box = new TextBox
        {
            Watermark = watermark, Height = height, Width = width, MinHeight = 0, FontSize = 13, FontFamily = DerethPalette.Body,
            Foreground = DerethPalette.CreamBrush, CaretBrush = DerethPalette.GoldBrush, Background = MarketInk.SunkBrush,
            BorderBrush = MarketInk.LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0), VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.GotFocus += (_, _) => box.BorderBrush = MarketInk.LitBrush;
        box.LostFocus += (_, _) => box.BorderBrush = MarketInk.LineBrush;
        return box;
    }

    /// <summary>A green count pill, as the Sell entry's unseen sales.</summary>
    public static (Border Badge, TextBlock Count) Badge()
    {
        var count = Text(string.Empty, DerethPalette.Brush(Color.Parse("#062010")), 11);
        count.FontWeight = FontWeight.SemiBold;
        count.TextTrimming = TextTrimming.None;
        var badge = new Border
        {
            Background = MarketInk.LitBrush, CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 0), MinHeight = 17,
            VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Child = count,
        };
        return (badge, count);
    }
}

/// <summary>The top bar's search: a rounded sunk field with the lens, the text box and the "/" key hint.</summary>
public sealed class MarketSearch : Border
{
    public MarketSearch(string placeholder)
    {
        var box = new TextBox
        {
            Watermark = placeholder, Foreground = DerethPalette.CreamBrush, CaretBrush = DerethPalette.GoldBrush, FontFamily = DerethPalette.Body,
            FontSize = 13.5, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var key = new Border
        {
            BorderBrush = MarketInk.LineBrush, BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = MarketParts.Text("/", MarketInk.MutedBrush, 11),
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        row.Children.Add(new MarketGlyph(MarketIcon.Search, DerethPalette.Gold));
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        Grid.SetColumn(key, 2);
        row.Children.Add(key);
        box.GotFocus += (_, _) => { BorderBrush = MarketInk.LitBrush; key.IsVisible = false; };
        box.LostFocus += (_, _) => { BorderBrush = MarketInk.LineBrush; key.IsVisible = true; };
        Background = MarketInk.SunkBrush;
        BorderBrush = MarketInk.LineBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(14, 0, 12, 0);
        MinWidth = 160;
        Child = row;
    }
}

/// <summary>
/// The mockup's tab strip: options in a dark sunk pill, the chosen one raised. Each option is a button whose content is its text, so it is
/// clicked like any button; <see cref="Chosen"/> says which.
/// </summary>
public sealed class MarketTabs : Border
{
    private readonly List<MarketButton> _options = new();
    private int _selected;

    public MarketTabs(params string[] options)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var option = MarketParts.Button(options[i], MarketParts.Kind.Quiet, 24);
            option.Padding = new Thickness(12, 0);
            option.Click += (_, _) => Chosen?.Invoke(index);
            _options.Add(option);
            row.Children.Add(option);
        }
        Background = DerethPalette.Brush(Color.FromArgb(0x4D, 0, 0, 0));
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(3);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        Child = row;
        Light();
    }

    /// <summary>The chosen option's index. Setting it raises nothing.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Light();
        }
    }

    public event Action<int>? Chosen;

    private void Light()
    {
        for (var i = 0; i < _options.Count; i++)
        {
            var on = i == _selected;
            _options[i].Background = on ? DerethPalette.Brush(MarketInk.PanelHi) : Brushes.Transparent;
            _options[i].BorderBrush = MarketInk.LineBrush;
            _options[i].BorderThickness = new Thickness(on ? 1 : 0);
            ((TextBlock)_options[i].Content!).Foreground = on ? DerethPalette.CreamBrush : MarketInk.MutedBrush;
        }
    }
}
