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
/// Where the attack bar's parts sit for a size in DIPs. At the design size, 610 by 90, it is retail's panel: the bar across the top with
/// Speed and Power inside its ends, the options in a row under it, the plaques stacked at the right edge and the bottom third empty. A taller
/// window draws the whole bar larger, text and controls with it, by <see cref="Scale"/>; the parts are placed in the unscaled
/// <see cref="Size"/>. Width beyond that lengthens the bar, carrying its right label and the plaques with the right edge; the option gems
/// stay at retail's places. Height beyond it, in a window too narrow to scale up further, thickens the bar and spreads the rows.
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
        Groove = new Rect(14, 14 + _tall * 0.15, Math.Max(40, 506 + _wide), Math.Min(22, 14 + _tall * 0.3));
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
    /// <summary>Where the Power or Accuracy label ends: inside the bar's right end, as retail's.</summary>
    public double RightLabelX => Groove.Right - 9;
    /// <summary>Where the plate's top lozenge, bottom rule and stud are centred: over the bar.</summary>
    public double OrnamentX => 267 + _wide / 2;

    /// <summary>A height plaque's rectangle, drawn and clicked: 0 High, 1 Medium, 2 Low, from the top.</summary>
    public Rect Plaque(int index)
    {
        // At the design size the plaques are 60 by 16 on retail's 19.5 DIP pitch; added height grows both.
        double pitch = 19.5 + _tall / 3, height = Math.Min(26, 16 + _tall * 0.2);
        var middle = 18.5 + pitch * index;
        return new Rect(Size.Width - 76, middle - height / 2, 60, height);
    }

    /// <summary>An option gem's centre: Repeat Attacks, Auto Target, Keep in View, from the left.</summary>
    public Point Toggle(int index) => new(index switch { 0 => 19, 1 => 125, _ => 213 }, 43 + _tall * 0.75);

    /// <summary>An option's click area: its gem and its label, each as wide as its 11 DIP Palatino label reaches.</summary>
    public Rect ToggleHit(int index) => new(Toggle(index).X - 6, Toggle(index).Y - 8, index switch { 0 => 90, 1 => 76, _ => 84 }, 16);
}

/// <summary>
/// The attack bar's art, laid out by <see cref="DerethAttackLayout"/> for its size: the Speed ↔ Power (or Accuracy) bar in the vitals' glass
/// groove between gold end caps with its labels inside its ends, its set point as a green gem on a gold pin, the charge filling from the left,
/// three height plaques with the lit one's arrows, and three option gems. With Recklessness trained, its range is tinted ember in the groove. It
/// draws only when its face or its size changes, so a still bar draws no new frame.
/// </summary>
public sealed class DerethAttackBarArt : Control
{
    /// <summary>The bar's design size in DIPs: the size it opens at and the smallest height it lays out for.</summary>
    public const int DesignWidth = 610, DesignHeight = 90;
    /// <summary>The narrowest the bar lays out for, where Keep in View still clears the plaques, and the largest size, twice retail's, in DIPs.</summary>
    public const int MinimumWidth = 400, MaximumWidth = 1220, MaximumHeight = 180;

    private const double GemRadius = 5;
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
        DrawGroove(context, layout.Groove, face);
        Text(context, "Speed", 11, DerethPalette.CreamBrush, layout.Groove.X + 9, middle, centred: false);
        Text(context, face.Accuracy ? "Accuracy" : "Power", 11, DerethPalette.CreamBrush, layout.RightLabelX, middle, centred: false, right: true);
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
        Text(context, PlaqueNames[index], 11, lit ? LitText : DerethPalette.MutedBrush, rect.Center.X, middle, centred: true);
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
        Text(context, ToggleNames[index], 11, on ? DerethPalette.CreamBrush : DerethPalette.MutedBrush, centre.X + 10, centre.Y, centred: false);
    }

    /// <summary>
    /// Draws a line of Palatino with its capitals and digits centred on <paramref name="middle"/>, from <paramref name="x"/> or centred across it,
    /// as the vitals' values are centred in their plaques.
    /// </summary>
    private static void Text(DrawingContext context, string text, double size, IBrush brush, double x, double middle, bool centred, bool right = false)
    {
        var typeface = new Typeface(DerethPalette.Title);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
        var top = middle - formatted.Height / 2 + DerethPalette.InkShift(typeface, size);
        context.DrawText(formatted, new Point(centred ? x - formatted.Width / 2 : right ? x - formatted.Width : x, top));
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
