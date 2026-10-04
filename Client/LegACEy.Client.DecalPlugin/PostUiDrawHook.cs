using System;
using System.Runtime.InteropServices;
using Microsoft.DirectX.Direct3D;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Installs the one post-UI draw seam used by LegACEy windows. IDirect3DDevice9.EndScene is
/// called after the game's retail UI and Decal's RenderFrame subscribers have drawn. The vtable
/// slot is restored on dispose, and a callback exception disables this seam only.
/// </summary>
internal sealed class PostUiDrawHook : IDisposable
{
    private const int EndSceneVtableIndex = 42;
    private readonly Action _draw;
    private readonly Action<Exception> _failed;
    private readonly EndSceneDelegate _callback;
    private IntPtr _slot;
    private IntPtr _originalPointer;
    private EndSceneDelegate? _original;
    private bool _installed;
    private bool _inside;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndSceneDelegate(IntPtr device);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    private const uint PageExecuteReadWrite = 0x40;

    public PostUiDrawHook(Action draw, Action<Exception> failed)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        _callback = OnEndScene;
    }

    public bool IsInstalled => _installed;

    public bool Install(IntPtr nativeDevice)
    {
        if (_installed) return true;
        if (nativeDevice == IntPtr.Zero) return false;
        var vtable = Marshal.ReadIntPtr(nativeDevice);
        if (vtable == IntPtr.Zero) return false;

        _slot = IntPtr.Add(vtable, EndSceneVtableIndex * IntPtr.Size);
        _originalPointer = Marshal.ReadIntPtr(_slot);
        if (_originalPointer == IntPtr.Zero) return false;
        _original = (EndSceneDelegate)Marshal.GetDelegateForFunctionPointer(_originalPointer, typeof(EndSceneDelegate));
        var callback = Marshal.GetFunctionPointerForDelegate(_callback);
        if (!WriteVtable(_slot, callback))
        {
            _slot = IntPtr.Zero;
            _original = null;
            return false;
        }
        _installed = true;
        return true;
    }

    private int OnEndScene(IntPtr device)
    {
        var original = _original;
        if (!_inside && _installed)
        {
            _inside = true;
            try
            {
                _draw();
            }
            catch (Exception exception)
            {
                _installed = false;
                _failed(exception);
                RestoreVtable();
            }
            finally
            {
                _inside = false;
            }
        }
        return original == null ? 0 : original(device);
    }

    public void Dispose()
    {
        if (!_installed && _slot == IntPtr.Zero) return;
        _installed = false;
        RestoreVtable();
        GC.KeepAlive(_callback);
    }

    private void RestoreVtable()
    {
        if (_slot != IntPtr.Zero && _originalPointer != IntPtr.Zero)
            WriteVtable(_slot, _originalPointer);
        _slot = IntPtr.Zero;
        _original = null;
    }

    private static bool WriteVtable(IntPtr slot, IntPtr value)
    {
        if (!VirtualProtect(slot, (UIntPtr)IntPtr.Size, PageExecuteReadWrite, out var oldProtect))
            return false;
        try
        {
            Marshal.WriteIntPtr(slot, value);
            return true;
        }
        finally
        {
            VirtualProtect(slot, (UIntPtr)IntPtr.Size, oldProtect, out _);
        }
    }
}
