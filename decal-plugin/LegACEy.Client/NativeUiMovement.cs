using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Dispatch movement through the element so retail overrides also save character settings.</summary>
internal static class NativeUiMovement
{
    // Installed gmFloatyIndicatorsUI vtable: +0x2C points to 0x004D44F0.
    // That override calls base MoveTo and writes the panel's saved X/Y properties.
    internal const int MoveToVtableOffset = 0x2C;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void MoveToFn(IntPtr element, int x, int y);

    internal static void MoveTo(IntPtr element, Point location)
    {
        var move = (MoveToFn)Marshal.GetDelegateForFunctionPointer(ResolveMoveTo(element), typeof(MoveToFn));
        move(element, location.X, location.Y);
    }

    internal static IntPtr ResolveMoveTo(IntPtr element)
    {
        if (element == IntPtr.Zero) throw new InvalidOperationException("The native UI element is absent.");
        var vtable = Marshal.ReadIntPtr(element);
        if (vtable == IntPtr.Zero) throw new InvalidOperationException("The native UI element has no vtable.");
        var target = Marshal.ReadIntPtr(vtable, MoveToVtableOffset);
        if (target == IntPtr.Zero) throw new InvalidOperationException("The native UI element has no MoveTo method.");
        return target;
    }
}
