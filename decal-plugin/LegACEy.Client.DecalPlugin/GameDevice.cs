using System;
using System.Runtime.InteropServices;
using Decal.Adapter;
using Microsoft.DirectX.Direct3D;

namespace LegACEy.Client.DecalPlugin;

internal static class GameDevice
{
    private static readonly Guid Direct3DDevice9 = new("D0223B96-BF7A-43FD-92BD-A43B0D82B9EB");

    /// <summary>Wrap the game's own IDirect3DDevice9, which Decal hands out, for Managed DirectX.</summary>
    public static Device Open() => Open(out _);

    /// <summary>Open the game device and return its COM pointer for the post-UI hook seam.</summary>
    public static Device Open(out IntPtr nativePointer)
    {
        var iid = Direct3DDevice9;
        var device = CoreManager.Current.Decal.Underlying.GetD3DDevice(ref iid)
            ?? throw new InvalidOperationException("Decal has no Direct3D device yet.");
        var unknown = Marshal.GetIUnknownForObject(device);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out nativePointer));
            return new Device(nativePointer);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
