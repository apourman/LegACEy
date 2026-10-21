using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>What the attack bar shows: the bar's Accuracy or Power label and set point, the charge on it, the lit height plaque and the three options.</summary>
/// <param name="LitPlaque">The lit height: 0 High, 1 Medium, 2 Low.</param>
public readonly record struct DerethAttackFace(bool Accuracy, double SetPoint, double Charge, int LitPlaque, bool RepeatAttacks, bool AutoTarget, bool KeepInView);

/// <summary>
/// The attack bar's art, drawn at the mockup's coordinates in DIPs (320 by 58): the Speed ↔ Power (or Accuracy) bar in the vitals' glass groove
/// between gold end caps, its set point as a green gem on a gold pin, the charge filling from the left, three height plaques behind a gold
/// divider with the lit one's arrows, and three option gems. It draws only when its face changes, so a still bar draws no new frame.
/// </summary>
public sealed class DerethAttackBarArt : Control
{
    /// <summary>The bar's design size in DIPs.</summary>
    public const double DesignWidth = 320, DesignHeight = 58;
    /// <summary>Where the plate's top lozenge, bottom rule and stud are centred: over the bar, as the mockup places them.</summary>
    public const double PlateOrnamentX = 122;
    /// <summary>The groove: the bar's glass, whose middle line is the bar's centre.</summary>
    public static readonly Rect GrooveRect = new(46, 14, 168, 10);
    /// <summary>A height plaque's rectangle: 0 High, 1 Medium, 2 Low, from the top.</summary>
    public static Rect PlaqueRect(int index) => new(274, 9 + 14 * index, 36, 12);
    /// <summary>An option gem's centre: Repeat Attacks, Auto Target, Keep in View, from the left.</summary>
    public static Point ToggleCentre(int index) => new(index switch { 0 => 16, 1 => 90, _ => 164 }, 42);

