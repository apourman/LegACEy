using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The game window's messages, from a subclass of its window procedure. The client subclasses once the window exists, which is after
/// Decal's own subclass when Decal loads it, so LegACEy sees each message first and a message it eats never reaches Decal's plugins or the
/// game. The ANSI calls keep the window ANSI, as retail made it, so typed characters arrive as they did through Decal.
/// </summary>
internal static unsafe class GameWindowHook
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build) for names; our objdump read of installed acclient.exe";
    /// <summary>Device::m_hWnd: the game window, which Device::WndProc serves.</summary>
    private const uint WindowGlobal = 0x008381A4;
    private const int GwlWndProc = -4;

    private static readonly NativeUiEntry[] Entries =
    {
        new("Device::m_hWnd read", 0x004391B4, NativeUiCatalogue.Bytes("8B 0D A4 81 83 00"), Source, "data reference (absolute VA)"),
    };

    /// <summary>(hwnd, message, wParam, lParam), true to eat the message.</summary>
    internal delegate bool MessageHandler(int window, int message, int wParam, int lParam);

    private static MessageHandler? _message;
    private static Action<Exception>? _failed;
    private static IntPtr _window, _previous;
    private static bool _checked;

    /// <summary>
    /// Subclasses the game window, once it exists. False while there is no window yet, so it is asked again; true once subclassed, and once
    /// it can't be (logged).
    /// </summary>
    public static bool TryPlace(MessageHandler message, Action<Exception> failed, Action<string> log)
    {
        if (!_checked)
        {
            if (!NativeUi.Ready || !NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries,
                    "LegACEy windows take no input: the running client does not match the game window's entries.")) return true;
            _checked = true;
        }
        var window = *(IntPtr*)WindowGlobal;
        if (window == IntPtr.Zero) return false;
        _message = message;
        _failed = failed;
        if (_window == window && _previous != IntPtr.Zero) return true;
        var previous = SetWindowLongA(window, GwlWndProc, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, IntPtr>)&WndProc);
        if (previous == IntPtr.Zero)
        {
            log($"LegACEy windows take no input: the game window could not be subclassed (error {Marshal.GetLastWin32Error()}).");
            _message = null;
            return true;
        }
        (_window, _previous) = (window, previous);
        return true;
    }

    /// <summary>Stops handling messages. The subclass is undone when nothing subclassed the window after it, and otherwise only forwards.</summary>
    public static void Remove()
    {
        _message = null;
        _failed = null;
        if (_previous == IntPtr.Zero || GetWindowLongA(_window, GwlWndProc) != (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, IntPtr>)&WndProc) return;
        SetWindowLongA(_window, GwlWndProc, _previous);
        (_window, _previous) = (IntPtr.Zero, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        if (_message is { } handle)
        {
            try
            {
                if (handle((int)window, message, (int)wParam, (int)lParam)) return IntPtr.Zero;
            }
            catch (Exception exception)
            {
                try { _failed?.Invoke(exception); }
                catch { /* Reporting must never escape the window procedure. */ }
            }
        }
        return CallWindowProcA(_previous, window, message, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongA(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongA(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcA(IntPtr previous, IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
