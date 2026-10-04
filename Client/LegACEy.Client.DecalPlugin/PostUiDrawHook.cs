using System;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Intercepts Decal's exported EndSceneO forwarding slot, which its EndScene wrapper calls
/// after the game's retail UI and Decal's pre-EndScene subscribers have drawn. The slot is
/// restored on dispose, and a callback exception disables this seam only.
/// </summary>
internal sealed class PostUiDrawHook : IDisposable
{
    private readonly Action _draw;
    private readonly Action<Exception> _failed;
    private readonly EndSceneDelegate _callback;
    private readonly Func<IntPtr, IntPtr, bool> _writeSlot;
    private GCHandle _callbackRoot;
    private IntPtr _slot;
    private IntPtr _originalPointer;
    private EndSceneDelegate? _original;
    private bool _installed;
    private bool _inside;
    private bool _hasRun;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int EndSceneDelegate(IntPtr device);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string exportName);

    private const uint PageExecuteReadWrite = 0x40;

    public PostUiDrawHook(Action draw, Action<Exception> failed, Func<IntPtr, IntPtr, bool>? writeSlot = null)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        _callback = OnEndScene;
        _writeSlot = writeSlot ?? WriteSlot;
    }

    public bool IsInstalled => _installed;

    /// <summary>Whether the client has actually called this draw hook since installation.</summary>
    public bool HasRun => _hasRun;

    public bool Install()
    {
        if (_installed) return true;
        var inject = GetModuleHandle("Inject.dll");
        return inject != IntPtr.Zero && InstallForwardingSlot(GetProcAddress(inject, "EndSceneO"));
    }

    internal bool InstallForwardingSlot(IntPtr slot)
    {
        if (_installed) return true;
        // A failed removal must keep forwarding through the existing hook.
        if (_callbackRoot.IsAllocated) return false;
        if (slot == IntPtr.Zero) return false;
        _slot = slot;
        _originalPointer = Marshal.ReadIntPtr(_slot);
        if (_originalPointer == IntPtr.Zero) return false;
        _original = (EndSceneDelegate)Marshal.GetDelegateForFunctionPointer(_originalPointer, typeof(EndSceneDelegate));
        var callback = Marshal.GetFunctionPointerForDelegate(_callback);
        _callbackRoot = GCHandle.Alloc(_callback);
        if (!_writeSlot(_slot, callback))
        {
            _callbackRoot.Free();
            _slot = IntPtr.Zero;
            _original = null;
            return false;
        }
        _hasRun = false;
        _installed = true;
        return true;
    }

    private int OnEndScene(IntPtr device)
    {
        var original = _original;
        if (!_inside && _installed)
        {
            _hasRun = true;
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
                RestoreSlot();
            }
            finally
            {
                _inside = false;
            }
        }
        // Retry a failed removal on later frames, while still forwarding EndScene.
        if (!_installed && _callbackRoot.IsAllocated) RestoreSlot();
        return original == null ? 0 : original(device);
    }

    public void Dispose()
    {
        if (!_installed && _slot == IntPtr.Zero) return;
        _installed = false;
        RestoreSlot();
        GC.KeepAlive(_callback);
    }

    private void RestoreSlot()
    {
        if (!_callbackRoot.IsAllocated) return;
        try
        {
            // Another hook may now chain through ours; do not overwrite it or release our callback.
            if (Marshal.ReadIntPtr(_slot) != Marshal.GetFunctionPointerForDelegate(_callback)) return;
            if (!_writeSlot(_slot, _originalPointer)) return;
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

    private static bool WriteSlot(IntPtr slot, IntPtr value)
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
