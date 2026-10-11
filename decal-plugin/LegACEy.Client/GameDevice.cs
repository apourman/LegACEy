using System;
using System.Drawing;

namespace LegACEy.Client.DecalPlugin;

/// <summary>The game's own IDirect3DDevice9, which the stub reads from Decal, called through its vtable.</summary>
internal static class GameDevice
{
    public static IntPtr Open()
    {
        var device = Bridge.Device();
        return device != IntPtr.Zero ? device : throw new InvalidOperationException("Decal has no Direct3D device yet.");
    }

    /// <summary>The device's viewport size: the game's screen.</summary>
    public static Size ViewportSize(IntPtr device) => D3D9.Viewport(device).Size;
}
