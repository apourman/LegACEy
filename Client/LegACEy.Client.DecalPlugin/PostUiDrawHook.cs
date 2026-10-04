using System;
using System.Runtime.InteropServices;

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
    private readonly Func<IntPtr, IntPtr, bool> _writeVtable;
    private GCHandle _callbackRoot;
    private IntPtr _slot;
    private IntPtr _originalPointer;
    private EndSceneDelegate? _original;
    private bool _installed;
    private bool _inside;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int EndSceneDelegate(IntPtr device);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    private const uint PageExecuteReadWrite = 0x40;

    public PostUiDrawHook(Action draw, Action<Exception> failed, Func<IntPtr, IntPtr, bool>? writeVtable = null)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        _callback = OnEndScene;
        _writeVtable = writeVtable ?? WriteVtable;
    }

    public bool IsInstalled => _installed;

    public bool Install(IntPtr nativeDevice)
    {
        if (_installed) return true;
        // A failed removal must keep forwarding through the existing hook.
        if (_callbackRoot.IsAllocated) return false;
        if (nativeDevice == IntPtr.Zero) return false;
        var vtable = Marshal.ReadIntPtr(nativeDevice);
        if (vtable == IntPtr.Zero) return false;

        _slot = IntPtr.Add(vtable, EndSceneVtableIndex * IntPtr.Size);
        _originalPointer = Marshal.ReadIntPtr(_slot);
        if (_originalPointer == IntPtr.Zero) return false;
        _original = (EndSceneDelegate)Marshal.GetDelegateForFunctionPointer(_originalPointer, typeof(EndSceneDelegate));
        var callback = Marshal.GetFunctionPointerForDelegate(_callback);
        _callbackRoot = GCHandle.Alloc(_callback);
        if (!_writeVtable(_slot, callback))
        {
            _callbackRoot.Free();
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
                try
                {
                    _failed(exception);
                }
                catch
                {
                    // Failure reporting must never escape the unmanaged callback.
                }
                RestoreVtable();
            }
            finally
            {
                _inside = false;
            }
        }
        // Retry a failed removal on later frames, while still forwarding EndScene.
        if (!_installed && _callbackRoot.IsAllocated) RestoreVtable();
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
        if (!_callbackRoot.IsAllocated) return;
        try
        {
            if (!_writeVtable(_slot, _originalPointer)) return;
        }
        catch
        {
            // Keep the delegate rooted and the original callable until removal succeeds.
            return;
        }
        _slot = IntPtr.Zero;
        _original = null;
        _callbackRoot.Free();
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
