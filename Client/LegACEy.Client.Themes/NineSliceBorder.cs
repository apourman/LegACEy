using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Themes;

/// <summary>Draws retail chrome corners at native size and tiles its edge and center pieces.</summary>
public sealed class NineSliceBorder : Control
{
    public static readonly uint[] DefaultPieceIds =
    {
        0x060074C3, 0x060074BF, 0x060074C4,
        0x060074C0, AcClientTheme.WindowChromeCenterId, 0x060074C2,
        0x060074C5, 0x060074C1, 0x060074C6
    };

    private IImage?[] _pieces;
    private readonly IBrush _fallback;
    private readonly double _edgeSize;
    private IGameArtSource _artSource;

    public NineSliceBorder(IGameArtSource source, double edgeSize = 8, IBrush? fallback = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        _artSource = source;
        _edgeSize = edgeSize;
        _fallback = fallback ?? new SolidColorBrush(Color.FromRgb(0x23, 0x20, 0x19));
        _pieces = LoadPieces(source);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public void SetArtSource(IGameArtSource source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (ReferenceEquals(source, _artSource)) return;
        foreach (var bitmap in _pieces.OfType<IDisposable>())
            bitmap.Dispose();
        _artSource = source;
        _pieces = LoadPieces(source);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        using var clip = context.PushClip(bounds);
        context.DrawRectangle(_fallback, null, bounds);
        var left = _pieces[3]?.Size.Width ?? _edgeSize;
        var right = _pieces[5]?.Size.Width ?? _edgeSize;
        var top = _pieces[1]?.Size.Height ?? _edgeSize;
        var bottom = _pieces[7]?.Size.Height ?? _edgeSize;
        if (Bounds.Width <= left + right || Bounds.Height <= top + bottom) return;

        // Retail corners extend along the frame farther than the edge's thickness.
        // Draw the interior and native-thickness edges first, then the native-size corners.
        Draw(4, new Rect(left, top, Bounds.Width - left - right, Bounds.Height - top - bottom), true, true);
        var tl = _pieces[0]?.Size ?? new Size(left, top);
        var tr = _pieces[2]?.Size ?? new Size(right, top);
        var bl = _pieces[6]?.Size ?? new Size(left, bottom);
        var br = _pieces[8]?.Size ?? new Size(right, bottom);
        Draw(1, new Rect(tl.Width, 0, Math.Max(0, Bounds.Width - tl.Width - tr.Width), top), true, false);
        Draw(7, new Rect(bl.Width, Bounds.Height - bottom, Math.Max(0, Bounds.Width - bl.Width - br.Width), bottom), true, false);
        Draw(3, new Rect(0, tl.Height, left, Math.Max(0, Bounds.Height - tl.Height - bl.Height)), false, true);
        Draw(5, new Rect(Bounds.Width - right, tr.Height, right, Math.Max(0, Bounds.Height - tr.Height - br.Height)), false, true);
        Draw(0, new Rect(0, 0, tl.Width, tl.Height), false, false);
        Draw(2, new Rect(Bounds.Width - tr.Width, 0, tr.Width, tr.Height), false, false);
        Draw(6, new Rect(0, Bounds.Height - bl.Height, bl.Width, bl.Height), false, false);
        Draw(8, new Rect(Bounds.Width - br.Width, Bounds.Height - br.Height, br.Width, br.Height), false, false);

        void Draw(int index, Rect destination, bool horizontal, bool vertical)
        {
            var image = _pieces[index];
            if (image != null)
                GameArtDrawing.Tile(context, image, new Rect(image.Size), destination, horizontal, vertical);
        }
    }

    private static IImage?[] LoadPieces(IGameArtSource source) =>
        DefaultPieceIds.Select(id => (IImage?)GameArtImageExtension.CreateBitmap(source, id)).ToArray();
}
