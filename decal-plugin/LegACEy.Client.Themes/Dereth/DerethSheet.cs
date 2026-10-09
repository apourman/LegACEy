using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LegACEy.Client.Themes;

/// <summary>
/// The Dereth chrome: one 2× sprite sheet with named regions, styled after the concept art. New art replaces the PNG,
/// this table and <see cref="Scale"/> only. Region rects are in sheet pixels; layout is in DIPs, so sheet pixels divided by
/// <see cref="Scale"/>. Corners, top edges and left edges are stored top-left; the other sides mirror them.
/// </summary>
internal static class DerethSheet
{
    /// <summary>Sheet pixels per DIP: the sheet is painted at 2×.</summary>
    public const double Scale = 2;

    private static Bitmap? _image;

    /// <summary>The sheet, decoded once from the embedded asset.</summary>
    public static Bitmap Image => _image ??= new Bitmap(AssetLoader.Open(new Uri("avares://LegACEy.Client.Themes/Assets/dereth-sheet.png")));

    // The sheet is painted by a generator, not cut from the concept. Every region sits in a 6 px gutter whose inner 2 px repeat
    // the region's edge pixels, so filtered sampling never pulls in a neighbour.

    // Window frame. Corner 17 DIP, edges 8 DIP.
    public static readonly Rect FrameCorner = new(6, 6, 34, 34);
    public static readonly Rect FrameTop = new(46, 6, 120, 16);
    public static readonly Rect FrameLeft = new(172, 6, 16, 120);

    // Slot frame, 38 DIP cells: a 32 px icon inside 3 DIP edges. Corner 8 DIP.
    public static readonly Rect SlotCorner = new(194, 6, 16, 16);
    public static readonly Rect SlotTop = new(216, 6, 6, 6);
    public static readonly Rect SlotLeft = new(228, 6, 6, 6);

    // Title rule: left cap, stretched middle, right cap. 5 DIP tall.
    public static readonly Rect RuleLeft = new(20, 132, 8, 10);
    public static readonly Rect RuleMiddle = new(34, 132, 200, 10);
    public static readonly Rect RuleRight = new(240, 132, 8, 10);

    private static readonly Dictionary<(DerethFrameArt Art, DerethState State), NineSlice> Frames = new()
    {
        [(DerethFrameArt.Window, DerethState.Normal)] = new NineSlice(FrameCorner, FrameTop, FrameLeft, Color.Parse("#081016")),
        [(DerethFrameArt.Slot, DerethState.Normal)] = new NineSlice(SlotCorner, SlotTop, SlotLeft, Color.Parse("#02070B")),
        // Button frame: close box, text buttons and the header icon. Corner 8 DIP, edges 4 DIP.
        [(DerethFrameArt.Button, DerethState.Normal)] = new NineSlice(new(240, 6, 16, 16), new(262, 6, 8, 8), new(276, 6, 8, 8), Color.Parse("#071016")),
        [(DerethFrameArt.Button, DerethState.Hover)] = new NineSlice(new(290, 6, 16, 16), new(312, 6, 8, 8), new(326, 6, 8, 8), Color.Parse("#0E1923")),
        [(DerethFrameArt.Button, DerethState.Pressed)] = new NineSlice(new(340, 6, 16, 16), new(362, 6, 8, 8), new(6, 132, 8, 8), Color.Parse("#03070A")),
    };

    // Search field per state: left cap (carries the magnifier), stretched middle, right cap. 25 DIP tall.
    private static readonly Dictionary<DerethFieldState, (Rect Left, Rect Middle, Rect Right)> Searches = new()
    {
        [DerethFieldState.Normal] = (new(254, 132, 54, 50), new(6, 188, 100, 50), new(112, 188, 28, 50)),
        [DerethFieldState.Hover] = (new(146, 188, 54, 50), new(206, 188, 100, 50), new(312, 188, 28, 50)),
        [DerethFieldState.Focus] = (new(6, 244, 54, 50), new(66, 244, 100, 50), new(172, 244, 28, 50)),
    };

    // Whole pictures per state: the 34×32 DIP paging arrows and the 12×12 DIP close X.
    private static readonly Dictionary<(DerethSpriteArt Art, DerethState State), Rect> Sprites = new()
    {
        [(DerethSpriteArt.PagerPrevious, DerethState.Normal)] = new(206, 244, 68, 64),
        [(DerethSpriteArt.PagerNext, DerethState.Normal)] = new(280, 244, 68, 64),
        [(DerethSpriteArt.PagerPrevious, DerethState.Hover)] = new(6, 314, 68, 64),
        [(DerethSpriteArt.PagerNext, DerethState.Hover)] = new(80, 314, 68, 64),
        [(DerethSpriteArt.PagerPrevious, DerethState.Pressed)] = new(154, 314, 68, 64),
        [(DerethSpriteArt.PagerNext, DerethState.Pressed)] = new(228, 314, 68, 64),
        [(DerethSpriteArt.Close, DerethState.Normal)] = new(302, 314, 24, 24),
        [(DerethSpriteArt.Close, DerethState.Hover)] = new(332, 314, 24, 24),
        [(DerethSpriteArt.Close, DerethState.Pressed)] = new(6, 384, 24, 24),
    };

    /// <summary>The frame art for a state. A state the sheet has no region for falls back to normal.</summary>
    public static NineSlice Frame(DerethFrameArt art, DerethState state) =>
        Frames.TryGetValue((art, state), out var slice) ? slice : Frames[(art, DerethState.Normal)];

    /// <summary>The search field's three pieces in a state.</summary>
    public static (Rect Left, Rect Middle, Rect Right) Search(DerethFieldState state) => Searches[state];

    /// <summary>A whole picture in a state.</summary>
    public static Rect Sprite(DerethSpriteArt art, DerethState state) => Sprites[(art, state)];

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

/// <summary>The search field's state: focus stands in for pressed, as a text field stays active while typing.</summary>
internal enum DerethFieldState { Normal, Hover, Focus }

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
