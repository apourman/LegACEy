using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// The round scope of the Dereth radar: the ground turned under the dots, or indoors the floor plan of the place (the player's level solid,
/// the others faint, a peeked level solid in teal, stair marks on the solid level), the range rings, the dots (dots on another level as
/// arrows) and the marker for the player. The window sets what it shows; the scope draws it, and a hovered dot's label beside it.
/// </summary>
public sealed class DerethRadarScope : Control
{
    private static readonly Color Teal = DerethPalette.TealText;
    private static readonly Color Cream = DerethPalette.Cream;
    private static readonly Color Gold = DerethPalette.Gold;
    private static readonly Color Navy = Color.Parse("#18160F");
    private static readonly IBrush Darken = DerethPalette.Brush(Color.FromArgb(0x40, 22, 20, 12));
    private static readonly IBrush Disc = new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.Parse("#2A2A1A").WithAlpha(0xEB), 0), new GradientStop(Color.Parse("#0A0906").WithAlpha(0xF0), 1) }
    };
    private static readonly IBrush Vignette = new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.FromArgb(0, 8, 7, 4), 0.55), new GradientStop(Color.FromArgb(0x99, 8, 7, 4), 1) }
    };
    private static readonly IBrush Cross = DerethPalette.Brush(Teal.WithAlpha(0x1A));
    private static readonly IPen CrossPen = new Pen(Cross, 0.6);
    private static readonly IBrush RingBrush = DerethPalette.Brush(Gold.WithAlpha(0x2E));
    private static readonly IPen RingPen = new Pen(RingBrush, 0.8);
    private static readonly IPen OutlinePen = new Pen(DerethPalette.Brush(Color.FromArgb(0xC0, 0, 0, 0)), 0.6);
    private static readonly IBrush MarkerBrush = DerethPalette.Brush(Cream);
    private static readonly Geometry Marker = Geometry.Parse("M0,-4.5 L3.2,3.5 L0,2 L-3.2,3.5 Z");
    private static readonly IPen SelectedPen = new Pen(DerethPalette.TealBrush, 1);
    private static readonly IPen HoveredPen = new Pen(DerethPalette.Brush(Cream.WithAlpha(0xB3)), 1);
    private static readonly IBrush LabelFill = DerethPalette.Brush(Navy.WithAlpha(0xD9));
    private static readonly IPen LabelEdge = new Pen(DerethPalette.GoldBrush, 1);
    private static readonly Typeface LabelFace = new(DerethPalette.Title);
    // The plan: the other levels as navy and gold at a low alpha (a building's are darker, over its ground), the player's as slate with a
    // gold wall, over a dark halo.
    private static readonly IBrush PlanNavy = DerethPalette.Brush(Color.FromArgb(0x6B, 0x2A, 0x36, 0x40));
    private static readonly IBrush PlanNavyBuilding = DerethPalette.Brush(Color.FromArgb(0x8C, 0x0A, 0x12, 0x18));
    private static readonly IBrush PlanFaintGold = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x29));
    private static readonly IBrush PlanSlate = DerethPalette.Brush(Color.Parse("#5C7684").WithAlpha(0xD9));
    // A peeked level is solid in the palette's teal, a wash over the dark, so it reads as the player's floor does but is not.
    private static readonly IBrush PlanPeek = DerethPalette.Brush(DerethPalette.Teal.WithAlpha(0x59));
    private static readonly IBrush PlanWall = DerethPalette.Brush(DerethPalette.GoldLight);
    private static readonly IBrush PlanHalo = DerethPalette.Brush(Color.FromArgb(0x66, 0, 0, 0));
    // The arrows for dots on another level: a small triangle pointing up (the level above) or down, in the dot's colour.
    private static readonly Geometry ArrowUp = Geometry.Parse("M0,-2.6 L2.4,1.6 L-2.4,1.6 Z");
    private static readonly Geometry ArrowDown = Geometry.Parse("M0,2.6 L2.4,-1.6 L-2.4,-1.6 Z");
    private const double ArrowOpacity = 0.85;
    private static readonly IBrush StairInk = DerethPalette.CreamBrush;
    // A ping: a diamond with a ring that swells from it once a pulse; one past the rim is an arrow on the rim pointing out. It fades over the
    // last fifth of its life.
    private static readonly Geometry PingDiamond = Geometry.Parse("M0,-3.6 L3.6,0 L0,3.6 L-3.6,0 Z");
    private static readonly Geometry PingArrow = Geometry.Parse("M0,-4.5 L3.8,2.5 L-3.8,2.5 Z");
    private const double PingPulseSeconds = 1.2, PingLifeSeconds = 15, PingRimInset = 5;

    private IReadOnlyList<(IImage Image, Rect Metres)> _tiles = Array.Empty<(IImage Image, Rect Metres)>();
    private IReadOnlyList<(Geometry Floors, Geometry Walls, bool Solid, bool Peek)> _planLevels = Array.Empty<(Geometry Floors, Geometry Walls, bool Solid, bool Peek)>();
    private Point _planOrigin;
    private bool _planBuilding;
    private IReadOnlyList<(Point At, bool Up)> _stairMarks = Array.Empty<(Point At, bool Up)>();
    private FormattedText[]? _stairGlyphs;
    private PlanPens? _pens;
    // ArrowDir is +1 for a dot on the level above, -1 for one below, and 0 for a dot on the player's level.
    private IReadOnlyList<(Point At, Color Colour, double Opacity, int ArrowDir, bool Selected, bool Hovered)> _dots =
        Array.Empty<(Point At, Color Colour, double Opacity, int ArrowDir, bool Selected, bool Hovered)>();
    // The dots' brushes by colour and alpha, made once each: a dot's alpha takes only a few values.
    private readonly Dictionary<(Color Colour, byte Alpha), IBrush> _dotBrushes = new();
    private (string Text, Point At)? _label;
    private IReadOnlyList<(Point At, Color Colour, double Age, double? Edge)> _pings = Array.Empty<(Point At, Color Colour, double Age, double? Edge)>();
    private double _turn, _arrow, _pixelsPerMetre = 1;
    private bool _rings;

    /// <summary>The clockwise turn of the world on screen, in degrees.</summary>
    public double Turn { get => _turn; set { if (_turn == value) return; _turn = value; InvalidateVisual(); } }

    /// <summary>DIPs per metre of range.</summary>
    public double PixelsPerMetre { get => _pixelsPerMetre; set { if (_pixelsPerMetre == value) return; _pixelsPerMetre = value; InvalidateVisual(); } }

    /// <summary>Whether the range rings show.</summary>
    public bool Rings { get => _rings; set { if (_rings == value) return; _rings = value; InvalidateVisual(); } }

    /// <summary>The marker's angle in degrees: 0 points up, the way the scope is turned to face.</summary>
    public double Arrow { get => _arrow; set { if (_arrow == value) return; _arrow = value; InvalidateVisual(); } }

    public IReadOnlyList<(IImage Image, Rect Metres)> Tiles
    {
        get => _tiles;
        set { if (ReferenceEquals(_tiles, value)) return; _tiles = value; InvalidateVisual(); }
    }

    /// <summary>
    /// The indoor plan's levels in drawing order, each as its floor and wall paths in landblock metres; whether it is solid (the player's own
    /// level, or a peeked one), and whether that solid one is a peek, drawn teal. Empty outdoors.
    /// </summary>
    public IReadOnlyList<(Geometry Floors, Geometry Walls, bool Solid, bool Peek)> PlanLevels
    {
        get => _planLevels;
        set { if (ReferenceEquals(_planLevels, value)) return; _planLevels = value; InvalidateVisual(); }
    }

    /// <summary>The player's position in landblock metres: the plan is drawn from it, at the centre.</summary>
    public Point PlanOrigin { get => _planOrigin; set { if (_planOrigin == value) return; _planOrigin = value; InvalidateVisual(); } }

    /// <summary>Whether the plan is a building's, drawn over its town ground: its other levels are then darker.</summary>
    public bool PlanBuilding { get => _planBuilding; set { if (_planBuilding == value) return; _planBuilding = value; InvalidateVisual(); } }

    /// <summary>The stair marks in DIPs from the scope's top-left, and whether each leads up. Drawn upright whatever the turn.</summary>
    public IReadOnlyList<(Point At, bool Up)> StairMarks
    {
        get => _stairMarks;
        set { if (ReferenceEquals(_stairMarks, value)) return; _stairMarks = value; InvalidateVisual(); }
    }

    public IReadOnlyList<(Point At, Color Colour, double Opacity, int ArrowDir, bool Selected, bool Hovered)> Dots
    {
        get => _dots;
        set { if (ReferenceEquals(_dots, value)) return; _dots = value; InvalidateVisual(); }
    }

    /// <summary>
    /// The pings: each one's point in DIPs from the scope's top-left, its colour, how far through its life it is (0 to 1), and for one past the
    /// rim the screen angle it lies at, clockwise from up.
    /// </summary>
    public IReadOnlyList<(Point At, Color Colour, double Age, double? Edge)> Pings
    {
        get => _pings;
        set { if (ReferenceEquals(_pings, value)) return; _pings = value; InvalidateVisual(); }
    }

    /// <summary>The hovered dot's label and the dot's point, so the label sits beside it; null for none.</summary>
    public (string Text, Point At)? Label
    {
        get => _label;
        set { if (_label == value) return; _label = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var radius = size / 2;
        var centre = new Point(radius, radius);
        using (context.PushGeometryClip(new EllipseGeometry(new Rect(0, 0, size, size))))
        {
            context.DrawEllipse(Disc, null, centre, radius, radius);
            // The world turned and scaled so the player is at the centre.
            var world = Matrix.CreateScale(PixelsPerMetre, PixelsPerMetre) * Matrix.CreateRotation(Turn * Math.PI / 180)
                * Matrix.CreateTranslation(centre);
            // The ground: indoors it is a building's, at half brightness under the plan, else darkened a little to sit under the dots.
            var planned = _planLevels.Count > 0;
            using (context.PushOpacity(planned ? 0.5 : 1))
            using (context.PushTransform(world))
                foreach (var tile in _tiles)
                    context.DrawImage(tile.Image, new Rect(tile.Image.Size), tile.Metres);
            if (_tiles.Count > 0 && !planned) context.DrawEllipse(Darken, null, centre, radius, radius);
            if (planned) DrawPlan(context, world);
            DrawStairs(context);
            context.DrawEllipse(Vignette, null, centre, radius, radius);
            DrawCrosshair(context, centre, radius);
            if (Rings) DrawRings(context, centre, radius);
            foreach (var dot in _dots) DrawDot(context, dot);
            foreach (var ping in _pings) DrawPing(context, ping);
            using (context.PushTransform(Matrix.CreateRotation(Arrow * Math.PI / 180) * Matrix.CreateTranslation(centre)))
                context.DrawGeometry(MarkerBrush, null, Marker);
        }
        if (_label is { } label) DrawLabel(context, size, label);
    }

    // The plan's paths are in landblock metres, y north; the scope's are y south, so the plan is flipped, moved so the player is at the
    // origin, and then turned and scaled with the world. Pen widths are in the same metres, so a pen is its DIPs over the scale.
    private void DrawPlan(DrawingContext context, Matrix world)
    {
        // The pens are made again only when the scale changes: their widths are in the plan's metres.
        if (_pens == null || _pens.Scale != PixelsPerMetre) _pens = new PlanPens(PixelsPerMetre);
        var pens = _pens;
        using (context.PushTransform(new Matrix(1, 0, 0, -1, 0, 0) * Matrix.CreateTranslation(-_planOrigin.X, _planOrigin.Y) * world))
            foreach (var (floors, walls, solid, peek) in _planLevels)
            {
                if (solid)
                {
                    context.DrawGeometry(null, pens.Halo, floors);
                    context.DrawGeometry(peek ? PlanPeek : PlanSlate, null, floors);
                    context.DrawGeometry(null, pens.Wall, walls);
                }
                else
                {
                    context.DrawGeometry(PlanBuilding ? PlanNavyBuilding : PlanNavy, null, floors);
                    context.DrawGeometry(null, pens.FaintWall, walls);
                }
            }
    }

    // The stair marks: a ▲ or ▼ at each door, upright, so the glyphs are made once and only their places change with the turn.
    private void DrawStairs(DrawingContext context)
    {
        if (_stairMarks.Count == 0) return;
        _stairGlyphs ??= new[] { StairGlyph("▼"), StairGlyph("▲") };
        foreach (var (at, up) in _stairMarks)
        {
            var glyph = _stairGlyphs[up ? 1 : 0];
            context.DrawText(glyph, new Point(at.X - glyph.Width / 2, at.Y - glyph.Height / 2));
        }
    }

    private static FormattedText StairGlyph(string text) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(DerethPalette.Title), 8, StairInk);

    // The plan's pens at a scale: the halo's width is 6 DIPs, the player's walls 1.1 and the other levels' 0.8, each over the scale.
    private sealed record PlanPens(double Scale)
    {
        public Pen Halo { get; } = new(PlanHalo, 6 / Scale) { LineJoin = PenLineJoin.Round };
        public Pen Wall { get; } = new(PlanWall, 1.1 / Scale) { LineCap = PenLineCap.Round };
        public Pen FaintWall { get; } = new(PlanFaintGold, 0.8 / Scale) { LineCap = PenLineCap.Round };
    }

    private static void DrawCrosshair(DrawingContext context, Point centre, double radius)
    {
        context.DrawLine(CrossPen, new Point(centre.X, 0), new Point(centre.X, radius * 2));
        context.DrawLine(CrossPen, new Point(0, centre.Y), new Point(radius * 2, centre.Y));
    }

    private static void DrawRings(DrawingContext context, Point centre, double radius)
    {
        foreach (var fraction in new[] { 1.0 / 3, 2.0 / 3, 1 })
            context.DrawEllipse(null, RingPen, centre, radius * fraction, radius * fraction);
    }

    private void DrawDot(DrawingContext context, (Point At, Color Colour, double Opacity, int ArrowDir, bool Selected, bool Hovered) dot)
    {
        // An arrow is drawn at its own alpha, a dot at its own.
        var alpha = (byte)Math.Round((dot.ArrowDir == 0 ? dot.Opacity : ArrowOpacity) * 255);
        var key = (dot.Colour, alpha);
        if (!_dotBrushes.TryGetValue(key, out var colour)) _dotBrushes[key] = colour = DerethPalette.Brush(dot.Colour.WithAlpha(alpha));
        if (dot.ArrowDir == 0)
        {
            context.DrawEllipse(colour, OutlinePen, dot.At, 1.9, 1.9);
        }
        else
        {
            using (context.PushTransform(Matrix.CreateTranslation(dot.At.X, dot.At.Y)))
                context.DrawGeometry(colour, OutlinePen, dot.ArrowDir > 0 ? ArrowUp : ArrowDown);
        }
        if (dot.Selected) context.DrawEllipse(null, SelectedPen, dot.At, 4.5, 4.5);
        if (dot.Hovered) context.DrawEllipse(null, HoveredPen, dot.At, 4.5, 4.5);
    }

    private static void DrawPing(DrawingContext context, (Point At, Color Colour, double Age, double? Edge) ping)
    {
        var fade = Math.Min(1, (1 - ping.Age) * 5);
        var fill = new SolidColorBrush(ping.Colour, fade);
        if (ping.Edge is { } angle)
        {
            var radians = angle * Math.PI / 180;
            var at = new Point(ping.At.X - Math.Sin(radians) * PingRimInset, ping.At.Y + Math.Cos(radians) * PingRimInset);
            using (context.PushTransform(Matrix.CreateRotation(radians) * Matrix.CreateTranslation(at.X, at.Y)))
                context.DrawGeometry(fill, OutlinePen, PingArrow);
            return;
        }
        var pulse = ping.Age * PingLifeSeconds / PingPulseSeconds % 1;
        context.DrawEllipse(null, new Pen(new SolidColorBrush(ping.Colour, fade * (1 - pulse)), 1.2), ping.At, 3 + 9 * pulse, 3 + 9 * pulse);
        using (context.PushTransform(Matrix.CreateTranslation(ping.At.X, ping.At.Y)))
            context.DrawGeometry(fill, OutlinePen, PingDiamond);
    }

    // The label sits to the lower right of the hovered dot, and moves in where the scope's edge would cut it.
    private static void DrawLabel(DrawingContext context, double size, (string Text, Point At) label)
    {
        var text = new FormattedText(label.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 9.5, DerethPalette.CreamBrush);
        var box = new Rect(0, 0, text.Width + 10, text.Height + 3);
        var x = Math.Max(2, Math.Min(label.At.X + 6, size - box.Width - 2));
        var y = Math.Max(2, Math.Min(label.At.Y + 6, size - box.Height - 2));
        var place = new Rect(x, y, box.Width, box.Height);
        context.DrawRectangle(LabelFill, LabelEdge, new RoundedRect(place, 3));
        context.DrawText(text, new Point(x + 5, y + 1.5));
    }
}

