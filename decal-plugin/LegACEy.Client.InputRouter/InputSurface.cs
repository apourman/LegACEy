using System;

namespace LegACEy.Client.InputRouter;

/// <summary>A visible panel snapshot used for screen-space hit testing.</summary>
public sealed class InputSurface
{
    private readonly Func<int, bool>? _acceptsShortcut;

    /// <param name="wantsKeyboard">The surface takes every key while it has the focus: a text entry has it.</param>
    /// <param name="acceptsShortcut">The virtual keys the surface takes while it has the focus without wanting the keyboard. Other keys go to the game.</param>
    public InputSurface(string id, int x, int y, int width, int height, int zOrder, bool wantsKeyboard = false, Func<int, bool>? acceptsShortcut = null)
    {
        Id = id;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        ZOrder = zOrder;
        WantsKeyboard = wantsKeyboard;
        _acceptsShortcut = acceptsShortcut;
    }

    public string Id { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int ZOrder { get; }
    public bool WantsKeyboard { get; }

    internal bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    /// <summary>Whether a key with this virtual key is the surface's: every key while it wants the keyboard, otherwise its shortcuts.</summary>
    internal bool AcceptsKey(int virtualKey) => WantsKeyboard || _acceptsShortcut?.Invoke(virtualKey) == true;
}
