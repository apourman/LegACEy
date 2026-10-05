using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LegACEy.Client.Themes;

/// <summary>Nine-slices a control face while keeping its corners and tiled edges at native size.</summary>
internal sealed class GameArtBorder : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<GameArtBorder>();
    public static readonly StyledProperty<IBrush?> BorderBrushProperty = Border.BorderBrushProperty.AddOwner<GameArtBorder>();
    public static readonly StyledProperty<bool> SliceProperty = AvaloniaProperty.Register<GameArtBorder, bool>(nameof(Slice), true);
    public bool Slice { get => GetValue(SliceProperty); set => SetValue(SliceProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }

    static GameArtBorder() => AffectsRender<GameArtBorder>(BackgroundProperty, BorderBrushProperty, SliceProperty);

    private readonly bool _outline;

    public GameArtBorder(bool outline)
    {
        _outline = outline;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context)
    {
        if (Background is not ImageBrush { Source: IImage image })
        {
            context.DrawRectangle(Background, BorderBrush == null ? null : new Pen(BorderBrush, 1), new Rect(Bounds.Size));
            return;
        }

        if (!Slice)
        {
            var width = Math.Min(image.Size.Width, Bounds.Width);
            var height = Math.Min(image.Size.Height, Bounds.Height);
            context.DrawImage(image, new Rect(0, 0, width, height), new Rect(Math.Floor((Bounds.Width - width) / 2), Math.Floor((Bounds.Height - height) / 2), width, height));
            return;
        }

        if (_outline)
            context.DrawRectangle(Brushes.Black, null, new Rect(Bounds.Size));

        var inset = Math.Min(4, Math.Min(image.Size.Width, image.Size.Height) / 3);
        var left = Math.Min(inset, Bounds.Width / 2);
        var top = Math.Min(inset, Bounds.Height / 2);
        var sx = new[] { 0d, inset, image.Size.Width - inset, image.Size.Width };
        var sy = new[] { 0d, inset, image.Size.Height - inset, image.Size.Height };
        var dx = new[] { 0d, left, Bounds.Width - left, Bounds.Width };
        var dy = new[] { 0d, top, Bounds.Height - top, Bounds.Height };
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            var source = new Rect(sx[column], sy[row], sx[column + 1] - sx[column], sy[row + 1] - sy[row]);
            var target = new Rect(dx[column], dy[row], dx[column + 1] - dx[column], dy[row + 1] - dy[row]);
            GameArtDrawing.Tile(context, image, source, target, column == 1, row == 1);
        }
        if (_outline && BorderBrush != null)
            context.DrawRectangle(null, new Pen(BorderBrush, 1), new Rect(Bounds.Size).Deflate(0.5));
    }
}

internal static class GameArtDrawing
{
    internal static void Tile(DrawingContext context, IImage image, Rect source, Rect destination, bool horizontal, bool vertical)
    {
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0) return;
        var tileWidth = horizontal ? source.Width : destination.Width;
        var tileHeight = vertical ? source.Height : destination.Height;
        for (var y = destination.Y; y < destination.Bottom; y += tileHeight)
        for (var x = destination.X; x < destination.Right; x += tileWidth)
        {
            var target = new Rect(x, y, Math.Min(tileWidth, destination.Right - x), Math.Min(tileHeight, destination.Bottom - y));
            var cropped = new Rect(source.X, source.Y, source.Width * target.Width / tileWidth, source.Height * target.Height / tileHeight);
            context.DrawImage(image, cropped, target);
        }
    }
}