/// <summary>
/// The bezel round the scope: the gold rings, and the compass letters on the rim, turned with the world (north in green, the minor points
/// muted). It takes no pointer, so the scope underneath it does.
/// </summary>
public sealed class DerethRadarBezel : Control
{
    // The rings sit outside the scope's edge, as the bezel's box shadows do in the mockup: each as (distance from the edge, pen).
    private static readonly (double Outset, IPen Pen)[] Rings =
    {
        (1, new Pen(DerethPalette.Brush(Color.Parse("#8A6326")), 1.5)),
        (3.5, new Pen(DerethPalette.Brush(Color.Parse("#0E0B06")), 1.5)),
        (5, new Pen(DerethPalette.Brush(DerethPalette.Gold), 1)),
        (6.5, new Pen(DerethPalette.Brush(Color.Parse("#2B2112")), 1.5)),
    };
    private const double ScopeRadius = 59;
    // The letters sit inside the rim, as retail's do.
    private const double LetterRadius = ScopeRadius - 6;
    private static readonly (string Text, double Bearing)[] Letters = { ("N", 0), ("E", 90), ("S", 180), ("W", 270) };

    private static readonly Color GoldLight = DerethPalette.GoldLight;
    private double _turn;
    private FormattedText[]? _letters;
    private FormattedText[]? _minors;

