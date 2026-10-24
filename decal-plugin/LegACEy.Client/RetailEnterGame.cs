using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Holds character select's Enter while a world update is pending. Retail's Enter button and a double click on a character both call
/// <c>gmCharacterManagementUI::EnterGame</c>, which puts up the Entering World dialog and logs the selected character on. While
/// <c>hold</c> says so the call is swallowed and <c>held</c> runs instead, so the player stays at character select; otherwise it passes
/// through untouched. Called on the game thread.
/// </summary>
internal sealed class RetailEnterGame : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xC90 here) for names; our objdump read of installed acclient.exe";
    private const uint EnterGameAddress = 0x004EE0D0;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // The prologue too, where the hook goes: GetPlayerSystem, the selected character at +0xB8, then the checks before the dialog.
        new NativeUiEntry("gmCharacterManagementUI::EnterGame", EnterGameAddress,
            NativeUiCatalogue.Bytes("53 56 8B F1 E8 F7 00 07 00 8B D8 85 DB 74 47 8B 86 B8 00 00 00 85 C0 74 3D"), Source,
            "ThisCall (gmCharacterManagementUI*), void: shows Entering World and calls CPlayerSystem::LogOnCharacter (0x00560600)"),
    };

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate void EnterGameFn(IntPtr ui);

    private readonly Func<bool> _hold;
    private readonly Action _held;
    private readonly Action<string> _log;
    private readonly IHook<EnterGameFn> _hook;
    private GCHandle _root;
    // Set by Dispose: a hook that could not be removed lets every Enter through.
    private volatile bool _off;

    private RetailEnterGame(Func<bool> hold, Action held, Action<string> log)
    {
        _hold = hold;
        _held = held;
        _log = log;
        _root = GCHandle.Alloc(this);
        try
        {
            _hook = ReloadedHooks.Instance.CreateHook<EnterGameFn>(OnEnterGame, EnterGameAddress);
            _hook.Activate();
        }
        catch
        {
            _root.Free();
            throw;
        }
    }

    /// <summary>The hold, or null, logged, when the running client doesn't match its entry or the hook can't be placed: Enter then always works.</summary>
    public static RetailEnterGame? Create(Func<uint, int, byte[]> readMemory, Func<bool> hold, Action held, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries,
                "Enter at character select is never held for a world update: the running client does not match its entry."))
            return null;
        try { return new RetailEnterGame(hold, held, log); }
        catch (Exception exception)
        {
            log($"Enter at character select is never held for a world update: {exception.Message}");
            return null;
        }
    }

    private void OnEnterGame(IntPtr ui)
    {
        bool hold;
        try { hold = !_off && _hold(); }
        catch (Exception exception)
        {
            Report($"Could not ask whether to hold Enter; letting it through: {exception.Message}");
            hold = false;
        }
        if (!hold)
        {
            _hook.OriginalFunction(ui);
            return;
        }
        Report("Enter held at character select: a world update is pending.");
        try { _held(); }
        catch (Exception exception) { Report($"Could not show the world update toast: {exception.Message}"); }
    }

    /// <summary>Reporting runs inside the native callback, so it must never throw.</summary>
    private void Report(string message)
    {
        try { _log(message); }
        catch { /* Reporting must never escape the native callback. */ }
    }

    public void Dispose()
    {
        _off = true;
        try { _hook.Disable(); }
        catch (Exception exception)
        {
            // Still callable: stays rooted and keeps forwarding.
            Report($"Could not remove the Enter hold; it lets Enter through from now on: {exception.Message}");
            return;
        }
        if (_root.IsAllocated) _root.Free();
    }
}
