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

/// <summary>
/// The gilded vitals' shared gold: the vertical gold of rules and caps, the round gold of a bezel or stud, and the navy inside them.
/// </summary>
internal static class DerethGilt
{
    public static readonly IBrush GoldVertical = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#F3DDA2"), 0), new GradientStop(Color.Parse("#C9A45C"), 0.4),
            new GradientStop(Color.Parse("#7A5E30"), 0.75), new GradientStop(Color.Parse("#B08A44"), 1)
        }
    };
    public static readonly IBrush GoldRound = new RadialGradientBrush
    {
        Center = new RelativePoint(0.36, 0.32, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.36, 0.32, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.72, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.72, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#FFF1C8"), 0), new GradientStop(Color.Parse("#D9B66A"), 0.35),
            new GradientStop(Color.Parse("#7A5E30"), 0.78), new GradientStop(Color.Parse("#3A2A12"), 1)
        }
    };
    public static readonly IBrush NavyRound = new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.65, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.65, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#14283A"), 0), new GradientStop(Color.Parse("#091420"), 0.7), new GradientStop(Color.Parse("#04080C"), 1) }
    };
    public static readonly IBrush Inlay = DerethPalette.Brush(Color.Parse("#0A1620"));
    public static readonly IPen Outline = new Pen(DerethPalette.Brush(Color.Parse("#2A1C0C")), 0.5);
    public static readonly IPen GoldDarkHair = new Pen(DerethPalette.GoldDarkBrush, 0.5);

    /// <summary>A gold lozenge with a navy heart, centred on (x, y), <paramref name="radius"/> from centre to point.</summary>
    public static void Lozenge(DrawingContext context, double x, double y, double radius)
    {
        context.DrawGeometry(GoldVertical, Outline, Diamond(x, y, radius));
        context.DrawGeometry(Inlay, null, Diamond(x, y, radius * 0.45));
    }

    private static StreamGeometry Diamond(double x, double y, double r)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x, y - r), true);
        g.LineTo(new Point(x + r, y));
        g.LineTo(new Point(x, y + r));
        g.LineTo(new Point(x - r, y));
        g.EndFigure(true);
        return geometry;
    }

    /// <summary>A rectangle with its corners cut at 45 degrees by <paramref name="cut"/>.</summary>
    public static StreamGeometry Chamfered(Rect rect, double cut)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(rect.Left + cut, rect.Top), true);
        g.LineTo(new Point(rect.Right - cut, rect.Top));
        g.LineTo(new Point(rect.Right, rect.Top + cut));
        g.LineTo(new Point(rect.Right, rect.Bottom - cut));
        g.LineTo(new Point(rect.Right - cut, rect.Bottom));
        g.LineTo(new Point(rect.Left + cut, rect.Bottom));
        g.LineTo(new Point(rect.Left, rect.Bottom - cut));
        g.LineTo(new Point(rect.Left, rect.Top + cut));
        g.EndFigure(true);
        return geometry;
    }
}

