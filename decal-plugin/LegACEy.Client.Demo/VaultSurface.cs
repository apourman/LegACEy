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
    private static readonly IBrush Rivet = VaultShellPanel.Brush("#171715");
    private static readonly Pen Shadow = new(VaultShellPanel.Brush("#050607"));
    private readonly Pen _highlight;
    private static readonly Pen Grain = new(VaultShellPanel.Brush("#04FFFFFF"));
    private static readonly Pen DarkGrain = new(VaultShellPanel.Brush("#14000000"));

    private static readonly IBrush Mottle = new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.Parse("#06FFFFFF"), 0),
            new GradientStop(Color.Parse("#03FFFFFF"), 0.5),
            new GradientStop(Colors.Transparent, 1)
        }
    };

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
                new GradientStop(Color.Parse(blue ? "#263B60" : socket ? "#080A0B" : "#181918"), 0),
                new GradientStop(Color.Parse(blue ? "#101B32" : "#0C0D0E"), 0.55),
                new GradientStop(Color.Parse(blue ? "#1A2B48" : socket ? "#191C1D" : "#171817"), 1)
            }
        };
        _edge = new Pen(VaultShellPanel.Brush(material == VaultMaterial.SelectedSocket ? "#DDB968" : socket ? "#484B4B" : "#A68A55"));
        _highlight = new Pen(VaultShellPanel.Brush(socket ? "#646868" : "#C4AA71"));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        var rect = new Rect(Bounds.Size).Deflate(0.5);
        context.DrawRectangle(_fill, Shadow, rect);
        // Fixed mottling and fine grain evoke retail charcoal without external textures.
        using (context.PushClip(rect.Deflate(3)))
        {
            if (_material == VaultMaterial.Slate)
            {
                for (var y = 8; y < Bounds.Height; y += 29)
                for (var x = 8; x < Bounds.Width; x += 37)
                {
                    var seed = unchecked((uint)(x * 374761393 + y * 668265263));
                    seed = (seed ^ (seed >> 13)) * 1274126177u;
                    context.DrawEllipse(Mottle, null,
                        new Point(x + seed % 19, y + (seed >> 8) % 13),
                        26 + (seed >> 16) % 24, 14 + (seed >> 24) % 18);
                }
            }
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
        context.DrawLine(_highlight, inner.TopLeft, inner.TopRight);
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
                    context.DrawLine(_highlight, point + new Vector(dx * 3, dy * 3), point + new Vector(dx * 15, dy * 3));
                    context.DrawLine(_edge, point + new Vector(dx * 3, dy * 3), point + new Vector(dx * 3, dy * 15));
                    context.DrawEllipse(Rivet, _edge,
                        point + new Vector(dx * 3, dy * 3), 2, 2);
                }
            }
        }
    }
}
