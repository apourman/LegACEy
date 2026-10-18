using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// A round gold medallion with a glyph: a window button, the stance, Use, Examine or the backpack. Normal, hover and pressed
/// art, and the green ring while <see cref="IsOpen"/>. A click is a release over the medallion after a press on it.
/// </summary>
public sealed class DerethMedallion : Decorator
{
    private static readonly Color GoldLight = Color.Parse("#DDC47D");
    private static readonly IPen RimPen = new Pen(DerethPalette.Brush(Color.Parse("#8A6326")), 1);
    private static readonly IPen InnerPen = new Pen(DerethPalette.Brush(Color.Parse("#DDC47D").WithAlpha(0x59)), 1);
    private static readonly IPen RingPen = new Pen(DerethPalette.TealBrush, 2.5);
    private static readonly IBrush PressedFace = DerethPalette.Brush(Color.Parse("#0A0805"));
    private static readonly IBrush NormalFace = Radial(Color.Parse("#2E2A1E"), Color.Parse("#18160F"));
    private static readonly IBrush HoverFace = Radial(Color.Parse("#3E3826"), Color.Parse("#221E14"));

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

    /// <summary>Shows the green ring: the window or the menu it opens is open.</summary>
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
        base.OnPointerReleased(e);
        var wasPressed = _pressed;
        _pressed = false;
        e.Handled = true;
        InvalidateVisual();
        if (wasPressed && new Rect(Bounds.Size).Contains(e.GetPosition(this))) Clicked?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        var centre = new Point(_diameter / 2, _diameter / 2);
        var radius = _diameter / 2;
        if (_open)
        {
            // The green ring sits 1 to 3.5 DIPs outside the medallion, as its box-shadow does in the mockup.
            context.DrawEllipse(null, RingPen, centre, radius + 2.25, radius + 2.25);
        }
        var face = _pressed ? PressedFace : _hover ? HoverFace : NormalFace;
        context.DrawEllipse(face, RimPen, centre, radius - 0.5, radius - 0.5);
        context.DrawEllipse(null, InnerPen, centre, radius - 1.5, radius - 1.5);

        var colour = _open ? DerethPalette.TealText : _hover ? GoldLight : DerethPalette.Gold;
        var offset = (_diameter - _glyphSize) / 2;
        DerethGlyphs.Draw(context, _glyph, _glyphSize, offset, offset, colour);
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
    private static readonly IBrush Brush = DerethPalette.Brush(Color.Parse("#8A6326"));

    public DerethDivider()
    {
        Width = 1;
        Height = 26;
        Margin = new Thickness(3, 0);
    }

    public override void Render(DrawingContext context) => context.DrawRectangle(Brush, null, new Rect(Bounds.Size));
}