/// <summary>
/// The gilded vitals plate: navy with a faint gold damask, the Dereth double gold rule on chamfered corners and, when ornate, a ringed corner
/// with a filigree scroll at each corner, a gold lozenge on the top edge and a gold stud on the bottom edge. Its child sits on top.
/// </summary>
public sealed class DerethGildedPlate : Decorator
{
    private static readonly IBrush Navy = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#112230"), 0), new GradientStop(Color.Parse("#0A1620"), 0.35), new GradientStop(Color.Parse("#050A0E"), 1) }
    };
    // The damask: gold lozenge outlines on a 12 DIP grid, faint enough that the bars read first.
    private static readonly IPen Damask = new Pen(DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x1A)), 0.4);
    private static readonly IPen OuterRule = new Pen(DerethGilt.GoldVertical, 1.6);
    private static readonly IPen InnerRule = new Pen(DerethPalette.GoldDarkBrush, 0.8);
    private static readonly IPen Shadow = new Pen(DerethPalette.Brush(Color.FromArgb(0x80, 0, 0, 0)), 0.6);
    private static readonly IPen Scroll = new Pen(DerethPalette.GoldBrush, 0.75, lineCap: PenLineCap.Round);
    private static readonly IPen Ring = new Pen(DerethGilt.GoldVertical, 0.9);
    private static readonly IPen BottomRule = new Pen(DerethPalette.Brush(Color.Parse("#E3C27A")), 1.6);
    private static readonly StreamGeometry CornerScroll = CornerScrollGeometry();
    private readonly double _cut;
    private readonly bool _ornate;

    /// <param name="cut">How far the corners are cut, in DIPs.</param>
    /// <param name="ornate">Whether the corners, the top lozenge and the bottom stud are drawn.</param>
    public DerethGildedPlate(double cut, bool ornate)
    {
        _cut = cut;
        _ornate = ornate;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var outline = DerethGilt.Chamfered(new Rect(size), _cut);
        context.DrawGeometry(Navy, null, outline);
        using (context.PushGeometryClip(outline)) DrawDamask(context, size);
        context.DrawGeometry(null, OuterRule, DerethGilt.Chamfered(new Rect(size).Deflate(0.8), _cut));
        context.DrawGeometry(null, InnerRule, DerethGilt.Chamfered(new Rect(size).Deflate(3.4), _cut - 1.2));
        context.DrawGeometry(null, Shadow, DerethGilt.Chamfered(new Rect(size).Deflate(4.4), _cut - 1.6));
        if (!_ornate) return;

        foreach (var (x, y, sx, sy) in new[] { (0.0, 0.0, 1.0, 1.0), (size.Width, 0, -1, 1), (0, size.Height, 1, -1), (size.Width, size.Height, -1, -1) })
        {
            using (context.PushTransform(Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(x, y)))
            {
                context.DrawGeometry(null, Scroll, CornerScroll);
                context.DrawEllipse(DerethGilt.Inlay, Ring, new Point(7.4, 7.4), 2.5, 2.5);
                context.DrawEllipse(DerethPalette.GoldBrush, null, new Point(7.4, 7.4), 0.8, 0.8);
            }
        }
        DerethGilt.Lozenge(context, size.Width / 2, 4.4, 4.2);
        var bottom = size.Height - 0.8;
        context.DrawLine(BottomRule, new Point(size.Width / 2 - 22, bottom), new Point(size.Width / 2 + 22, bottom));
        context.DrawEllipse(DerethGilt.GoldRound, DerethGilt.Outline, new Point(size.Width / 2, size.Height - 2.3), 2.2, 2.2);
    }

    private static void DrawDamask(DrawingContext context, Size size)
    {
        for (var x = 0.0; x < size.Width; x += 12)
            for (var y = 0.0; y < size.Height; y += 12)
            {
                context.DrawLine(Damask, new Point(x + 6, y + 1.5), new Point(x + 8, y + 6));
                context.DrawLine(Damask, new Point(x + 8, y + 6), new Point(x + 6, y + 10.5));
                context.DrawLine(Damask, new Point(x + 6, y + 10.5), new Point(x + 4, y + 6));
                context.DrawLine(Damask, new Point(x + 4, y + 6), new Point(x + 6, y + 1.5));
            }
    }

    // The top-left corner's scroll, one curl along each edge from the ring; the other corners mirror it.
    private static StreamGeometry CornerScrollGeometry()
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(11.5, 3.4), false);
        g.CubicBezierTo(new Point(15.4, 3.4), new Point(17.6, 5.4), new Point(17, 7.6));
        g.CubicBezierTo(new Point(16.4, 6.2), new Point(14.8, 5.9), new Point(13.8, 6.6));
        g.EndFigure(false);
        g.BeginFigure(new Point(3.4, 11.5), false);
        g.CubicBezierTo(new Point(3.4, 15.4), new Point(5.4, 17.6), new Point(7.6, 17));
        g.CubicBezierTo(new Point(6.2, 16.4), new Point(5.9, 14.8), new Point(6.6, 13.8));
        g.EndFigure(false);
        return geometry;
    }
}

/// <summary>A vital's numbers' plaque: chamfered navy with a dark gold edge. Its child, the value, sits inside.</summary>
public sealed class DerethValuePlaque : Decorator
{
    private static readonly IBrush Face = DerethPalette.Brush(Color.Parse("#05090D"));
    private static readonly IPen Edge = new Pen(DerethPalette.GoldDarkBrush, 0.9);

    public override void Render(DrawingContext context) =>
        context.DrawGeometry(Face, Edge, DerethGilt.Chamfered(new Rect(Bounds.Size).Deflate(0.45), 2.5));
}

/// <summary>A gold lozenge between two vitals of the side-by-side strip.</summary>
public sealed class DerethLozenge : Control
{
    private const double Radius = 3.6;

    public DerethLozenge()
    {
        Width = Radius * 2;
        Height = Radius * 2;
    }