    private const double GemRadius = 3.4;
    private static readonly string[] PlaqueNames = { "High", "Medium", "Low" };
    private static readonly string[] ToggleNames = { "Repeat Attacks", "Auto Target", "Keep in View" };
    // Ember sits between the stamina gold and the health crimson, the mockup's power fill; accuracy runs gold to the theme's green.
    private static readonly Color Ember = Color.Parse("#D2642A");
    private static readonly IBrush PowerFill = Linear(new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative),
        (DerethVitalsPalette.StaminaMid, 0), (Ember, 0.55), (DerethVitalsPalette.HealthMid, 1));
    private static readonly IBrush AccuracyFill = Linear(new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative),
        (DerethVitalsPalette.StaminaMid, 0), (DerethPalette.Raised, 0.55), (DerethPalette.Teal, 1));
    private static readonly IBrush Shade = Linear(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative),
        (Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.35), (Color.FromArgb(0x73, 0, 0, 0), 1));
    private static readonly IBrush Zone = DerethPalette.Brush(DerethPalette.Cream.WithAlpha(0x12));
    private static readonly IBrush PlateLit = Linear(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative),
        (Color.Parse("#F3DDA2"), 0), (DerethPalette.Gold, 0.5), (Color.Parse("#8A6A34"), 1));
    private static readonly IBrush LitText = DerethPalette.Brush(Color.Parse("#1A1008"));
    private static readonly IBrush GemOn = Radial((Color.Parse("#E6FFEC"), 0), (DerethPalette.TealText, 0.25), (Color.Parse("#1F7A35"), 0.65), (Color.Parse("#06240E"), 1));
    private static readonly IBrush GemOff = Radial((Color.Parse("#8A8070"), 0), (Color.Parse("#2E2A1E"), 0.3), (Color.Parse("#0A0805"), 1));
    private static readonly IPen Pin = new Pen(DerethPalette.GoldBrush, 1.6);
    private static readonly IPen Divider = new Pen(DerethPalette.GoldDarkBrush, 0.8);
    private static readonly IPen DividerShadow = new Pen(DerethPalette.Brush(Color.FromArgb(0x80, 0, 0, 0)), 0.6);
    private static readonly IPen LitEdge = new Pen(DerethPalette.Brush(Color.Parse("#2A1C0C")), 0.9);
    private static readonly IPen UnlitEdge = new Pen(DerethPalette.GoldDarkBrush, 0.9);
    private static readonly IPen GemEdge = new Pen(DerethPalette.Brush(Color.Parse("#2A1C0C")), 0.4);

    private DerethAttackFace _face = new(false, 0, 0, 0, false, false, false);

    public DerethAttackBarArt()
    {
        Width = DesignWidth;
        Height = DesignHeight;
        IsHitTestVisible = false;
    }

    /// <summary>The face as last shown.</summary>
    public DerethAttackFace Face => _face;

    /// <summary>Shows <paramref name="face"/>, redrawing only when it differs from the face shown.</summary>
    public void Show(DerethAttackFace face)
    {
        if (face == _face) return;
        _face = face;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var face = _face;
        Text(context, "Speed", 9, DerethPalette.MutedBrush, 12, 22, centred: false);
        Text(context, face.Accuracy ? "Accuracy" : "Power", 9, DerethPalette.MutedBrush, 222, 22, centred: false);
        DrawGroove(context, face);
        context.DrawLine(Divider, new Point(262, 9), new Point(262, 49));
        context.DrawLine(DividerShadow, new Point(263, 9), new Point(263, 49));
        for (var i = 0; i < PlaqueNames.Length; i++) DrawPlaque(context, i, i == face.LitPlaque);
        for (var i = 0; i < ToggleNames.Length; i++) DrawToggle(context, i, i switch { 0 => face.RepeatAttacks, 1 => face.AutoTarget, _ => face.KeepInView });
    }

    private static void DrawGroove(DrawingContext context, DerethAttackFace face)
    {
        var body = GrooveRect;
        var set = Clamp01(face.SetPoint);
        context.DrawRectangle(DerethPalette.GrooveBrush, DerethVitalBar.GrooveEdge, new RoundedRect(body.Inflate(0.7), 5.7));
        using (context.PushClip(new RoundedRect(body, 5)))
        {
            context.DrawRectangle(DerethVitalBar.Groove, null, body);
            // The part up to the set point is washed pale, so where the charge should stop reads at a glance.
            context.DrawRectangle(Zone, null, new Rect(body.X, body.Y, body.Width * set, body.Height));
            var charge = Clamp01(face.Charge);
            if (charge > 0)
                using (context.PushClip(new Rect(body.X, body.Y, body.Width * charge, body.Height)))
                {
                    context.DrawRectangle(face.Accuracy ? AccuracyFill : PowerFill, null, body);
                    context.DrawRectangle(Shade, null, body);
                }
            context.DrawRectangle(DerethVitalBar.Highlight, null, new RoundedRect(new Rect(body.X + 3, body.Y + 1, body.Width - 6, body.Height * 0.26), body.Height * 0.13));
        }
        using (context.PushTransform(Matrix.CreateTranslation(body.X, body.Y)))
        {
            DerethVitalBar.DrawCap(context, -1.6, -1, body.Height);
            DerethVitalBar.DrawCap(context, body.Width + 1.6, 1, body.Height);
            foreach (var q in new[] { 0.25, 0.5, 0.75 })
                context.DrawLine(DerethVitalBar.TickPen, new Point(body.Width * q, body.Height + 2), new Point(body.Width * q, body.Height + 4));
        }

        // The set point: a green gem on a gold pin that stands through the groove.
        var pin = body.X + body.Width * set;
        context.DrawLine(Pin, new Point(pin, body.Y - 2.5), new Point(pin, body.Bottom + 2.5));
        context.DrawGeometry(DerethGilt.GoldVertical, DerethGilt.Outline,
            Polygon(new Point(pin, body.Y - 6), new Point(pin + 3.2, body.Y - 2.6), new Point(pin, body.Y + 0.8), new Point(pin - 3.2, body.Y - 2.6)));
        context.DrawEllipse(GemOn, null, new Point(pin, body.Y - 2.6), 1.5, 1.5);
    }

    private static void DrawPlaque(DrawingContext context, int index, bool lit)
    {
        var rect = PlaqueRect(index);
        var middle = rect.Center.Y;
        context.DrawGeometry(lit ? PlateLit : DerethGilt.Inlay, lit ? LitEdge : UnlitEdge, DerethGilt.Chamfered(rect, 2.5));
        Text(context, PlaqueNames[index], 8.6, lit ? LitText : DerethPalette.MutedBrush, rect.Center.X, middle + 3.2, centred: true);
        if (!lit) return;
        // The lit height's arrows, on each side, as retail's yellow arrows.
        context.DrawGeometry(DerethGilt.GoldVertical, null,
            Polygon(new Point(rect.X - 6, middle - 3.2), new Point(rect.X - 1.6, middle), new Point(rect.X - 6, middle + 3.2)));
        context.DrawGeometry(DerethGilt.GoldVertical, null,
            Polygon(new Point(rect.Right + 6, middle - 3.2), new Point(rect.Right + 1.6, middle), new Point(rect.Right + 6, middle + 3.2)));
    }

    private static void DrawToggle(DrawingContext context, int index, bool on)
    {
        var centre = ToggleCentre(index);
        context.DrawEllipse(DerethGilt.GoldRound, GemEdge, centre, GemRadius + 1, GemRadius + 1);
        context.DrawEllipse(on ? GemOn : GemOff, null, centre, GemRadius, GemRadius);
        Text(context, ToggleNames[index], 8.6, on ? DerethPalette.CreamBrush : DerethPalette.MutedBrush, centre.X + 7, 45, centred: false);
    }

    /// <summary>Draws a line of Palatino with its baseline at <paramref name="baseline"/>, from <paramref name="x"/> or centred on it.</summary>
    private static void Text(DrawingContext context, string text, double size, IBrush brush, double x, double baseline, bool centred)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(DerethPalette.Title), size, brush);
        context.DrawText(formatted, new Point(centred ? x - formatted.Width / 2 : x, baseline - formatted.Baseline));
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

    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

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
