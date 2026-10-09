using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LegACEy.Client.Themes;

/// <summary>
/// The Dereth chrome: one sprite sheet with named regions. The first sheet is cut from the concept art
/// (<c>.artifacts/client-ui/renders/vault-concept.png</c>) with the prototype's rects; a hi-res sheet replaces the PNG and
/// this table only. Corners, top edges and left edges are stored top-left; the other sides mirror them.
/// </summary>
internal static class DerethSheet
{
    private static Bitmap? _image;

    /// <summary>The sheet, decoded once from the embedded asset.</summary>
    public static Bitmap Image => _image ??= new Bitmap(AssetLoader.Open(new Uri("avares://LegACEy.Client.Themes/Assets/dereth-sheet.png")));

    // Window frame. The corner (18 px) is larger than the 8 px edges. Concept rects: corner 1502,742; top edge 1300,751
    // (60×8, flipped); left edge 1511,450 (8×60, flipped); corner rotated 180°.
    public static readonly Rect FrameCorner = new(174, 1, 18, 18);
    public static readonly Rect FrameTop = new(21, 62, 60, 8);
    public static readonly Rect FrameLeft = new(1, 1, 8, 60);

    // Slot frame (46 px cells). Concept rects: corner 1232,321 (9×9); top 1242,322 (3×7); left 1233,331 (7×3).
    public static readonly Rect SlotCorner = new(1, 62, 9, 9);
    public static readonly Rect SlotTop = new(82, 62, 3, 7);
    public static readonly Rect SlotLeft = new(11, 72, 7, 3);

    // Button frame, for the close box and text buttons. Concept rects: corner 1249,714 (9×9); top 1260,715 (4×4);
    // left 1250,724 (4×4).
    public static readonly Rect ButtonCorner = new(11, 62, 9, 9);
    public static readonly Rect ButtonTop = new(1, 72, 4, 4);
    public static readonly Rect ButtonLeft = new(6, 72, 4, 4);

    // Title rule: left cap, stretched middle, right cap. Concept rects: 1300,277 (4×5), 1304,277 (100×5), 1404,277 (4×5).
    public static readonly Rect RuleLeft = new(86, 62, 4, 5);
    public static readonly Rect RuleMiddle = new(91, 62, 100, 5);
    public static readonly Rect RuleRight = new(192, 62, 4, 5);

    // Search field: left cap (carries the magnifier), middle, right cap. Concept rects: 887,301 (27×25), 975,301 (50×25),
    // 1030,301 (14×25).
    public static readonly Rect SearchLeft = new(80, 1, 27, 25);
    public static readonly Rect SearchMiddle = new(108, 1, 50, 25);
    public static readonly Rect SearchRight = new(159, 1, 14, 25);

    // Paging arrows, whole 34×32 buttons. Concept rects: 1250,715 (previous) and 1452,715 (next).
    public static readonly Rect ArrowPrevious = new(10, 1, 34, 32);
    public static readonly Rect ArrowNext = new(45, 1, 34, 32);

    public static readonly NineSlice WindowFrame = new(FrameCorner, FrameTop, FrameLeft, edge: 8, fill: Color.Parse("#15202B"));
    public static readonly NineSlice Slot = new(SlotCorner, SlotTop, SlotLeft, edge: 7, fill: Color.Parse("#0D131B"));
    public static readonly NineSlice Button = new(ButtonCorner, ButtonTop, ButtonLeft, edge: 4, fill: Color.Parse("#131D28"));

    /// <summary>Draws a sheet region into a rect, mirrored about its centre when asked.</summary>
    public static void Draw(DrawingContext context, Rect source, Rect destination, bool flipX = false, bool flipY = false)
    {
        if (destination.Width <= 0 || destination.Height <= 0) return;
        if (!flipX && !flipY)
        {
            context.DrawImage(Image, source, destination);
            return;
        }
        var center = destination.Center;
        var mirror = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(flipX ? -1 : 1, flipY ? -1 : 1) * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(mirror))
            context.DrawImage(Image, source, destination);
    }
}

/// <summary>A frame's art: a top-left corner (its size is the corner size), a top edge and a left edge, and the fill.</summary>
internal sealed class NineSlice
{
    public NineSlice(Rect corner, Rect top, Rect left, double edge, Color fill)
    {
        Corner = corner;
        Top = top;
        Left = left;
        Edge = edge;
        Fill = fill;
    }

    public Rect Corner { get; }
    public Rect Top { get; }
    public Rect Left { get; }
    /// <summary>The edge thickness; the corner is drawn at its own size, which is larger.</summary>
    public double Edge { get; }
    public Color Fill { get; }
}
