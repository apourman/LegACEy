using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The player's combat stance, read from ClientCombatSystem's combat mode (+0x1C), which retail stores as 1 peace, 2 melee, 4 missile and
/// 8 magic: the values of <see cref="RetailStance"/> (RynthCore's CombatModeHooks reads it the same way). Checked against the running client
/// once at start; when it does not match, the stance reads as peace.
/// </summary>
internal static unsafe class NativeStance
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>The combat mode (int32) in ClientCombatSystem.</summary>
    private const int ModeOffset = 0x1C;

    // ToggleCombatMode's prologue is left out: Decal patches prologues in the running client. Its compare of the mode with peace (1) ties
    // the offset.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        RetailToolbarCatalogue.CombatSystemGetter,
        new NativeUiEntry("ClientCombatSystem::ToggleCombatMode compares the mode at +0x1C with peace (1)", RetailToolbarCatalogue.ToggleCombatMode + 3,
            NativeUiCatalogue.Bytes("83 7E 1C 01"), Source, "instruction reference (cmp dword ptr [esi+0x1C], 1)"),
    };

    private static bool _ok;

    /// <summary>Checks the running client once, after <see cref="NativeUi.Initialize"/>. A mismatch is logged once and leaves the stance at peace.</summary>
    public static void Initialize(Action<string> log)
    {
        if (!NativeUi.Ready) return;
        try { _ok = NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, "Native combat stance is off; LegACEy windows read peace."); }
        catch (Exception exception) { log($"Native combat stance is off: its address could not be checked: {exception.Message}"); }
    }

    /// <summary>The player's stance; peace outside the game (no combat system yet) and when the running client does not match.</summary>
    public static RetailStance Stance
    {
        get
        {
            if (!_ok) return RetailStance.Peace;
            var system = ((delegate* unmanaged[Cdecl]<byte*>)RetailToolbarCatalogue.GetCombatSystem)();
            return system == null ? RetailStance.Peace : (RetailStance)(*(int*)(system + ModeOffset));
        }
    }
}
