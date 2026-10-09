using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LegACEy.Client.Themes;

/// <summary>
/// The Dereth chrome: one sprite sheet with named regions. The first sheet is cut from the concept art
/// (<c>.artifacts/client-ui/renders/vault-concept.png</c>); a hi-res sheet replaces the PNG, this table and
/// <see cref="Scale"/> only. Region rects are in sheet pixels; layout is in DIPs, so sheet pixels divided by
/// <see cref="Scale"/>. Corners, top edges and left edges are stored top-left; the other sides mirror them.
/// </summary>
internal static class DerethSheet
{
    /// <summary>Sheet pixels per DIP: 1 for the concept-cut sheet, 2 for a 2× sheet.</summary>
    public const double Scale = 1;

    private static Bitmap? _image;

    /// <summary>The sheet, decoded once from the embedded asset.</summary>
    public static Bitmap Image => _image ??= new Bitmap(AssetLoader.Open(new Uri("avares://LegACEy.Client.Themes/Assets/dereth-sheet.png")));

    // Window frame. Corner 17 px, edges 8 px. Concept: corner 1502,742 (17×17, bottom-right, rotated 180°); bottom edge
    // 1300,751 (60×8, flipped to top); right edge 1511,450 (8×60, flipped to left). Outer lips align with the edges.
    public static readonly Rect FrameCorner = new(174, 1, 17, 17);
    public static readonly Rect FrameTop = new(1, 62, 60, 8);
    public static readonly Rect FrameLeft = new(1, 1, 8, 60);

    // Slot frame, 46 px cells. Concept: corner 1233,322 (8×8); top 1242,322 (3×7); left 1233,331 (7×3).
    public static readonly Rect SlotCorner = new(62, 62, 8, 8);
    public static readonly Rect SlotTop = new(80, 62, 3, 7);
    public static readonly Rect SlotLeft = new(6, 71, 7, 3);

    // Button frame for the close box and text buttons. Concept: corner 1250,715 (8×8); top 1260,715 (4×4); left 1250,724 (4×4).
    public static readonly Rect ButtonCorner = new(71, 62, 8, 8);
    public static readonly Rect ButtonTop = new(195, 62, 4, 4);
    public static readonly Rect ButtonLeft = new(1, 71, 4, 4);

    // Title rule: left cap, stretched middle, right cap. Concept: 1300,277 (4×5), 1304,277 (100×5), 1404,277 (4×5).
    public static readonly Rect RuleLeft = new(84, 62, 4, 5);
    public static readonly Rect RuleMiddle = new(89, 62, 100, 5);
    public static readonly Rect RuleRight = new(190, 62, 4, 5);

    // Search field: left cap (carries the magnifier), middle, right cap. Concept: 887,301 (27×25), 975,301 (50×25), 1030,301 (14×25).
    public static readonly Rect SearchLeft = new(80, 1, 27, 25);
    public static readonly Rect SearchMiddle = new(108, 1, 50, 25);
    public static readonly Rect SearchRight = new(159, 1, 14, 25);

    // Paging arrows, whole 34×32 buttons. Concept: 1250,715 (previous) and 1452,715 (next).
    public static readonly Rect ArrowPrevious = new(10, 1, 34, 32);
    public static readonly Rect ArrowNext = new(45, 1, 34, 32);

    private static readonly Dictionary<(DerethFrameArt Art, DerethState State), NineSlice> Frames = new()
    {
        [(DerethFrameArt.Window, DerethState.Normal)] = new NineSlice(FrameCorner, FrameTop, FrameLeft, Color.Parse("#15202B")),
        [(DerethFrameArt.Slot, DerethState.Normal)] = new NineSlice(SlotCorner, SlotTop, SlotLeft, Color.Parse("#0D131B")),
        [(DerethFrameArt.Button, DerethState.Normal)] = new NineSlice(ButtonCorner, ButtonTop, ButtonLeft, Color.Parse("#131D28")),
    };

    /// <summary>
    /// The frame art for a state. A state the sheet has no region for falls back to normal, so a hover or pressed
    /// region is added here when the art exists.
    /// </summary>
    public static NineSlice Frame(DerethFrameArt art, DerethState state) =>
        Frames.TryGetValue((art, state), out var slice) ? slice : Frames[(art, DerethState.Normal)];

    /// <summary>Draws a sheet region into a rect in DIPs, mirrored about its centre when asked.</summary>
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

/// <summary>The interaction state a frame is drawn in.</summary>
internal enum DerethState { Normal, Hover, Pressed }

/// <summary>A frame's art: a top-left corner, a top edge and a left edge, and a prebuilt fill.</summary>
internal sealed class NineSlice
{
    public NineSlice(Rect corner, Rect top, Rect left, Color fill)
    {
        CornerSource = corner;
        TopSource = top;
        LeftSource = left;
        Fill = new SolidColorBrush(fill);
    }

    public Rect CornerSource { get; }
    public Rect TopSource { get; }
    public Rect LeftSource { get; }
    /// <summary>The corner's size in DIPs. It is larger than the edges.</summary>
    public double Corner => CornerSource.Width / DerethSheet.Scale;
    /// <summary>The edge thickness in DIPs.</summary>
    public double Edge => TopSource.Height / DerethSheet.Scale;
    public IBrush Fill { get; }
}