    /// <summary>The clockwise turn of the world on screen, in degrees. The letters turn with it.</summary>
    public double Turn { get => _turn; set { if (_turn == value) return; _turn = value; InvalidateVisual(); } }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        foreach (var (outset, pen) in Rings)
            context.DrawEllipse(null, pen, centre, ScopeRadius + outset, ScopeRadius + outset);
        _letters ??= Letters.Select(letter => Face(letter.Text, letter.Text == "N" ? DerethPalette.TealBrush : DerethPalette.Brush(GoldLight), 10)).ToArray();
        _minors ??= new[] { Face("·", DerethPalette.MutedBrush, 6), Face("·", DerethPalette.MutedBrush, 6), Face("·", DerethPalette.MutedBrush, 6), Face("·", DerethPalette.MutedBrush, 6) };
        for (var i = 0; i < Letters.Length; i++)
            Place(context, _letters[i], centre, Letters[i].Bearing, DerethPalette.InkShift(new Typeface(DerethPalette.Title), 10));
        for (var i = 0; i < _minors.Length; i++)
            Place(context, _minors[i], centre, 45 + 90 * i);
    }

    // A letter at its bearing: its screen angle is the bearing turned by the world's turn, clockwise from up.
    // The shift centres a letter's capital on its point (see DerethPalette.InkShift); the dots need none.
    private void Place(DrawingContext context, FormattedText text, Point centre, double bearing, double shift = 0)
    {
        var angle = (bearing + Turn) * Math.PI / 180;
        var at = new Point(centre.X + Math.Sin(angle) * LetterRadius, centre.Y - Math.Cos(angle) * LetterRadius);
        context.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2 + shift));
    }

    private static FormattedText Face(string text, IBrush brush, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(DerethPalette.Title), size, brush);
}

