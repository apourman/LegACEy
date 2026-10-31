using System;
using System.Drawing;

namespace LegACEy.Client.DecalPlugin;

/// <summary>The game's own IDirect3DDevice9, which the EndScene hook reads, called through its vtable.</summary>
internal static class GameDevice
{
    /// <summary>The device's viewport size: the game's screen.</summary>
    public static Size ViewportSize(IntPtr device) => D3D9.Viewport(device).Size;
}
