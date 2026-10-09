using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Inventory;

/// <summary>
/// The faint carved picture of what goes in an empty paperdoll slot. The glyphs are drawn here, not taken from the DAT: line art
/// for jewellery, clothing, weapons and the shield; a small figure with the covered part lit for armour; coloured diamonds for
/// the aetheria. Each is engraved, a dark shadow 1 px below and right of the cut, and nearly transparent.
/// </summary>
internal static class InventoryGlyphs
{
    private const double Size = 30;
    // 22% for line art. The figure's parts are small, so it sits a little stronger, about 35%.
    private const double LineOpacity = 0.22;
    private const double FigureOpacity = 0.35;

    private static readonly IBrush CutLine = DerethPalette.Brush(Color.Parse("#B9A27A"));
    private static readonly IBrush FigureDim = DerethPalette.Brush(Color.Parse("#46525E"));
    private static readonly IBrush Shadow = DerethPalette.Brush(Colors.Black);

    private static readonly Dictionary<PaperdollSlot, string> Lines = new()
    {
        [PaperdollSlot.Neck] = "M8,6 Q15,19 22,6 M15,16 L18,20 L15,24 L12,20 Z",
        [PaperdollSlot.Trinket] = "M15,5 L17,13 L25,15 L17,17 L15,25 L13,17 L5,15 L13,13 Z",
        [PaperdollSlot.RightWrist] = Bangle,
        [PaperdollSlot.LeftWrist] = Bangle,
        [PaperdollSlot.RightRing] = Ring,
        [PaperdollSlot.LeftRing] = Ring,
        [PaperdollSlot.Shield] = "M8,7 L15,5 L22,7 L22,15 Q22,22 15,26 Q8,22 8,15 Z M15,5 L15,26 M8,13 L22,13",
        [PaperdollSlot.Weapon] = "M24,5 L24,8 L13,19 L11,17 L22,6 Z M8,15 L15,22 M12,19 L6,25",
        [PaperdollSlot.Ammo] = "M6,24 L20,10 M17,9 L21,9 L21,13 M10,26 L24,12 M21,11 L25,11 L25,15 M6,24 L5,21 M6,24 L9,25",
        [PaperdollSlot.Cloak] = "M11,6 L19,6 L23,25 Q15,22 7,25 Z M11,6 Q15,10 19,6",
        [PaperdollSlot.Shirt] = "M10,6 L13,6 Q15,9 17,6 L20,6 L25,11 L22,14 L20,12 L20,24 L10,24 L10,12 L8,14 L5,11 Z",
        [PaperdollSlot.Pants] = "M9,6 L21,6 L22,25 L17,25 L15,13 L13,25 L8,25 Z",
    };

    private const string Bangle = "M6,15 A9,5 0 1 0 24,15 A9,5 0 1 0 6,15 Z M9,15 A6,2.5 0 1 0 21,15 A6,2.5 0 1 0 9,15 Z";
    private const string Ring = "M9,18 A6,6 0 1 0 21,18 A6,6 0 1 0 9,18 Z M15,5 L18,8.5 L15,12 L12,8.5 Z";
    private const string Diamond = "M15,6 L22,15 L15,24 L8,15 Z M15,10 L18.5,15 L15,20 L11.5,15 Z";

    private static readonly Dictionary<PaperdollSlot, Color> Aetheria = new()
    {
        [PaperdollSlot.AetheriaOne] = Color.Parse("#4A7BD8"),
        [PaperdollSlot.AetheriaTwo] = Color.Parse("#D8B44A"),
        [PaperdollSlot.AetheriaThree] = Color.Parse("#D8584A"),
    };

    // The armour figure: each box is x, y, width, height in a 30 px square. A part is lit when its slot is the one shown.
    private static readonly (PaperdollSlot Part, double X, double Y, double W, double H)[] Figure =
    {
        (PaperdollSlot.Head, 12, 1, 6, 6), (PaperdollSlot.Chest, 11, 8, 8, 5.5), (PaperdollSlot.Abdomen, 11.5, 14, 7, 3),
        (PaperdollSlot.UpperArms, 7, 8, 3, 4.5), (PaperdollSlot.UpperArms, 20, 8, 3, 4.5),
        (PaperdollSlot.LowerArms, 6.5, 13, 3, 4), (PaperdollSlot.LowerArms, 20.5, 13, 3, 4),
        (PaperdollSlot.Hands, 6, 17.5, 3, 2.5), (PaperdollSlot.Hands, 21, 17.5, 3, 2.5),
        (PaperdollSlot.UpperLegs, 11.5, 17.5, 3, 4.5), (PaperdollSlot.UpperLegs, 15.5, 17.5, 3, 4.5),
        (PaperdollSlot.LowerLegs, 11.5, 22.5, 3, 4), (PaperdollSlot.LowerLegs, 15.5, 22.5, 3, 4),
        (PaperdollSlot.Feet, 10.5, 27, 4, 2), (PaperdollSlot.Feet, 15.5, 27, 4, 2),
    };

    /// <summary>The glyph for an empty slot.</summary>
    public static Control For(PaperdollSlot slot)
    {
        var canvas = new Canvas { Width = Size, Height = Size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (Aetheria.TryGetValue(slot, out var colour))
        {
            canvas.Opacity = LineOpacity;
            Engrave(canvas, Diamond, DerethPalette.Brush(colour), DerethPalette.Brush(colour.WithAlpha(0x50)));
        }
        else if (Lines.TryGetValue(slot, out var line))
        {
            canvas.Opacity = LineOpacity;
            Engrave(canvas, line, CutLine, fill: null);
        }
        else
        {
            canvas.Opacity = FigureOpacity;
            DrawFigure(canvas, slot);
        }
        return canvas;
    }

    /// <summary>The cut line on top of a 2.2 px dark shadow offset 1 px down and right.</summary>
    private static void Engrave(Canvas canvas, string data, IBrush line, IBrush? fill)
    {
        var shadow = new Path
        {
            Data = Geometry.Parse(data), Stroke = Shadow, StrokeThickness = 2.2, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
            Fill = fill == null ? null : Shadow,
        };
        Canvas.SetLeft(shadow, 1);
        Canvas.SetTop(shadow, 1.2);
        canvas.Children.Add(shadow);
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(data), Stroke = line, StrokeThickness = 1.5, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round, Fill = fill,
        });
    }

    private static void DrawFigure(Canvas canvas, PaperdollSlot slot)
    {
        foreach (var (part, x, y, width, height) in Figure)
        {
            var lit = part == slot;
            var radius = new CornerRadius(part == PaperdollSlot.Head ? 3 : 1);
            var shadow = new Border { Width = width, Height = height, CornerRadius = radius, Background = Shadow };
            Canvas.SetLeft(shadow, x + 0.8);
            Canvas.SetTop(shadow, y + 0.8);
            canvas.Children.Add(shadow);
            var box = new Border { Width = width, Height = height, CornerRadius = radius, Background = lit ? CutLine : FigureDim };
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            canvas.Children.Add(box);
        }
    }
}
