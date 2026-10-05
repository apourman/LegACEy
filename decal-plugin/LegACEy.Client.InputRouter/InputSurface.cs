namespace LegACEy.Client.InputRouter;

/// <summary>A visible panel snapshot used for screen-space hit testing.</summary>
public sealed class InputSurface
{
    public InputSurface(string id, int x, int y, int width, int height, int zOrder, bool wantsKeyboard = false)
    {
        Id = id;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        ZOrder = zOrder;
        WantsKeyboard = wantsKeyboard;
    }

    public string Id { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int ZOrder { get; }
    public bool WantsKeyboard { get; }

    internal bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}
