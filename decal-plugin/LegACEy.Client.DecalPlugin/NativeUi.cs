using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Calls into the retail client's own UI system (UIElementManager and UIElement).
/// </summary>
/// <remarks>
/// The addresses are for the end-of-retail acclient.exe and come from Chorizite's AcClient bindings
/// (github.com/Chorizite/Chorizite, NativeClientBootstrapper/AcClient/UIRegion.cs). Each one was
/// checked against the installed acclient.exe: every address starts a function, and they read
/// UIElementManager::s_pInstance at 0x0083E03C. Call these only on the game thread while in game.
/// </remarks>
internal static class NativeUi
{
    /// <summary>Root element ids, from Chorizite.Common's RootElementId.</summary>
    public const uint Indicators = 0x10000611;
    public const uint CharacterInfo = 0x10000183;
    public const uint PositiveEffects = 0x10000184;
    public const uint NegativeEffects = 0x10000185;
    public const uint LinkStatus = 0x10000187;
    public const uint MiniGame = 0x10000188;
    public const uint Vitae = 0x1000018A;

    private static readonly IntPtr ManagerInstance = new(0x0083E03C);
    private static bool _ready;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetElementFn(IntPtr manager, uint id);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte IsVisibleFn(IntPtr element);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetVisibleFn(IntPtr element, byte visible);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendNoticeEndCharacterSessionFn(int confirm);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GetCurrentPositionFn(IntPtr element, out Box2D position, out int zLevel);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte LockUiFn(IntPtr playerModule);

    [StructLayout(LayoutKind.Sequential)]
    private struct Box2D
    {
        public int X0;
        public int Y0;
        public int X1;
        public int Y1;
    }

    private static readonly GetElementFn GetElementNative = Function<GetElementFn>(0x00459A00);
    private static readonly IsVisibleFn IsVisibleNative = Function<IsVisibleFn>(0x004603A0);
    private static readonly SetVisibleFn SetVisibleNative = Function<SetVisibleFn>(0x00462390);
    private static readonly GetCurrentPositionFn GetCurrentPosition = Function<GetCurrentPositionFn>(0x00460180);
    private static readonly SetVisibleFn SetSaveLocationNative = Function<SetVisibleFn>(0x0045FA10);
    private static readonly SendNoticeEndCharacterSessionFn SendNoticeEndCharacterSession = Function<SendNoticeEndCharacterSessionFn>(0x00479F40);
    private static readonly LockUiFn LockUiNative = Function<LockUiFn>(0x005D4330);

    public static bool Ready => _ready;

    public static bool Initialize(Action<string> log)
    {
        try
        {
            var module = Process.GetCurrentProcess().MainModule!;
            if (IntPtr.Size != 4 || module.BaseAddress.ToInt32() != 0x00400000)
            {
                log("Retail takeover disabled: unsupported acclient architecture or image base.");
                return _ready = false;
            }
            _ready = NativeUiCatalogue.Validate(ReadMemory, log);
            return _ready;
        }
        catch (Exception exception)
        {
            _ready = false;
            log($"Retail takeover disabled: native UI validation could not complete: {exception.Message}");
            return false;
        }
    }

    /// <summary>The client's UI element with this id, or zero if the UI or the element doesn't exist.</summary>
    public static IntPtr GetElement(uint id)
    {
        EnsureReady();
        var manager = Marshal.ReadIntPtr(ManagerInstance);
        return manager == IntPtr.Zero ? IntPtr.Zero : GetElementNative(manager, id);
    }

    public static bool IsVisible(IntPtr element) { EnsureReady(); return IsVisibleNative(element) != 0; }

    public static void SetVisible(IntPtr element, bool visible) { EnsureReady(); SetVisibleNative(element, visible ? (byte)1 : (byte)0); }

    /// <summary>The element's rectangle in screen pixels.</summary>
    public static Rectangle GetBounds(IntPtr element)
    {
        EnsureReady();
        GetCurrentPosition(element, out var box, out _);
        return Rectangle.FromLTRB(box.X0, box.Y0, box.X1, box.Y1);
    }

    /// <summary>Enable retail layout saving before moving a replacement's native element.</summary>
    public static void SetSaveLocation(IntPtr element, bool save) { EnsureReady(); SetSaveLocationNative(element, save ? (byte)1 : (byte)0); }

    /// <summary>Move through the element's retail override, including its native saved-position writeback.</summary>
    public static void MoveTo(IntPtr element, Point location) { EnsureReady(); NativeUiMovement.MoveTo(element, location); }

    /// <summary>Show a hidden root element or hide a shown one, as its retail indicator button does.</summary>
    public static void ToggleRootElement(uint id)
    {
        var element = GetElement(id);
        if (element != IntPtr.Zero)
            SetVisible(element, !IsVisible(element));
    }

    /// <summary>
    /// Ask to log out the way the retail bar's X button does: gmFloatyIndicatorsUI handles a click
    /// on element 0x100000FA by calling CM_UI::SendNotice_EndCharacterSession(1), and the game-play
    /// UI answers that notice with the retail log out confirmation dialog.
    /// </summary>
    public static void RequestLogOut() { EnsureReady(); SendNoticeEndCharacterSession(1); }

    public static bool IsUiLocked
    {
        get
        {
            EnsureReady();
            var playerSystem = Marshal.ReadIntPtr(new IntPtr(0x0087119C));
            if (playerSystem == IntPtr.Zero) return false;
            // CPlayerModule starts at +0x30, but its PlayerModule base starts four bytes later.
            // Retail drag handlers pass playerSystem +0x34 to PlayerModule::LockUI.
            return LockUiNative(IntPtr.Add(playerSystem, 0x34)) != 0;
        }
    }

    private static void EnsureReady()
    {
        if (!_ready) throw new InvalidOperationException("The retail native UI catalogue has not passed validation.");
    }

    private static byte[] ReadMemory(uint address, int count)
    {
        var bytes = new byte[count];
        if (!ReadProcessMemory(GetCurrentProcess(), new IntPtr(unchecked((int)address)), bytes, count, out var read) || read.ToInt32() != count)
            throw new InvalidOperationException($"Could not read native address 0x{address:X8}.");
        return bytes;
    }

    private static T Function<T>(int address) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(new IntPtr(address), typeof(T));
}
