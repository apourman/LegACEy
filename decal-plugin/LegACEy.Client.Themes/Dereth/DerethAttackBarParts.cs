using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// What the attack bar shows: the bar's Accuracy or Power label and set point, the charge on it, the lit height plaque, the three options and
/// whether the Recklessness range is marked.
/// </summary>
/// <param name="LitPlaque">The lit height: 0 High, 1 Medium, 2 Low.</param>
public readonly record struct DerethAttackFace(bool Accuracy, double SetPoint, double Charge, int LitPlaque, bool RepeatAttacks, bool AutoTarget,
    bool KeepInView, bool Reckless);

/// <summary>
/// Where the attack bar's parts sit for a size in DIPs. At the design size, 320 by 58, it is the mockup's layout. A taller window draws the
/// whole bar larger, text and controls with it, by <see cref="Scale"/>; the parts are placed in the unscaled <see cref="Size"/>. Width
/// beyond that lengthens the bar, carrying its right label, the divider and the plaques with the right edge, and spreads the option gems. Height
/// beyond it, in a window too narrow to scale up further, thickens the bar and spreads the rows.
/// </summary>
public sealed class DerethAttackLayout
{
    private readonly double _wide, _tall;

    public DerethAttackLayout(Size size)
    {
        // The bar scales up with its height, as far as its width allows without going below its narrowest layout.
        Scale = Math.Max(1, Math.Min(size.Height / DerethAttackBarArt.DesignHeight, size.Width / DerethAttackBarArt.MinimumWidth));
        Size = new Size(size.Width / Scale, size.Height / Scale);
        _wide = Size.Width - DerethAttackBarArt.DesignWidth;
        _tall = Math.Max(0, Size.Height - DerethAttackBarArt.DesignHeight);
        Groove = new Rect(46, 14 + _tall * 0.15, Math.Max(40, 168 + _wide), Math.Min(18, 10 + _tall * 0.3));
        GrooveHit = new Rect(Groove.X - 4, Groove.Y - 7, Groove.Width + 8, Groove.Height + 12);
    }

    /// <summary>How much larger than its design the bar draws: 1 at the design height, growing with the height as far as the width allows.</summary>
    public double Scale { get; }
    /// <summary>The size the parts are placed in, before <see cref="Scale"/>; every rectangle and point below is in it.</summary>
    public Size Size { get; }
    /// <summary>The groove: the bar's glass, whose middle line is the bar's centre.</summary>
    public Rect Groove { get; }
    /// <summary>The groove's click area: 4 DIPs past each end of the bar, so a press just outside the glass still sets the point.</summary>
    public Rect GrooveHit { get; }
    /// <summary>Where the Power or Accuracy label starts: just past the bar's right cap.</summary>
    public double RightLabelX => Groove.Right + 8;
    /// <summary>The gold divider before the plaques, and how far down it runs.</summary>
    public double DividerX => Size.Width - 58;
    public double DividerBottom => 49 + _tall;
    /// <summary>Where the plate's top lozenge, bottom rule and stud are centred: over the bar, as the mockup places them.</summary>
    public double OrnamentX => 122 + _wide / 2;

    /// <summary>A height plaque's rectangle, drawn and clicked: 0 High, 1 Medium, 2 Low, from the top.</summary>
    public Rect Plaque(int index)
    {
        // At the design size the plaques are 12 tall on a 14 DIP pitch from y 9; added height grows both.
        double pitch = 14 + _tall / 3, height = Math.Min(22, 12 + _tall * 0.2);
        var middle = 9 + pitch * index + pitch / 2 - 1;
        return new Rect(Size.Width - 46, middle - height / 2, 36, height);
    }

    /// <summary>An option gem's centre: Repeat Attacks, Auto Target, Keep in View, from the left.</summary>
    public Point Toggle(int index) => new(16 + index * (74 + Math.Max(0, _wide) / 3), 42 + _tall * 0.75);