    public override void Render(DrawingContext context) => DerethGilt.Lozenge(context, Radius, Radius, Radius);
}

/// <summary>A vital's glyph in a gold bezel with an engraved rim of 16 ticks, the glyph in the vital's colour on navy.</summary>
public sealed class DerethVitalGlyph : Control
{
    private static readonly IPen Tick = new Pen(DerethPalette.Brush(Color.Parse("#4A3414")), 0.5);
    private readonly DerethGlyph _glyph;
    private readonly Color _tint;

    /// <param name="diameter">The bezel's diameter in DIPs.</param>
    public DerethVitalGlyph(DerethGlyph glyph, Color tint, double diameter)
    {
        _glyph = glyph;
        _tint = tint;
        Width = diameter;
        Height = diameter;
    }

    public override void Render(DrawingContext context)
    {
        var r = Bounds.Width / 2;
        var centre = new Point(r, r);
        context.DrawEllipse(DerethGilt.GoldRound, DerethGilt.Outline, centre, r, r);
        for (var i = 0; i < 16; i++)
        {
            var a = i * Math.PI / 8;
            var (c, s) = (Math.Cos(a), Math.Sin(a));
            context.DrawLine(Tick, new Point(r + c * (r - 1.6), r + s * (r - 1.6)), new Point(r + c * (r - 0.4), r + s * (r - 0.4)));
        }
        context.DrawEllipse(DerethGilt.NavyRound, DerethGilt.Outline, centre, r - 2, r - 2);
        context.DrawEllipse(null, DerethGilt.GoldDarkHair, centre, r - 2.6, r - 2.6);
        var glyph = r * 1.05;
        DerethGlyphs.Draw(context, _glyph, glyph, r - glyph / 2, r - glyph / 2, _tint);
    }
}

