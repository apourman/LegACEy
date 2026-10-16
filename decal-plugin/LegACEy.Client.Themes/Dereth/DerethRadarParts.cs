using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// The round scope of the Dereth radar: the ground turned under the dots, the range rings, the dots and the marker for the player. The
/// window sets what it shows; the scope draws it, and a hovered dot's label beside it.
/// </summary>
public sealed class DerethRadarScope : Control
{
    private static readonly Color Teal = DerethPalette.TealText;
    private static readonly Color Cream = DerethPalette.Cream;
    private static readonly Color Gold = DerethPalette.Gold;
    private static readonly Color Navy = Color.Parse("#081016");
    private static readonly IBrush Darken = DerethPalette.Brush(Color.FromArgb(0x40, 10, 18, 25));
    private static readonly IBrush Disc = new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.Parse("#122C2C").WithAlpha(0xEB), 0), new GradientStop(Color.Parse("#04090D").WithAlpha(0xF0), 1) }
    };
    private static readonly IBrush Vignette = new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.FromArgb(0, 4, 9, 13), 0.55), new GradientStop(Color.FromArgb(0x99, 4, 9, 13), 1) }
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

    private IReadOnlyList<(IImage Image, Rect Metres)> _tiles = Array.Empty<(IImage Image, Rect Metres)>();
    private IReadOnlyList<(Point At, Color Colour, double Opacity, bool Selected, bool Hovered)> _dots = Array.Empty<(Point At, Color Colour, double Opacity, bool Selected, bool Hovered)>();
    private string? _label;
    private double _turn, _arrow, _pixelsPerMetre = 1;
    private bool _rings;

    /// <summary>The clockwise turn of the world on screen, in degrees.</summary>
    public double Turn { get => _turn; set { _turn = value; InvalidateVisual(); } }

    /// <summary>DIPs per metre of range.</summary>
    public double PixelsPerMetre { get => _pixelsPerMetre; set { _pixelsPerMetre = value; InvalidateVisual(); } }

    /// <summary>Whether the range rings show.</summary>
    public bool Rings { get => _rings; set { _rings = value; InvalidateVisual(); } }

    /// <summary>The marker's angle in degrees: 0 points up, the way the scope is turned to face.</summary>
    public double Arrow { get => _arrow; set { _arrow = value; InvalidateVisual(); } }

    public IReadOnlyList<(IImage Image, Rect Metres)> Tiles
    {
        get => _tiles;
        set { _tiles = value; InvalidateVisual(); }
    }

    public IReadOnlyList<(Point At, Color Colour, double Opacity, bool Selected, bool Hovered)> Dots
    {
        get => _dots;
        set { _dots = value; InvalidateVisual(); }
    }

    /// <summary>The hovered dot's label, drawn beside the hovered dot; null for none.</summary>
    public string? Label
    {
        get => _label;
        set { _label = value; InvalidateVisual(); }
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
            // The ground: the world turned and scaled so the player is at the centre, then darkened a little to sit under the dots.
            using (context.PushTransform(Matrix.CreateScale(PixelsPerMetre, PixelsPerMetre) * Matrix.CreateRotation(Turn * Math.PI / 180) * Matrix.CreateTranslation(centre)))
                foreach (var tile in _tiles)
                    context.DrawImage(tile.Image, new Rect(tile.Image.Size), tile.Metres);
            if (_tiles.Count > 0) context.DrawEllipse(Darken, null, centre, radius, radius);
            context.DrawEllipse(Vignette, null, centre, radius, radius);
            DrawCrosshair(context, centre, radius);
            if (Rings) DrawRings(context, centre, radius);
            foreach (var dot in _dots) DrawDot(context, dot);
            using (context.PushTransform(Matrix.CreateRotation(Arrow * Math.PI / 180) * Matrix.CreateTranslation(centre)))
                context.DrawGeometry(MarkerBrush, null, Marker);
        }
        if (_label != null) DrawLabel(context, size);
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

    private static void DrawDot(DrawingContext context, (Point At, Color Colour, double Opacity, bool Selected, bool Hovered) dot)
    {
        var colour = DerethPalette.Brush(dot.Colour.WithAlpha((byte)Math.Round(dot.Opacity * 255)));
        context.DrawEllipse(colour, OutlinePen, dot.At, 1.9, 1.9);
        if (dot.Selected) context.DrawEllipse(null, SelectedPen, dot.At, 4.5, 4.5);
        if (dot.Hovered) context.DrawEllipse(null, HoveredPen, dot.At, 4.5, 4.5);
    }

    // The label sits to the lower right of the hovered dot, and moves in where the scope's edge would cut it.
    private void DrawLabel(DrawingContext context, double size)
    {
        (Point At, Color Colour, double Opacity, bool Selected, bool Hovered)? hovered = null;
        foreach (var dot in _dots)
            if (dot.Hovered) hovered = dot;
        if (hovered is not { } at) return;
        var text = new FormattedText(_label!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 9.5, DerethPalette.CreamBrush);
        var box = new Rect(0, 0, text.Width + 10, text.Height + 3);
        var x = Math.Max(2, Math.Min(at.At.X + 6, size - box.Width - 2));
        var y = Math.Max(2, Math.Min(at.At.Y + 6, size - box.Height - 2));
        var place = new Rect(x, y, box.Width, box.Height);
        context.DrawRectangle(LabelFill, LabelEdge, new RoundedRect(place, 3));
        context.DrawText(text, new Point(x + 5, y + 1.5));
    }
}

