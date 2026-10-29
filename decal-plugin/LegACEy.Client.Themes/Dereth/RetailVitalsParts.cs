using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// A retail vital bar's colours: the fill and the empty track as top-to-bottom gradients, one stop per row of retail's 16-pixel bar
/// textures (health fill 0x06007482 / track 0x0600747F, stamina 0x06007488 / 0x06007485, mana 0x0600748E / 0x0600748B). Drawn as
/// gradients, not the textures, so the bar stays sharp at any scale.
/// </summary>
public sealed class RetailVitalArt
{
    public static readonly RetailVitalArt Health = new(
        "040406 1c0b0b 280b0b 420c0b 520403 750100 a40100 b00100 a10100 850100 4e0000 330000 2d0505 210505 190606 140505",
        "060100 431534 2c0e2d 180826 0a0303 050202 020000 010000 020000 030101 03011e 0a0418 200a0c 361111 290d0d 3f1313");
    public static readonly RetailVitalArt Stamina = new(
        "040405 1a1202 332303 5c3f06 885c04 ab7300 d99301 e69b02 cd8a05 af7705 7f5502 4c3300 2a1c00 231803 191204 0f0b02",
        "060200 402b11 28210b 121102 010100 000000 000000 000000 000000 000000 07000b 0b040c 1e1709 3a2c12 27180c 3d2813");
    public static readonly RetailVitalArt Mana = new(
        "080300 0d1e2d 0d3449 023957 00517a 0173a8 0076aa 006393 00507a 003e60 002a43 001725 051626 06131f 061118 061311",
        "080300 0d2622 071516 060d14 03060a 000000 000000 000000 000000 070401 0a0601 0c0c0a 17252d 1d383e 18212e 131a18");

    public IBrush Fill { get; }
    public IBrush Track { get; }

    private RetailVitalArt(string fill, string track) => (Fill, Track) = (VerticalGradient(fill), VerticalGradient(track));

    private static IBrush VerticalGradient(string rows)
    {
        var colours = rows.Split(' ');
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative)
        };
        for (var i = 0; i < colours.Length; i++)
            brush.GradientStops.Add(new GradientStop(Color.Parse("#" + colours[i]), (i + 0.5) / colours.Length));
        return brush;
    }
}

/// <summary>
/// Retail's vitals window edge around its child, from 0x060074BF: per 5 DIPs of <see cref="Decorator.Padding"/>, 2 clear, then a line of
/// dark bronze, one of bronze and one of black, with black inside.
/// </summary>
public sealed class RetailVitalsFrame : Decorator
{
    // The edge's width in DIPs.
    private const double EdgeWidth = 5;
    private static readonly IPen Dark = new Pen(new SolidColorBrush(Color.Parse("#67553B")), 1);
    private static readonly IPen Bronze = new Pen(new SolidColorBrush(Color.Parse("#907854")), 1);

    public RetailVitalsFrame() => Padding = new Thickness(EdgeWidth);

    public override void Render(DrawingContext context)
    {
        var box = new Rect(Bounds.Size);
        context.DrawRectangle(Brushes.Black, null, box.Deflate(2));
        context.DrawRectangle(null, Dark, box.Deflate(2.5));
        context.DrawRectangle(null, Bronze, box.Deflate(3.5));
    }
}

/// <summary>Bold white text with a black outline, centred on its ink, so a number reads over any part of a bar.</summary>
public sealed class OutlinedText : Control
{
    private static readonly IPen Outline = new Pen(Brushes.Black, 2.2, lineJoin: PenLineJoin.Round);
    private static readonly Typeface Face = new(DerethPalette.Body, FontStyle.Normal, FontWeight.Bold);
    private readonly double _size;
    private string _text = "";
    private Geometry? _ink;

    /// <param name="size">The font size in DIPs.</param>
    public OutlinedText(double size)
    {
        _size = size;
        IsHitTestVisible = false;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (value == _text) return;
            _text = value;
            _ink = null;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        _ink ??= new FormattedText(_text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, _size, Brushes.White).BuildGeometry(default);
        if (_ink == null) return;
        var ink = _ink.Bounds;
        using (context.PushTransform(Matrix.CreateTranslation(Bounds.Width / 2 - ink.Center.X, Bounds.Height / 2 - ink.Center.Y)))
        {
            context.DrawGeometry(null, Outline, _ink);
            context.DrawGeometry(Brushes.White, null, _ink);
        }
    }
}
