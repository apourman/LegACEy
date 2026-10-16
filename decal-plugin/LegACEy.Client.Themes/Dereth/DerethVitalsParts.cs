using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>The vitals' colours, from retail's bars: each vital's light tone for its glyph, and three tones for its fill, top to bottom.</summary>
public static class DerethVitalsPalette
{
    public static readonly Color HealthLight = Color.Parse("#EE6A5E");
    public static readonly Color HealthMid = Color.Parse("#C9363C");
    public static readonly Color HealthDark = Color.Parse("#7E1D24");
    public static readonly Color StaminaLight = Color.Parse("#F0CF6A");
    public static readonly Color StaminaMid = Color.Parse("#C99A2C");
    public static readonly Color StaminaDark = Color.Parse("#7A5814");
    public static readonly Color ManaLight = Color.Parse("#6CB8EE");
    public static readonly Color ManaMid = Color.Parse("#2F7FC6");
    public static readonly Color ManaDark = Color.Parse("#163F72");
}

/// <summary>A vital's glyph in its small gold bezel: a 13 DIP disc with the glyph 9 DIPs across, in the vital's colour.</summary>
public sealed class DerethVitalGlyph : Control
{
    /// <summary>The bezel's diameter in DIPs.</summary>
    public const double BezelSize = 13;
    private const double GlyphSize = 9;
    private static readonly IPen RimPen = new Pen(DerethPalette.Brush(Color.Parse("#7A5E30")), 1);
    private static readonly IBrush Face = new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.38, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.38, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#1B2832"), 0), new GradientStop(Color.Parse("#081016"), 1) }
    };

    private readonly DerethGlyph _glyph;
    private readonly Color _tint;

    public DerethVitalGlyph(DerethGlyph glyph, Color tint)
    {
        _glyph = glyph;
        _tint = tint;
        Width = BezelSize;
        Height = BezelSize;
    }

    public override void Render(DrawingContext context)
    {
        var radius = BezelSize / 2;
        context.DrawEllipse(Face, RimPen, new Point(radius, radius), radius - 0.5, radius - 0.5);
        var offset = (BezelSize - GlyphSize) / 2;
        DerethGlyphs.Draw(context, _glyph, GlyphSize, offset, offset, _tint);
    }
}

/// <summary>
/// A vital's bar: a groove, the fill in three tones, the pale trail behind a drop, and a red edge that pulses while health is low.
/// <see cref="Show"/> sets the values; the bar draws nothing else. Retail's bar is 11 DIPs high.
/// </summary>
public sealed class DerethVitalBar : Control
{
    /// <summary>The bar's height in DIPs.</summary>
    public const double BarHeight = 11;
    private static readonly IPen GrooveEdge = new Pen(DerethPalette.GrooveEdgeBrush, 1);
    private static readonly IBrush Trail = DerethPalette.Brush(Color.FromArgb(0x8C, 0xFF, 0xEC, 0xC8));
    private static readonly Color PulseRed = Color.Parse("#E0554E");
    // The pulse's glow and edge: a pen and brush each, made once; Show sets the brushes' opacity, so drawing allocates nothing.
    private readonly SolidColorBrush _glowBrush = new(PulseRed);
    private readonly SolidColorBrush _edgeBrush = new(PulseRed);
    private readonly Pen _glow;
    private readonly Pen _edge;
    private readonly IBrush _fill;
    private double _fraction;
    private double _trail;
    private bool _pulsing;
    private double _pulse;

    /// <summary>How full the fill is, from 0 to 1, as last shown.</summary>
    public double Fraction => _fraction;
    /// <summary>How far the pale trail reaches, from 0 to 1, as last shown.</summary>
    public double TrailFraction => _trail;
    /// <summary>Whether the edge pulses, as last shown.</summary>
    public bool Pulsing => _pulsing;

    public DerethVitalBar(Color light, Color mid, Color dark)
    {
        Height = BarHeight;
        _glow = new Pen(_glowBrush, 2);
        _edge = new Pen(_edgeBrush, 1);
        _fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(light, 0), new GradientStop(mid, 0.45), new GradientStop(dark, 1) }
        };
    }

    /// <summary>
    /// Sets the values, as fractions of the vital's max: the fill, the trail's end (drawn from the fill's end when it reaches past it), and
    /// while <paramref name="pulsing"/> the pulse's strength from 0 to 1. The bar is redrawn only when one of them changes.
    /// </summary>
    public void Show(double fraction, double trail, bool pulsing, double pulse)
    {
        var shownFraction = Clamp(fraction);
        var shownTrail = Math.Max(shownFraction, Clamp(trail));
        // The pulse's phase moves nothing while health is not pulsing.
        var shownPulse = pulsing ? Clamp(pulse) : 0;
        if (shownFraction == _fraction && shownTrail == _trail && pulsing == _pulsing && shownPulse == _pulse) return;
        _fraction = shownFraction;
        _trail = shownTrail;
        _pulsing = pulsing;
        _pulse = shownPulse;
        _glowBrush.Opacity = 0.5 * _pulse;
        _edgeBrush.Opacity = 0.35 + 0.65 * _pulse;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.DrawRectangle(DerethPalette.GrooveBrush, GrooveEdge, new RoundedRect(new Rect(0, 0, width, height), height / 2));

        // Retail's fill sits 1 DIP inside the groove.
        var left = 1.0;
        var span = width - 2;
        var inner = height - 2;
        var radius = inner / 2;
        var trailEnd = left + span * _trail;
        var fillEnd = left + span * _fraction;
        if (trailEnd > left)
            context.DrawRectangle(Trail, null, new RoundedRect(new Rect(left, 1, trailEnd - left, inner), radius));
        if (fillEnd > left)
            context.DrawRectangle(_fill, null, new RoundedRect(new Rect(left, 1, fillEnd - left, inner), radius));

        if (!_pulsing) return;
        // The edge pulses with a glow just outside it, so the warning is the only thing on the window that moves.
        context.DrawRectangle(null, _glow, new RoundedRect(new Rect(-1, -1, width + 2, height + 2), (height + 2) / 2));
        context.DrawRectangle(null, _edge, new RoundedRect(new Rect(0.5, 0.5, width - 1, height - 1), height / 2));
    }

    private static double Clamp(double value) => Math.Max(0, Math.Min(1, value));
}

/// <summary>
/// The stretch grip at a window's bottom right: three gold ridges across the corner, 9 DIPs square. The window shows it while the
/// pointer is over it and hides it while retail's UI is locked.
/// </summary>
public sealed class DerethStretchGrip : Control
{
    /// <summary>The grip's size in DIPs.</summary>
    public const double GripSize = 9;
    private static readonly IPen Ridge = new Pen(DerethPalette.GoldBrush, 1.2);

    public DerethStretchGrip()
    {
        Width = GripSize;
        Height = GripSize;
    }

    public override void Render(DrawingContext context)
    {
        // Each ridge runs across the corner: the points where x + y = c, clipped to the square.
        foreach (var c in new[] { 8.0, 11.9, 15.8 })
            context.DrawLine(Ridge, new Point(Math.Max(0, c - GripSize), Math.Min(GripSize, c)), new Point(Math.Min(GripSize, c), Math.Max(0, c - GripSize)));
    }
}
