using System;
using System.Drawing;
using System.Runtime.InteropServices;

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

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetElementFn(IntPtr manager, uint id);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte IsVisibleFn(IntPtr element);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetVisibleFn(IntPtr element, byte visible);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GetCurrentPositionFn(IntPtr element, out Box2D position, out int zLevel);

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

    /// <summary>The client's UI element with this id, or zero if the UI or the element doesn't exist.</summary>
    public static IntPtr GetElement(uint id)
    {
        var manager = Marshal.ReadIntPtr(ManagerInstance);
        return manager == IntPtr.Zero ? IntPtr.Zero : GetElementNative(manager, id);
    }

    public static bool IsVisible(IntPtr element) => IsVisibleNative(element) != 0;

    public static void SetVisible(IntPtr element, bool visible) => SetVisibleNative(element, visible ? (byte)1 : (byte)0);

    /// <summary>The element's rectangle in screen pixels.</summary>
    public static Rectangle GetBounds(IntPtr element)
    {
        GetCurrentPosition(element, out var box, out _);
        return Rectangle.FromLTRB(box.X0, box.Y0, box.X1, box.Y1);
    }

    /// <summary>Show a hidden root element or hide a shown one, as its retail indicator button does.</summary>
    public static void ToggleRootElement(uint id)
    {
        var element = GetElement(id);
        if (element != IntPtr.Zero)
            SetVisible(element, !IsVisible(element));
    }

    private static T Function<T>(int address) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(new IntPtr(address), typeof(T));
}
