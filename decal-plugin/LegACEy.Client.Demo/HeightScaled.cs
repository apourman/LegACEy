using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Demo;

/// <summary>
/// Lays its child out at a fixed layout height and draws it scaled to fill the height it is given, so a taller window draws its content
/// larger rather than with more room. The width left over at that scale is the child's to fill, so a wider window still stretches it.
/// </summary>
public sealed class HeightScaled : Decorator
{
    private readonly double _layoutHeight;
    private readonly ScaleTransform _transform = new();

    /// <param name="layoutHeight">The child's height before the scale, in DIPs.</param>
    public HeightScaled(double layoutHeight) => _layoutHeight = layoutHeight;

    private double ScaleFor(Size size) => !double.IsInfinity(size.Height) && size.Height > 0 ? size.Height / _layoutHeight : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        var scale = ScaleFor(availableSize);
        child.Measure(new Size(availableSize.Width / scale, _layoutHeight));
        return new Size(!double.IsInfinity(availableSize.Width) ? availableSize.Width : child.DesiredSize.Width * scale, _layoutHeight * scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child) return finalSize;
        var scale = ScaleFor(finalSize);
        child.RenderTransformOrigin = RelativePoint.TopLeft;
        child.RenderTransform = _transform;
        child.Arrange(new Rect(0, 0, finalSize.Width / scale, _layoutHeight));
        // Layout rounding snaps the child to the window's pixels before the scale applies, so it can come out a pixel larger than its layout
        // size, and the scale would carry its right and bottom edges past the window. Each axis is scaled to fit the arranged size exactly.
        var scaleX = child.Bounds.Width > 0 ? finalSize.Width / child.Bounds.Width : scale;
        var scaleY = child.Bounds.Height > 0 ? finalSize.Height / child.Bounds.Height : scale;
        if (scaleX != _transform.ScaleX || scaleY != _transform.ScaleY) (_transform.ScaleX, _transform.ScaleY) = (scaleX, scaleY);
        return finalSize;
    }
}