    /// <summary>An option's click area: its gem and its label. The last label is shorter, so its area is narrower, as the mockup's.</summary>
    public Rect ToggleHit(int index) => new(Toggle(index).X - 6, Toggle(index).Y - 7, index == 2 ? 64 : 70, 14);
}

/// <summary>
/// The attack bar's art, laid out by <see cref="DerethAttackLayout"/> for its size: the Speed ↔ Power (or Accuracy) bar in the vitals' glass
/// groove between gold end caps, its set point as a green gem on a gold pin, the charge filling from the left, three height plaques behind a
/// gold divider with the lit one's arrows, and three option gems. With Recklessness trained, its range is tinted ember in the groove. It
/// draws only when its face or its size changes, so a still bar draws no new frame.
/// </summary>
public sealed class DerethAttackBarArt : Control
{
    /// <summary>The bar's design size in DIPs: the size it opens at and the smallest height it lays out for.</summary>
    public const int DesignWidth = 320, DesignHeight = 58;
    /// <summary>The narrowest the bar lays out for, its bar 128 DIPs long, and the largest size, in DIPs.</summary>
    public const int MinimumWidth = 280, MaximumWidth = 960, MaximumHeight = 150;

    private const double GemRadius = 3.4;
    /// <summary>The part of the bar where Recklessness applies, as ACE checks it: from 10% to 90%.</summary>
    private const double RecklessFrom = 0.1, RecklessTo = 0.9;
    private static readonly string[] PlaqueNames = { "High", "Medium", "Low" };
    private static readonly string[] ToggleNames = { "Repeat Attacks", "Auto Target", "Keep in View" };
    // Attack-only colours from the mockup. The rest of the bar is the vitals' gilt and the palette's.
    private static readonly Color Ember = Color.Parse("#D2642A");
    private static readonly Color PlateLitTop = Color.Parse("#F3DDA2");
    private static readonly Color PlateLitBottom = Color.Parse("#8A6A34");
    private static readonly Color LitTextColor = Color.Parse("#1A1008");
    private static readonly Color GemLight = Color.Parse("#E6FFEC");
    private static readonly Color GemOnEdge = Color.Parse("#1F7A35");
    private static readonly Color GemOnDeep = Color.Parse("#06240E");
    private static readonly Color GemOffLight = Color.Parse("#8A8070");
    private static readonly Color GemOffMid = Color.Parse("#2E2A1E");
    private static readonly Color GemOffDark = Color.Parse("#0A0805");

