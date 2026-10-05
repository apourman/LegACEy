using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.Demo;

internal enum VaultMaterial { Slate, Socket, SelectedSocket, BlueSteel }

/// <summary>Resolution-independent vault skin, shipped as drawing code with no external skin files.</summary>
internal sealed class VaultSurface : Decorator
{
    private readonly VaultMaterial _material;
    private readonly bool _ornate;
    private readonly IBrush _fill;
    private readonly Pen _edge;
    private static readonly IBrush Rivet = VaultShellPanel.Brush("#171D20");
    private static readonly Pen Shadow = new(VaultShellPanel.Brush("#090D10"));
    private static readonly Pen Highlight = new(VaultShellPanel.Brush("#A78B5C"));
    private static readonly Pen Grain = new(VaultShellPanel.Brush("#0CFFFFFF"));
    private static readonly Pen DarkGrain = new(VaultShellPanel.Brush("#18000000"));

    public VaultSurface(VaultMaterial material = VaultMaterial.Slate, bool ornate = false)
    {
        _material = material;
        _ornate = ornate;
        var blue = material == VaultMaterial.BlueSteel;
        var socket = material == VaultMaterial.Socket || material == VaultMaterial.SelectedSocket;
        _fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(blue ? "#30495B" : socket ? "#101619" : "#22292B"), 0),
                new GradientStop(Color.Parse(blue ? "#152737" : "#13191D"), 0.55),
                new GradientStop(Color.Parse(blue ? "#203747" : socket ? "#252B2C" : "#1A2225"), 1)
            }
        };
        _edge = new Pen(VaultShellPanel.Brush(material == VaultMaterial.SelectedSocket ? "#E4BA69" : "#786345"));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        var rect = new Rect(Bounds.Size).Deflate(0.5);
        context.DrawRectangle(_fill, Shadow, rect);
        // Fixed, subtle grain: no random animation, bitmap allocation, or runtime asset loading.
        using (context.PushClip(rect.Deflate(3)))
        {
            for (var y = 4; y < Bounds.Height - 3; y += 4)
            for (var x = 4; x < Bounds.Width - 3; x += 5)
            {
                var seed = unchecked((uint)(x * 374761393 + y * 668265263));
                seed = (seed ^ (seed >> 13)) * 1274126177u;
                if (seed % 3 == 0) continue;
                var start = new Point(x + seed % 4, y + (seed >> 8) % 3);
                context.DrawLine(seed % 2 == 0 ? Grain : DarkGrain, start, start + new Vector(2, -1));
            }
        }
        context.DrawRectangle(null, _edge, rect.Deflate(1));
        context.DrawRectangle(null, Shadow, rect.Deflate(2));
        var inset = _ornate ? 6 : 4;
        var inner = rect.Deflate(inset);
        context.DrawLine(Highlight, inner.TopLeft, inner.TopRight);
        context.DrawLine(_edge, inner.TopLeft, inner.BottomLeft);
        context.DrawLine(Shadow, inner.BottomLeft, inner.BottomRight);
        context.DrawLine(Shadow, inner.TopRight, inner.BottomRight);
        if (_ornate || _material != VaultMaterial.Slate)
        {
            foreach (var corner in new[] { (inner.TopLeft, 1, 1), (inner.TopRight, -1, 1),
                         (inner.BottomLeft, 1, -1), (inner.BottomRight, -1, -1) })
            {
                var point = corner.Item1;
                var dx = corner.Item2;
                var dy = corner.Item3;
                context.DrawLine(_edge, point, point + new Vector(dx * 6, dy * 6));
                if (_ornate)
                {
                    context.DrawLine(Highlight, point + new Vector(dx * 3, dy * 3), point + new Vector(dx * 15, dy * 3));
                    context.DrawLine(_edge, point + new Vector(dx * 3, dy * 3), point + new Vector(dx * 3, dy * 15));
                    context.DrawEllipse(Rivet, _edge,
                        point + new Vector(dx * 3, dy * 3), 2, 2);
                }
            }
        }
    }
}