/// <summary>
/// The heading tape above the scope: ticks every 5 degrees across 120 degrees of view, the compass points on the 45s (north in green),
/// faded at its ends, and the heading in degrees at its centre.
/// </summary>
public sealed class DerethHeadingTape : Control
{
    private static readonly Color GoldLight = DerethPalette.GoldLight;
    private const double Span = 120;
    // Ticks this close to the centre stay clear of the heading readout.
    private const double ClearOfCentre = 11;
    private static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private static readonly IBrush NorthBrush = DerethPalette.Brush(DerethPalette.TealText);
    private static readonly IBrush PointBrush = DerethPalette.Brush(GoldLight);
    private static readonly IPen NorthPen = new Pen(NorthBrush, 0.8);
    private static readonly IPen PointPen = new Pen(PointBrush, 0.8);
    private static readonly IPen TickPen = new Pen(DerethPalette.Brush(GoldLight.WithAlpha(0x80)), 0.6);
    private double _heading;
    private FormattedText[]? _labels;
    private FormattedText? _readout;
    private int _readoutDegrees;

    /// <summary>The heading in degrees: 0 north, 90 east.</summary>
    public double Heading { get => _heading; set { if (_heading == value) return; _heading = value; InvalidateVisual(); } }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        var middle = width / 2;
        // The compass labels are the same at every heading, so they are made once.
        _labels ??= Points.Select((point, i) => new FormattedText(point, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(DerethPalette.Title), point.Length > 1 ? 6 : 8, i == 0 ? NorthBrush : PointBrush)).ToArray();
        var first = Math.Floor((Heading - Span / 2) / 5) * 5;
        for (var degrees = first; degrees <= Heading + Span / 2; degrees += 5)
        {
            var x = middle + (degrees - Heading) / Span * width;
            var edge = Math.Max(0, (1 - Math.Abs(x - middle) / middle) * 1.3);
            var near = Math.Abs(x - middle) < ClearOfCentre;
            var turned = ((degrees % 360) + 360) % 360;
            if (turned % 45 == 0)
            {
                var index = (int)(turned / 45);
                var label = _labels[index];
                using (context.PushOpacity(edge))
                {
                    context.DrawText(label, new Point(x - label.Width / 2, 1 + DerethPalette.InkShift(new Typeface(DerethPalette.Title), Points[index].Length > 1 ? 6 : 8)));
                    if (!near) context.DrawLine(index == 0 ? NorthPen : PointPen, new Point(x, height - 5), new Point(x, height));
                }
            }
            else if (!near)
            {
                var tick = turned % 15 == 0 ? 4 : 2.5;
                using (context.PushOpacity(edge))
                    context.DrawLine(TickPen, new Point(x, height - tick), new Point(x, height));
            }
        }
        // The readout is made again only when the degrees it shows change.
        var degreesShown = (int)Math.Round(Heading) % 360;
        if (_readout == null || degreesShown != _readoutDegrees)
        {
            _readoutDegrees = degreesShown;
            _readout = new FormattedText(degreesShown.ToString("000", CultureInfo.InvariantCulture) + "°", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(DerethPalette.Title), 6.5, DerethPalette.CreamBrush);
        }
        context.DrawText(_readout, new Point(middle - _readout.Width / 2, 9 + DerethPalette.InkShift(new Typeface(DerethPalette.Title), 6.5)));
    }
}
