using System;
using System.Drawing;

namespace LegACEy.Client.DecalPlugin;

/// <summary>The game's own IDirect3DDevice9, which the stub reads from Decal, called through its vtable.</summary>
internal static unsafe class GameDevice
{
    // IDirect3DDevice9::GetViewport, checked against mingw d3d9.h.
    private const int GetViewportSlot = 48;

    public static IntPtr Open()
    {
        var device = Bridge.Device();
        return device != IntPtr.Zero ? device : throw new InvalidOperationException("Decal has no Direct3D device yet.");
    }

    /// <summary>The device's viewport size: the game's screen.</summary>
    public static Size ViewportSize(IntPtr device)
    {
        // D3DVIEWPORT9: X, Y, Width, Height, then MinZ and MaxZ.
        var viewport = stackalloc uint[6];
        var getViewport = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)(*(void***)device)[GetViewportSlot];
        var result = getViewport(device, viewport);
        if (result < 0) throw new InvalidOperationException($"IDirect3DDevice9::GetViewport failed: 0x{result:X8}.");
        return new Size((int)viewport[2], (int)viewport[3]);
    }
}
