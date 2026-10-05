using System.Collections.Generic;
using System.Drawing;

namespace LegACEy.Client.PanelHost;

/// <summary>A tightly packed BGRA32 frame rendered by the panel host.</summary>
public sealed class PanelFrame
{
    internal PanelFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = checked(width * 4);
        Pixels = new byte[checked(Stride * height)];
        DirtyRectangles = new[] { new Rectangle(0, 0, width, height) };
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }

    /// <summary>Pixel regions changed by the most recent successful tick.</summary>
    public IReadOnlyList<Rectangle> DirtyRectangles { get; internal set; }
}