/// <summary>
/// A vital's bar: glass in a sunken groove between two pointed gold end caps (drawn just outside the bar), the pale trail behind a drop, a
/// highlight along its top, quarter marks engraved under it when asked, and a red edge that pulses while health is low. A light runs once
/// along the fill when the value jumps (a hit, a heal, a spell), not while it regenerates, so a still bar draws no new frames.
/// <see cref="Show"/> sets the values.
/// </summary>
public sealed class DerethVitalBar : Control
{
    /// <summary>The stacked bar's height in DIPs.</summary>
    public const double BarHeight = 10;
    // A jump of at least this much of the max in one frame runs the light; regeneration moves far less per frame.
    private const double ShineJump = 0.02;
    private static readonly TimeSpan ShineTime = TimeSpan.FromSeconds(0.9);
    private const double ShineWidth = 30;
    private static readonly IBrush Groove = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#020406"), 0), new GradientStop(Color.Parse("#0B141C"), 0.55), new GradientStop(Color.Parse("#05080C"), 1) }
    };
    private static readonly IPen GrooveEdge = new Pen(DerethPalette.GrooveEdgeBrush, 0.7);
    private static readonly IBrush Highlight = DerethPalette.Brush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush Shine = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), 0.5), new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1) }
    };
    private static readonly IPen TickPen = new Pen(DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x99)), 0.7);
    private static readonly Color PulseRed = Color.Parse("#E0554E");
    // The pulse's glow and edge: a pen and brush each, made once; Show sets the brushes' opacity, so drawing allocates nothing.
    private readonly SolidColorBrush _glowBrush = new(PulseRed);
    private readonly SolidColorBrush _edgeBrush = new(PulseRed);
    private readonly Pen _glow;
    private readonly Pen _edge;
    private readonly IBrush _fill;
    private readonly IBrush _trailBrush;
    private readonly bool _ticks;
    private double _fraction;
    private double _trail;
    private bool _pulsing;
    private double _pulse;
    private TimeSpan? _shineStart;
    private double _shine = -1;

    /// <summary>How full the fill is, from 0 to 1, as last shown.</summary>
    public double Fraction => _fraction;
    /// <summary>How far the pale trail reaches, from 0 to 1, as last shown.</summary>
    public double TrailFraction => _trail;
    /// <summary>Whether the edge pulses, as last shown.</summary>
    public bool Pulsing => _pulsing;
    /// <summary>Whether the light is running along the fill, as last shown.</summary>
    public bool Shining => _shine >= 0;

    /// <param name="ticks">Whether quarter marks are engraved under the bar.</param>
    public DerethVitalBar(Color light, Color mid, Color dark, bool ticks = true)
    {
        _ticks = ticks;
        Height = BarHeight;
        _glow = new Pen(_glowBrush, 2);
        _edge = new Pen(_edgeBrush, 1);
        var deep = Mix(dark, Colors.Black, 0.55);
        _fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(deep, 0), new GradientStop(dark, 0.25), new GradientStop(mid, 0.46), new GradientStop(light, 0.54),
                new GradientStop(Mix(mid, dark, 0.4), 0.74), new GradientStop(deep, 1)
            }
        };
        _trailBrush = DerethPalette.Brush(Mix(light, Colors.White, 0.6).WithAlpha(0x80));
    }

    /// <summary>
    /// Sets the values, as fractions of the vital's max: the fill, the trail's end (drawn from the fill's end when it reaches past it), and
    /// while <paramref name="pulsing"/> the pulse's strength from 0 to 1, at <paramref name="now"/> on the player's clock. The bar is
    /// redrawn only when one of them changes or the light is running.
    /// </summary>
    public void Show(double fraction, double trail, bool pulsing, double pulse, TimeSpan now)
    {
        var shownFraction = Clamp(fraction);
        var shownTrail = Math.Max(shownFraction, Clamp(trail));
        // The pulse's phase moves nothing while health is not pulsing.
        var shownPulse = pulsing ? Clamp(pulse) : 0;
        if (Math.Abs(shownFraction - _fraction) >= ShineJump) _shineStart = now;
        var shine = -1.0;
        if (_shineStart is { } start)
        {
            var progress = (now - start).TotalSeconds / ShineTime.TotalSeconds;
            if (progress is >= 0 and < 1) shine = progress;
            else _shineStart = null;
        }
        if (shownFraction == _fraction && shownTrail == _trail && pulsing == _pulsing && shownPulse == _pulse && shine == _shine) return;
        _fraction = shownFraction;
        _trail = shownTrail;
        _pulsing = pulsing;
        _pulse = shownPulse;
        _shine = shine;
        _glowBrush.Opacity = 0.5 * _pulse;
        _edgeBrush.Opacity = 0.35 + 0.65 * _pulse;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var radius = height / 2;
        var groove = new RoundedRect(new Rect(0, 0, width, height), radius);
        context.DrawRectangle(Brushes.Black, GrooveEdge, new RoundedRect(new Rect(-0.7, -0.7, width + 1.4, height + 1.4), radius + 0.7));
        using (context.PushClip(groove))
        {
            context.DrawRectangle(Groove, null, new Rect(0, 0, width, height));
            var trailEnd = width * _trail;
            var fillEnd = width * _fraction;
            if (trailEnd > 0) context.DrawRectangle(_trailBrush, null, new Rect(0, 0, trailEnd, height));
            if (fillEnd > 0)
            {
                context.DrawRectangle(_fill, null, new Rect(0, 0, fillEnd, height));
                if (_shine >= 0)
                    using (context.PushClip(new Rect(0, 0, fillEnd, height)))
                        context.DrawRectangle(Shine, null, new Rect(-ShineWidth + (fillEnd + ShineWidth) * _shine, 0, ShineWidth, height));
            }
            context.DrawRectangle(Highlight, null, new RoundedRect(new Rect(3, 1, Math.Max(0, width - 6), height * 0.26), height * 0.13));
        }

        DrawCap(context, -1.6, -1, height);
        DrawCap(context, width + 1.6, 1, height);
        if (_ticks)
            foreach (var q in new[] { 0.25, 0.5, 0.75 })
                context.DrawLine(TickPen, new Point(width * q, height + 2), new Point(width * q, height + 4));

        if (!_pulsing) return;
        // The edge pulses with a glow just outside it, so the warning is the only thing on the window that moves.
        context.DrawRectangle(null, _glow, new RoundedRect(new Rect(-1, -1, width + 2, height + 2), radius + 1));
        context.DrawRectangle(null, _edge, new RoundedRect(new Rect(0.5, 0.5, width - 1, height - 1), radius));
    }

    // A gold collar at the bar's end with a point outward: from x towards dir.
    private static void DrawCap(DrawingContext context, double x, double dir, double height)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x, -2), true);
            g.LineTo(new Point(x + dir * 2.6, -2));
            g.LineTo(new Point(x + dir * 5, height / 2));
            g.LineTo(new Point(x + dir * 2.6, height + 2));
            g.LineTo(new Point(x, height + 2));
            g.EndFigure(true);
        }
        context.DrawGeometry(DerethGilt.GoldVertical, DerethGilt.Outline, geometry);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

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
