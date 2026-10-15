using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// A round gold medallion with a glyph: a window button, the stance, Use, Examine or the backpack. Normal, hover and pressed
/// art, and the teal ring while <see cref="IsOpen"/>. A click is a release over the medallion after a press on it.
/// </summary>
public sealed class DerethMedallion : Decorator
{
    private static readonly Color GoldLight = Color.Parse("#D9B66A");
    private static readonly IPen RimPen = new Pen(DerethPalette.Brush(Color.Parse("#7A5E30")), 1);
    private static readonly IPen InnerPen = new Pen(DerethPalette.Brush(Color.Parse("#D9B66A").WithAlpha(0x59)), 1);
    private static readonly IPen RingPen = new Pen(DerethPalette.TealBrush, 2.5);
    private static readonly IBrush PressedFace = DerethPalette.Brush(Color.Parse("#03070A"));
    private static readonly IBrush NormalFace = Radial(Color.Parse("#1B2832"), Color.Parse("#081016"));
    private static readonly IBrush HoverFace = Radial(Color.Parse("#263643"), Color.Parse("#0E1923"));
    private static readonly Dictionary<string, Geometry> Shapes = new(StringComparer.Ordinal);

    private readonly double _diameter;
    private readonly double _glyphSize;
    private DerethGlyph _glyph;
    private bool _open;
    private bool _hover;
    private bool _pressed;

    /// <summary>A medallion of <paramref name="diameter"/> DIPs, with its glyph drawn at <paramref name="glyphSize"/>.</summary>
    public DerethMedallion(DerethGlyph glyph, double diameter, double glyphSize)
    {
        _glyph = glyph;
        _diameter = diameter;
        _glyphSize = glyphSize;
        Width = diameter;
        Height = diameter;
        // A control with no background takes no pointer, so a transparent square as its child takes the pointer over the whole medallion.
        Child = new Border { Width = diameter, Height = diameter, Background = Brushes.Transparent };
    }

    /// <summary>Raised when the medallion is clicked.</summary>
    public event Action? Clicked;

    public DerethGlyph Glyph
    {
        get => _glyph;
        set { _glyph = value; InvalidateVisual(); }
    }

    /// <summary>Shows the teal ring: the window or the menu it opens is open.</summary>
    public bool IsOpen
    {
        get => _open;
        set { _open = value; InvalidateVisual(); }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        _pressed = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = true;
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        UiTrace.Log?.Invoke($"Medallion {_glyph}: release, pressed {_pressed}.");
        base.OnPointerReleased(e);
        var wasPressed = _pressed;
        _pressed = false;
        e.Handled = true;
        InvalidateVisual();
        var inside = new Rect(Bounds.Size).Contains(e.GetPosition(this));
        UiTrace.Log?.Invoke($"Medallion {_glyph}: inside {inside}, clicking {wasPressed && inside}.");
        if (wasPressed && inside) Clicked?.Invoke();
        UiTrace.Log?.Invoke($"Medallion {_glyph}: click handled.");
    }

    public override void Render(DrawingContext context)
    {
        var centre = new Point(_diameter / 2, _diameter / 2);
        var radius = _diameter / 2;
        if (_open)
        {
            // The teal ring sits 1 to 3.5 DIPs outside the medallion, as its box-shadow does in the mockup.
            context.DrawEllipse(null, RingPen, centre, radius + 2.25, radius + 2.25);
        }
        var face = _pressed ? PressedFace : _hover ? HoverFace : NormalFace;
        context.DrawEllipse(face, RimPen, centre, radius - 0.5, radius - 0.5);
        context.DrawEllipse(null, InnerPen, centre, radius - 1.5, radius - 1.5);

        var colour = _open ? DerethPalette.TealText : _hover ? GoldLight : DerethPalette.Gold;
        var scale = _glyphSize / DerethGlyphs.Grid;
        var offset = (_diameter - _glyphSize) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset, offset)))
            foreach (var part in DerethGlyphs.PartsOf(_glyph))
                DrawPart(context, part, colour);
    }

    private static void DrawPart(DrawingContext context, GlyphPart part, Color colour)
    {
        if (!Shapes.TryGetValue(part.Data, out var shape))
            Shapes[part.Data] = shape = Geometry.Parse(part.Data);
        var brush = new SolidColorBrush(colour, part.Opacity);
        if (part.Filled)
        {
            context.DrawGeometry(brush, null, shape);
            return;
        }
        var pen = new Pen(brush, part.StrokeWidth, part.Dash is null ? null : new DashStyle(part.Dash, 0), PenLineCap.Round, PenLineJoin.Round);
        context.DrawGeometry(null, pen, shape);
    }

    private static RadialGradientBrush Radial(Color centre, Color edge) => new()
    {
        Center = new RelativePoint(0.5, 0.38, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.38, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
        GradientStops = { new GradientStop(centre, 0), new GradientStop(edge, 1) }
    };
}

/// <summary>The gold divider between groups of toolbar buttons: 1 DIP wide, 26 tall, with 3 DIPs either side.</summary>
public sealed class DerethDivider : Control
{
    private static readonly IBrush Brush = DerethPalette.Brush(Color.Parse("#7A5E30"));

    public DerethDivider()
    {
        Width = 1;
        Height = 26;
        Margin = new Thickness(3, 0);
    }

    public override void Render(DrawingContext context) => context.DrawRectangle(Brush, null, new Rect(Bounds.Size));
}
