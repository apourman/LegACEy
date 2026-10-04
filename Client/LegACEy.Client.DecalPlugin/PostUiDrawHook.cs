using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Draws before the checked retail RenderDeviceD3D.EndScene function.</summary>
internal sealed class PostUiDrawHook : IDisposable
{
    internal static readonly byte[] Signature = { 0x56, 0x8b, 0xf1, 0x8a, 0x86, 0xac, 0, 0, 0, 0x84, 0xc0, 0x74, 0x16 };
    private readonly Action _draw;
    private readonly Action<Exception> _failed;
    private IHook<EndSceneDelegate>? _hook;
    private GCHandle _root;
    private bool _installed;
    private bool _inside;
    private bool _hasRun;

    [Function(CallingConventions.MicrosoftThiscall)]
    internal delegate void EndSceneDelegate(IntPtr renderDevice);

    public PostUiDrawHook(Action draw, Action<Exception> failed)
    {
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
    }

    public bool IsInstalled => _installed;
    public bool HasRun => _hasRun;

    public bool Install()
    {
        if (_installed) return true;
        var module = Process.GetCurrentProcess().MainModule!;
        var rva = FindEndSceneRva(File.ReadAllBytes(module.FileName));
        return rva.HasValue && InstallAt(IntPtr.Add(module.BaseAddress, rva.Value));
    }

    internal bool InstallAt(IntPtr entry)
    {
        if (IntPtr.Size != 4) return false;
        for (var i = 0; i < Signature.Length; i++)
            if (Marshal.ReadByte(entry, i) != Signature[i]) return false;
        _root = GCHandle.Alloc(this);
        try
        {
            _hook = ReloadedHooks.Instance.CreateHook<EndSceneDelegate>(OnEndScene, entry.ToInt64());
            _installed = true;
            _hasRun = false;
            _hook.Activate();
            return true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void OnEndScene(IntPtr renderDevice)
    {
        var original = _hook!.OriginalFunction;
        if (!_inside && _installed)
        {
            _inside = true;
            _hasRun = true;
            try { _draw(); }
            catch (Exception exception)
            {
                try { _failed(exception); }
                catch { /* Reporting must never escape the native callback. */ }
                Dispose();
            }
            finally { _inside = false; }
        }
        // Reloaded keeps the trampoline callable even after Disable().
        original(renderDevice);
    }

    public void Dispose()
    {
        _installed = false;
        try
        {
            // Disable redirects entirely through native code, preserving other detour chains.
            _hook?.Disable();
        }
        catch
        {
            // A still-callable managed callback must remain rooted and keep forwarding.
            return;
        }
        if (_root.IsAllocated) _root.Free();
    }

    /// <summary>Require one signature in executable PE sections; never trust a PDB address.</summary>
    internal static int? FindEndSceneRva(byte[] image)
    {
        if (image.Length < 64 || image[0] != 'M' || image[1] != 'Z') return null;
        var pe = BitConverter.ToInt32(image, 0x3c);
        if (pe < 0 || pe > image.Length - 24 || BitConverter.ToInt32(image, pe) != 0x4550 ||
            BitConverter.ToUInt16(image, pe + 4) != 0x14c) return null;
        var count = BitConverter.ToUInt16(image, pe + 6);
        var sections = pe + 24 + BitConverter.ToUInt16(image, pe + 20);
        int? found = null;
        for (var section = 0; section < count; section++)
        {
            var header = sections + section * 40;
            if (header > image.Length - 40) return null;
            if ((BitConverter.ToUInt32(image, header + 36) & 0x20000000) == 0) continue;
            var rva = BitConverter.ToInt32(image, header + 12);
            var size = BitConverter.ToInt32(image, header + 16);
            var offset = BitConverter.ToInt32(image, header + 20);
            if (offset < 0 || size < 0 || offset > image.Length - size) return null;
            for (var pos = offset; pos <= offset + size - Signature.Length; pos++)
            {
                var matches = true;
                for (var i = 0; i < Signature.Length && matches; i++) matches = image[pos + i] == Signature[i];
                if (!matches) continue;
                if (found.HasValue) return null;
                found = checked(rva + pos - offset);
            }
        }
        return found;
    }
}
