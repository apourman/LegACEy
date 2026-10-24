using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Holds entering the world while a world update is pending. Two retail functions are held, each on its own:
/// <list type="bullet">
/// <item><c>gmCharacterManagementUI::EnterGame</c>, which character select's Enter button and a double click on a character call. Held
/// there, the player stays at character select with no Entering World dialog.</item>
/// <item><c>CPlayerSystem::LogOnCharacter</c>, which every way into the world ends in: EnterGame, finishing character creation, and the
/// exported login that Decal and auto-login filters call. Held there, it says it did not log on.</item>
/// </list>
/// While <c>hold</c> says so the call is swallowed and <c>held</c> runs instead; otherwise it passes through untouched. Called on the game thread.
/// </summary>
internal sealed class RetailEnterGame : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xC90 at 0x004EDxxx, +0xD70 at 0x0055Fxxx) for names; our objdump read of installed acclient.exe";
    private const uint EnterGameAddress = 0x004EE0D0, LogOnCharacterAddress = 0x00560600;

    internal static readonly IReadOnlyList<NativeUiEntry> EnterGameEntries = new[]
    {
        // The prologue too, where the hook goes: GetPlayerSystem, the selected character at +0xB8, then the checks before the dialog.
        new NativeUiEntry("gmCharacterManagementUI::EnterGame", EnterGameAddress,
            NativeUiCatalogue.Bytes("53 56 8B F1 E8 F7 00 07 00 8B D8 85 DB 74 47 8B 86 B8 00 00 00 85 C0 74 3D"), Source,
            "ThisCall (gmCharacterManagementUI*), void: shows Entering World and calls CPlayerSystem::LogOnCharacter"),
    };

    internal static readonly IReadOnlyList<NativeUiEntry> LogOnEntries = new[]
    {
        // The prologue too, where the hook goes: the connected flag at +0x210, then the pending character kept for later at +0x4C.
        new NativeUiEntry("CPlayerSystem::LogOnCharacter", LogOnCharacterAddress,
            NativeUiCatalogue.Bytes("56 8B F1 8A 86 10 02 00 00 84 C0 57 75 3E 8B 0D 2C E7 83 00"), Source,
            "ThisCall (CPlayerSystem*, uint character) -> bool, ret 4; callers 0x004EE121, 0x004E9234, 0x0055AD77, 0x00563EFF"),
    };

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate void EnterGameFn(IntPtr ui);

    // A byte, not bool: retail returns in AL and leaves the rest of EAX as it was.
    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate byte LogOnCharacterFn(IntPtr playerSystem, uint character);

    private readonly Func<bool> _hold;
    private readonly Action _held;
    private readonly Action<string> _log;
    private IHook<EnterGameFn>? _enterGame;
    private IHook<LogOnCharacterFn>? _logOn;
    private GCHandle _root;
    // Set by Dispose: a hook that could not be removed lets everything through.
    private volatile bool _off;

    private RetailEnterGame(Func<bool> hold, Action held, Action<string> log)
    {
        _hold = hold;
        _held = held;
        _log = log;
        _root = GCHandle.Alloc(this);
    }

    /// <summary>
    /// The holds whose entries match the running client and whose hooks could be placed, each logged when it can't; null when neither could,
    /// and entering the world then always works.
    /// </summary>
    public static RetailEnterGame? Create(Func<uint, int, byte[]> readMemory, Func<bool> hold, Action held, Action<string> log)
    {
        var holds = new RetailEnterGame(hold, held, log);
        if (NativeUiCatalogue.Validate(readMemory, log, EnterGameEntries,
                "Character select's Enter isn't held for a world update: the running client does not match its entry."))
            holds._enterGame = holds.Place<EnterGameFn>(holds.OnEnterGame, EnterGameAddress, "Character select's Enter");
        if (NativeUiCatalogue.Validate(readMemory, log, LogOnEntries,
                "Logging a character on isn't held for a world update: the running client does not match its entry."))
            holds._logOn = holds.Place<LogOnCharacterFn>(holds.OnLogOnCharacter, LogOnCharacterAddress, "Logging a character on");
        if (holds._enterGame != null || holds._logOn != null) return holds;
        holds._root.Free();
        return null;
    }

    private IHook<T>? Place<T>(T callback, uint address, string what)
    {
        try
        {
            var hook = ReloadedHooks.Instance.CreateHook(callback, address);
            hook.Activate();
            return hook;
        }
        catch (Exception exception)
        {
            _log($"{what} isn't held for a world update: {exception.Message}");
            return null;
        }
    }

    private void OnEnterGame(IntPtr ui)
    {
        if (Hold("Enter held at character select")) return;
        _enterGame!.OriginalFunction(ui);
    }

    private byte OnLogOnCharacter(IntPtr playerSystem, uint character) =>
        Hold($"Logging character 0x{character:X8} on held") ? (byte)0 : _logOn!.OriginalFunction(playerSystem, character);

    /// <summary>Whether to swallow the call; when it does, says why and runs <c>held</c>.</summary>
    private bool Hold(string what)
    {
        try
        {
            if (_off || !_hold()) return false;
        }
        catch (Exception exception)
        {
            Report($"Could not ask whether to hold entering the world; letting it through: {exception.Message}");
            return false;
        }
        Report($"{what}: a world update is pending.");
        try { _held(); }
        catch (Exception exception) { Report($"Could not show the world update toast: {exception.Message}"); }
        return true;
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
        try
        {
            _enterGame?.Disable();
            _logOn?.Disable();
        }
        catch (Exception exception)
        {
            // Still callable: stays rooted and keeps forwarding.
            Report($"Could not remove the world update holds; they let everything through from now on: {exception.Message}");
            return;
        }
        if (_root.IsAllocated) _root.Free();
    }
}