/// <summary>
/// The bezel round the scope: the gold rings, and the compass letters on the rim, turned with the world (north in teal, the minor points
/// muted). It takes no pointer, so the scope underneath it does.
/// </summary>
public sealed class DerethRadarBezel : Control
{
    // The rings sit outside the scope's edge, as the bezel's box shadows do in the mockup: each as (distance from the edge, colour, width).
    private static readonly (double Outset, Color Colour, double Width)[] Rings =
    {
        (1, Color.Parse("#7A5E30"), 1.5), (3.5, Color.Parse("#0A1218"), 1.5), (5, DerethPalette.Gold, 1), (6.5, Color.Parse("#2B2112"), 1.5)
    };
    private const double ScopeRadius = 59;
    // The letters sit inside the rim, as retail's do.
    private const double LetterRadius = ScopeRadius - 6;
    private static readonly (string Text, double Bearing)[] Letters = { ("N", 0), ("E", 90), ("S", 180), ("W", 270) };

    private static readonly Color GoldLight = Color.Parse("#D9B66A");
    private double _turn;
    private FormattedText[]? _letters;
    private FormattedText[]? _minors;

    /// <summary>The clockwise turn of the world on screen, in degrees. The letters turn with it.</summary>
    public double Turn { get => _turn; set { _turn = value; InvalidateVisual(); } }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        foreach (var (outset, colour, width) in Rings)
            context.DrawEllipse(null, new Pen(DerethPalette.Brush(colour), width), centre, ScopeRadius + outset, ScopeRadius + outset);
        _letters ??= Letters.Select(letter => Face(letter.Text, letter.Text == "N" ? DerethPalette.TealBrush : DerethPalette.Brush(GoldLight), 10)).ToArray();
        _minors ??= new[] { Face("·", DerethPalette.MutedBrush, 6), Face("·", DerethPalette.MutedBrush, 6), Face("·", DerethPalette.MutedBrush, 6), Face("·", DerethPalette.MutedBrush, 6) };
        for (var i = 0; i < Letters.Length; i++)
            Place(context, _letters[i], centre, Letters[i].Bearing);
        for (var i = 0; i < _minors.Length; i++)
            Place(context, _minors[i], centre, 45 + 90 * i);
    }

    // A letter at its bearing: its screen angle is the bearing turned by the world's turn, clockwise from up.
    private void Place(DrawingContext context, FormattedText text, Point centre, double bearing)
    {
        var angle = (bearing + Turn) * Math.PI / 180;
        var at = new Point(centre.X + Math.Sin(angle) * LetterRadius, centre.Y - Math.Cos(angle) * LetterRadius);
        context.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
    }

    private static FormattedText Face(string text, IBrush brush, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(DerethPalette.Title), size, brush);
}

/// <summary>
/// The heading tape above the scope: ticks every 5 degrees across 120 degrees of view, the compass points on the 45s (north in teal),
/// faded at its ends, and the heading in degrees at its centre.
/// </summary>
public sealed class DerethHeadingTape : Control
{
    private static readonly Color GoldLight = Color.Parse("#D9B66A");
    private const double Span = 120;
    // Ticks this close to the centre stay clear of the heading readout.
    private const double ClearOfCentre = 11;
    private static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private double _heading;

    /// <summary>The heading in degrees: 0 north, 90 east.</summary>
    public double Heading { get => _heading; set { _heading = value; InvalidateVisual(); } }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        var middle = width / 2;
        var first = Math.Floor((Heading - Span / 2) / 5) * 5;
        for (var degrees = first; degrees <= Heading + Span / 2; degrees += 5)
        {
            var x = middle + (degrees - Heading) / Span * width;
            var edge = Math.Max(0, (1 - Math.Abs(x - middle) / middle) * 1.3);
            var near = Math.Abs(x - middle) < ClearOfCentre;
            var turned = ((degrees % 360) + 360) % 360;
            if (turned % 45 == 0)
            {
                var point = Points[(int)(turned / 45)];
                var brush = DerethPalette.Brush(point == "N" ? DerethPalette.TealText : GoldLight);
                var label = new FormattedText(point, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(DerethPalette.Title),
                    point.Length > 1 ? 6 : 8, brush);
                using (context.PushOpacity(edge))
                {
                    context.DrawText(label, new Point(x - label.Width / 2, 1));
                    if (!near) context.DrawLine(new Pen(brush, 0.8), new Point(x, height - 5), new Point(x, height));
                }
            }
            else if (!near)
            {
                var tick = turned % 15 == 0 ? 4 : 2.5;
                using (context.PushOpacity(edge))
                    context.DrawLine(new Pen(DerethPalette.Brush(GoldLight.WithAlpha(0x80)), 0.6), new Point(x, height - tick), new Point(x, height));
            }
        }
        var readout = new FormattedText(((int)Math.Round(Heading) % 360).ToString("000", CultureInfo.InvariantCulture) + "°", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(DerethPalette.Title), 6.5, DerethPalette.CreamBrush);
        context.DrawText(readout, new Point(middle - readout.Width / 2, 9));
    }
}
