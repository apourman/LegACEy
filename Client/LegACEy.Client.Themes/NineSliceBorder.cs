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
        0x060074BF, 0x060074C0, 0x060074C1,
        0x060074C2, AcClientTheme.WindowChromeCenterId, 0x060074C3,
        0x060074C4, 0x060074C5, 0x060074C6
    };

    private readonly IImage?[] _pieces;
    private readonly IBrush _fallback;
    private readonly double _edgeSize;

    public NineSliceBorder(IGameArtSource source, double edgeSize = 8, IBrush? fallback = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        _edgeSize = edgeSize;
        _fallback = fallback ?? new SolidColorBrush(Color.FromRgb(0x23, 0x20, 0x19));
        _pieces = DefaultPieceIds.Select(id => (IImage?)GameArtImageExtension.CreateBitmap(source, id)).ToArray();
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(_fallback, null, bounds);
        var left = Math.Max(_pieces[0]?.Size.Width ?? _edgeSize, _pieces[6]?.Size.Width ?? _edgeSize);
        var right = Math.Max(_pieces[2]?.Size.Width ?? _edgeSize, _pieces[8]?.Size.Width ?? _edgeSize);
        var top = Math.Max(_pieces[0]?.Size.Height ?? _edgeSize, _pieces[2]?.Size.Height ?? _edgeSize);
        var bottom = Math.Max(_pieces[6]?.Size.Height ?? _edgeSize, _pieces[8]?.Size.Height ?? _edgeSize);
        if (Bounds.Width <= left + right || Bounds.Height <= top + bottom) return;

        var xs = new[] { 0d, left, Bounds.Width - right, Bounds.Width };
        var ys = new[] { 0d, top, Bounds.Height - bottom, Bounds.Height };
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            var index = row * 3 + column;
            var image = _pieces[index];
            if (image == null) continue;
            var destination = new Rect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]);
            if (row == 1 && column is 0 or 2)
                Tile(context, image, destination, horizontal: false);
            else if (column == 1 && row is 0 or 2)
                Tile(context, image, destination, horizontal: true);
            else if (row == 1 && column == 1)
                Tile(context, image, destination, horizontal: true, vertical: true);
            else
                context.DrawImage(image, new Rect(image.Size), destination);
        }
    }

    private static void Tile(DrawingContext context, IImage image, Rect destination, bool horizontal, bool vertical = false)
    {
        var tileWidth = horizontal ? Math.Max(1, image.Size.Width) : destination.Width;
        var tileHeight = vertical ? Math.Max(1, image.Size.Height) : destination.Height;
        if (horizontal && !vertical) tileHeight = destination.Height;
        if (vertical && !horizontal) tileWidth = destination.Width;
        for (var y = destination.Y; y < destination.Bottom; y += tileHeight)
        for (var x = destination.X; x < destination.Right; x += tileWidth)
        {
            var target = new Rect(x, y, Math.Min(tileWidth, destination.Right - x), Math.Min(tileHeight, destination.Bottom - y));
            context.DrawImage(image, new Rect(image.Size), target);
        }
    }
}