    private static readonly IBrush PowerFill = Linear(new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative),
        (DerethVitalsPalette.StaminaMid, 0), (Ember, 0.55), (DerethVitalsPalette.HealthMid, 1));
    private static readonly IBrush AccuracyFill = Linear(new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative),
        (DerethVitalsPalette.StaminaMid, 0), (DerethPalette.Raised, 0.55), (DerethPalette.Teal, 1));
    private static readonly IBrush Shade = Linear(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative),
        (Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.35), (Color.FromArgb(0x73, 0, 0, 0), 1));
    private static readonly IBrush Zone = DerethPalette.Brush(DerethPalette.Cream.WithAlpha(0x12));
    private static readonly IBrush RecklessZone = DerethPalette.Brush(Ember.WithAlpha(0x2C));
    private static readonly IPen RecklessEdge = new Pen(DerethPalette.Brush(Ember.WithAlpha(0xC0)), 0.9);
    private static readonly IBrush PlateLit = Linear(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative),
        (PlateLitTop, 0), (DerethPalette.Gold, 0.5), (PlateLitBottom, 1));
    private static readonly IBrush LitText = DerethPalette.Brush(LitTextColor);
    private static readonly IBrush GemOn = Radial((GemLight, 0), (DerethPalette.TealText, 0.25), (GemOnEdge, 0.65), (GemOnDeep, 1));
    private static readonly IBrush GemOff = Radial((GemOffLight, 0), (GemOffMid, 0.3), (GemOffDark, 1));
    private static readonly IPen Pin = new Pen(DerethPalette.GoldBrush, 1.6);
    private static readonly IPen Divider = new Pen(DerethPalette.GoldDarkBrush, 0.8);
    private static readonly IPen DividerShadow = new Pen(DerethPalette.Brush(Color.FromArgb(0x80, 0, 0, 0)), 0.6);
    private static readonly IPen LitEdge = new Pen(DerethGilt.Outline.Brush, 0.9);
    private static readonly IPen UnlitEdge = new Pen(DerethPalette.GoldDarkBrush, 0.9);
    private static readonly IPen GemEdge = new Pen(DerethGilt.Outline.Brush, 0.4);

    private DerethAttackFace _face = new(false, 0, 0, 0, false, false, false, false);

    public DerethAttackBarArt() => IsHitTestVisible = false;

    /// <summary>The face as last shown.</summary>
    public DerethAttackFace Face => _face;

    /// <summary>Shows <paramref name="face"/>, redrawing only when it differs from the face shown.</summary>
    public void Show(DerethAttackFace face)
    {
        if (face == _face) return;
        _face = face;
        InvalidateVisual();
    }

    /// <summary>A value clamped to 0 to 1.</summary>
    public static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

    public override void Render(DrawingContext context)
    {
        var face = _face;
        var layout = new DerethAttackLayout(Bounds.Size);
        using var scaled = context.PushTransform(Matrix.CreateScale(layout.Scale, layout.Scale));
        var middle = layout.Groove.Center.Y;
        Text(context, "Speed", 9, DerethPalette.MutedBrush, 12, middle, centred: false);
        Text(context, face.Accuracy ? "Accuracy" : "Power", 9, DerethPalette.MutedBrush, layout.RightLabelX, middle, centred: false);
        DrawGroove(context, layout.Groove, face);
        context.DrawLine(Divider, new Point(layout.DividerX, 9), new Point(layout.DividerX, layout.DividerBottom));
        context.DrawLine(DividerShadow, new Point(layout.DividerX + 1, 9), new Point(layout.DividerX + 1, layout.DividerBottom));
        for (var i = 0; i < PlaqueNames.Length; i++) DrawPlaque(context, layout.Plaque(i), i, i == face.LitPlaque);
        for (var i = 0; i < ToggleNames.Length; i++)
            DrawToggle(context, layout.Toggle(i), i, i switch { 0 => face.RepeatAttacks, 1 => face.AutoTarget, _ => face.KeepInView });
    }

    private static void DrawGroove(DrawingContext context, Rect body, DerethAttackFace face)
    {
        var set = Clamp01(face.SetPoint);
        var glass = new Rect(0, 0, body.Width, body.Height);
        using (context.PushTransform(Matrix.CreateTranslation(body.X, body.Y)))
        {
            using (DerethGroove.Begin(context, body.Width, body.Height, DerethPalette.GrooveBrush))
            {
                // The part up to the set point is washed pale, so where the charge should stop reads at a glance.
                context.DrawRectangle(Zone, null, new Rect(0, 0, body.Width * set, body.Height));
                // Recklessness's range: an ember wash under the charge, between ember edges drawn over it.
                double from = body.Width * RecklessFrom, to = body.Width * RecklessTo;
                if (face.Reckless) context.DrawRectangle(RecklessZone, null, new Rect(from, 0, to - from, body.Height));
                var charge = Clamp01(face.Charge);
                if (charge > 0)
                    using (context.PushClip(new Rect(0, 0, body.Width * charge, body.Height)))
                    {
                        context.DrawRectangle(face.Accuracy ? AccuracyFill : PowerFill, null, glass);
                        context.DrawRectangle(Shade, null, glass);
                    }
                if (face.Reckless)
                {
                    context.DrawLine(RecklessEdge, new Point(from, 0), new Point(from, body.Height));
                    context.DrawLine(RecklessEdge, new Point(to, 0), new Point(to, body.Height));
                }
            }
            DerethGroove.End(context, body.Width, body.Height, ticks: true);
        }

        // The set point: a green gem on a gold pin that stands through the groove.
        var pin = body.X + body.Width * set;
        context.DrawLine(Pin, new Point(pin, body.Y - 2.5), new Point(pin, body.Bottom + 2.5));
        context.DrawGeometry(DerethGilt.GoldVertical, DerethGilt.Outline,
            Polygon(new Point(pin, body.Y - 6), new Point(pin + 3.2, body.Y - 2.6), new Point(pin, body.Y + 0.8), new Point(pin - 3.2, body.Y - 2.6)));
        context.DrawEllipse(GemOn, null, new Point(pin, body.Y - 2.6), 1.5, 1.5);
    }

    private static void DrawPlaque(DrawingContext context, Rect rect, int index, bool lit)
    {
        var middle = rect.Center.Y;
        context.DrawGeometry(lit ? PlateLit : DerethGilt.Inlay, lit ? LitEdge : UnlitEdge, DerethGilt.Chamfered(rect, 2.5));
        Text(context, PlaqueNames[index], 8.6, lit ? LitText : DerethPalette.MutedBrush, rect.Center.X, middle, centred: true);
        if (!lit) return;
        // The lit height's arrows, on each side, as retail's yellow arrows.
        context.DrawGeometry(DerethGilt.GoldVertical, null,
            Polygon(new Point(rect.X - 6, middle - 3.2), new Point(rect.X - 1.6, middle), new Point(rect.X - 6, middle + 3.2)));
        context.DrawGeometry(DerethGilt.GoldVertical, null,
            Polygon(new Point(rect.Right + 6, middle - 3.2), new Point(rect.Right + 1.6, middle), new Point(rect.Right + 6, middle + 3.2)));
    }

    private static void DrawToggle(DrawingContext context, Point centre, int index, bool on)
    {
        context.DrawEllipse(DerethGilt.GoldRound, GemEdge, centre, GemRadius + 1, GemRadius + 1);
        context.DrawEllipse(on ? GemOn : GemOff, null, centre, GemRadius, GemRadius);
        Text(context, ToggleNames[index], 8.6, on ? DerethPalette.CreamBrush : DerethPalette.MutedBrush, centre.X + 7, centre.Y, centred: false);
    }

    /// <summary>
    /// Draws a line of Palatino with its capitals and digits centred on <paramref name="middle"/>, from <paramref name="x"/> or centred across it,
    /// as the vitals' values are centred in their plaques.
    /// </summary>
    private static void Text(DrawingContext context, string text, double size, IBrush brush, double x, double middle, bool centred)
    {
        var typeface = new Typeface(DerethPalette.Title);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
        var top = middle - formatted.Height / 2 + DerethPalette.InkShift(typeface, size);
        context.DrawText(formatted, new Point(centred ? x - formatted.Width / 2 : x, top));
    }

    private static StreamGeometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(points[0], true);
        for (var i = 1; i < points.Length; i++) g.LineTo(points[i]);
        g.EndFigure(true);
        return geometry;
    }

    private static LinearGradientBrush Linear(RelativePoint start, RelativePoint end, params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(color, offset));
        return brush;
    }

    /// <summary>A gem's radial gradient: its light sits upper left, and its stops run from the light out to the dark edge.</summary>
    private static RadialGradientBrush Radial(params (Color Color, double Offset)[] stops)
    {
        var centre = new RelativePoint(0.38, 0.34, RelativeUnit.Relative);
        var brush = new RadialGradientBrush
        {
            Center = centre, GradientOrigin = centre,
            RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative)
        };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(color, offset));
        return brush;
    }
}
