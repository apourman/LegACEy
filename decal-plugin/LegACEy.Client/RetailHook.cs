using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Places and removes the client's hooks on checked retail functions.</summary>
internal static unsafe class RetailHook
{
    private const byte Jump = 0xE9;
    private const uint PageExecuteReadWrite = 0x40;

    /// <summary>
    /// When another hook (Decal's) starts the function at <paramref name="address"/> with a jump, points that jump at
    /// <paramref name="detour"/> and returns the other hook's function, which the detour calls on to retail; null when the function starts
    /// as retail's. Moving that jump's code, as Reloaded would, breaks the other hook; taking its place leaves it whole, and the client's
    /// hook still runs first.
    /// </summary>
    public static IntPtr? TakeJump(uint address, IntPtr detour)
    {
        if (*(byte*)address != Jump) return null;
        var next = (IntPtr)(address + 5 + *(int*)(address + 1));
        WriteJump(address, detour);
        return next;
    }

    /// <summary>Points a jump <see cref="TakeJump"/> took back at the other hook.</summary>
    public static void GiveJumpBack(uint address, IntPtr next, string name, Action<string> log)
    {
        try { WriteJump(address, next); }
        catch (InvalidOperationException exception) { log($"The {name} hook could not be removed; it stays and forwards: {exception.Message}"); }
    }

    private static void WriteJump(uint address, IntPtr target)
    {
        var offset = (IntPtr)(address + 1);
        if (!VirtualProtect(offset, (UIntPtr)4, PageExecuteReadWrite, out var old))
            throw new InvalidOperationException($"Could not unprotect 0x{address:X8}: error {Marshal.GetLastWin32Error()}.");
        try { *(int*)offset = (int)((uint)target - (address + 5)); }
        finally { VirtualProtect(offset, (UIntPtr)4, old, out _); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint oldProtection);

    /// <summary>Hooks <paramref name="address"/> once <paramref name="entries"/> check against the running client; null, with the reason logged, when they don't.</summary>
    public static IHook<T>? Place<T>(uint address, T callback, IReadOnlyList<NativeUiEntry> entries, string off, Action<string> log) where T : Delegate
    {
        if (!NativeUi.Ready) return null;
        try
        {
            if (!NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, entries, off)) return null;
            var hook = ReloadedHooks.Instance.CreateHook(callback, address);
            hook.Activate();
            return hook;
        }
        catch (Exception exception)
        {
            log($"{off} The hook could not be placed: {exception.Message}");
            return null;
        }
    }

    /// <summary>Removes a hook. One that can't be removed stays rooted, so retail can keep calling through it.</summary>
    public static void Remove<T>(IHook<T>? hook, string name, Action<string> log) where T : Delegate
    {
        if (hook == null) return;
        try { hook.Disable(); }
        catch (Exception exception)
        {
            GCHandle.Alloc(hook);
            log($"The {name} hook could not be removed; it stays and forwards to retail: {exception.Message}");
        }
    }
}
