namespace LegACEy.Client.PanelHost;

/// <summary>A tightly packed BGRA32 frame rendered by the panel host.</summary>
public sealed class PanelFrame
{
    internal PanelFrame(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = checked(width * 4);
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }
}
