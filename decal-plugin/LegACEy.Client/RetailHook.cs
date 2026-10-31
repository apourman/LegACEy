using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Places and removes the client's hooks on checked retail functions.</summary>
internal static class RetailHook
{
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
