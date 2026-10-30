using System;
using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The player's vitals as retail's own vitals window reads them: CACQualities::InqAttribute2nd(type, out value, raw) on the player's
/// qualities. Not raw, maximums carry enchantments and vitae; raw, they are the base the character window compares against.
/// Checked against the running client once; Decal stands in when it does not match.
/// </summary>
internal static unsafe class NativeVitals
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>CACQualities::InqAttribute2nd (uint type, uint* value, int raw) -> int, thiscall, callee pops 12.</summary>
    private const uint InqAttribute2nd = 0x00592D20;
    /// <summary>ACCWeenieObject's CACQualities*, as every ClientObjMaintSystem quality update reads it.</summary>
    private const int WeenieQualities = 0x14C;

    // The prologue is left out: Decal patches prologues in the running client.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("CACQualities::InqAttribute2nd body: maximums 1, 3 and 5 take the attribute path", InqAttribute2nd + 4,
            NativeUiCatalogue.Bytes("8B 7C 24 14 33 DB 83 FF 01 8B E9 89 5C 24 0C 74 0A 83 FF 03 74 05 83 FF 05 75 21"), Source,
            "thiscall (CACQualities*, uint type, uint* value, int raw) -> int"),
        new NativeUiEntry("gmVitalsUI::Update reads health (2) then its maximum (1) through InqAttribute2nd, raw 0", 0x004C0B52,
            NativeUiCatalogue.Bytes("57 8D 4C 24 1C 51 6A 02 8B CD 89 7C 24 24 89 7C 24 1C E8 B7 21 0D 00 57 8D 54 24 14 52 6A 01 8B CD E8 A8 21 0D 00"),
            Source, "call reference"),
        new NativeUiEntry("ClientObjMaintSystem's quality update reads the weenie's CACQualities at +0x14C", 0x00558B05,
            NativeUiCatalogue.Bytes("8B 8E 4C 01 00 00"), Source, "instruction reference"),
        RetailHudCatalogue.WeenieLookup,
        RetailHudCatalogue.PlayerIdRead,
    };

    // Null until checked.
    private static bool? _ok;

    /// <summary>
    /// One vital by ACE's numbering (PropertyAttribute2nd): 1 max health, 2 health, 3 max stamina, 4 stamina, 5 max mana, 6 mana. Raw leaves
    /// out enchantments. False when the running client does not match or the player is not in the world yet, so the caller asks Decal.
    /// </summary>
    public static bool TryRead(uint type, bool raw, Action<string> log, out int value)
    {
        value = 0;
        if (_ok == null)
        {
            if (!NativeUi.Ready) return false;
            try { _ok = NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, "Native vitals are off; vitals come from Decal."); }
            catch (Exception exception)
            {
                log($"Native vitals are off: their addresses could not be checked: {exception.Message}");
                _ok = false;
            }
        }
        if (_ok != true) return false;
        var weenie = ((delegate* unmanaged[Cdecl]<uint, byte*>)RetailHudCatalogue.GetWeenieObject)(RetailHudCatalogue.PlayerId());
        var qualities = weenie == null ? null : *(byte**)(weenie + WeenieQualities);
        if (qualities == null) return false;
        uint read;
        if (((delegate* unmanaged[Thiscall]<byte*, uint, uint*, int, int>)InqAttribute2nd)(qualities, type, &read, raw ? 1 : 0) == 0) return false;
        value = (int)read;
        return true;
    }
}
