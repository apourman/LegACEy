using System;
using System.Collections.Generic;
using System.Text;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// What the Dereth magic window asks of retail itself: adding a spell to the spell bar's current tab, as a double click in retail's spellbook
/// does (gmSpellbookUI sends CM_Magic::SendNotice_AddSpellShortcut, which retail's spellcasting UI takes), and the account name, which
/// picks the tapers in the player's spell formulas (ClientMagicSystem::GetAppropriateSpellFormula reads it from CPlayerSystem + 0x1C).
/// Checked against the running client once; with a mismatch the spell bar is left to retail and formulas show the table's tapers.
/// </summary>
internal static unsafe class MagicNatives
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb (near build)";
    /// <summary>CM_Magic::SendNotice_AddSpellShortcut (uint spell) -> bool, cdecl.</summary>
    private const uint SendAddSpellShortcut = 0x006A4190;
    /// <summary>CPlayerSystem::GetPlayerSystem () -> CPlayerSystem*, cdecl.</summary>
    private const uint GetPlayerSystem = 0x0055E1D0;
    /// <summary>The account name's accountID in CPlayerSystem: its first word points at a string buffer, whose characters start at + 0x14.</summary>
    private const int AccountOffset = 0x1C, StringData = 0x14;

    // The notice's prologue is left out: Decal patches prologues in the running client.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("CM_Magic::SendNotice_AddSpellShortcut body: each handler's RecvNotice_AddSpellShortcut (+0x168)", SendAddSpellShortcut + 5,
            NativeUiCatalogue.Bytes("8B 10 68 02 D2 4D 00 8B C8 FF 52 10 85 C0 74 33 56 8B 70 04 85 F6 74 2A 53 8B 5C 24 0C 57 8B 3E"), Source,
            "cdecl (uint spell) -> bool"),
        new NativeUiEntry("gmSpellbookUI's double click sends SendNotice_AddSpellShortcut with the row's spell", 0x0048C31A,
            NativeUiCatalogue.Bytes("8B 80 00 06 00 00 85 C0 0F 84 A8 00 00 00 50 E8 62 7E 21 00"), Source, "call reference"),
        new NativeUiEntry("CPlayerSystem::GetPlayerSystem", GetPlayerSystem, NativeUiCatalogue.Bytes("A1 9C 11 87 00 C3"), Source,
            "cdecl () -> CPlayerSystem*, read from 0x0087119C"),
        new NativeUiEntry("GetAppropriateSpellFormula takes the account name at CPlayerSystem + 0x1C", 0x00568B29,
            NativeUiCatalogue.Bytes("83 C7 1C 57"), Source, "instruction reference"),
    };

    // Null until checked.
    private static bool? _ok;

    private static bool Ok(Action<string> log)
    {
        if (_ok != null) return _ok.Value;
        if (!NativeUi.Ready) return false;
        try { _ok = NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, "The magic natives are off: the spell bar stays retail's own."); }
        catch (Exception exception)
        {
            log($"The magic natives are off: their addresses could not be checked: {exception.Message}");
            _ok = false;
        }
        return _ok.Value;
    }

    /// <summary>Adds the spell to the spell bar's current tab, as retail's double click does. False when the client does not match.</summary>
    public static bool AddToSpellBar(uint spell, Action<string> log) =>
        Ok(log) && ((delegate* unmanaged[Cdecl]<uint, byte>)SendAddSpellShortcut)(spell) != 0;

    /// <summary>The account name the player logged in with, or empty when the client does not match or has none yet.</summary>
    public static string AccountName(Action<string> log)
    {
        if (!Ok(log)) return string.Empty;
        var system = ((delegate* unmanaged[Cdecl]<byte*>)GetPlayerSystem)();
        if (system == null) return string.Empty;
        var buffer = *(byte**)(system + AccountOffset);
        if (buffer == null) return string.Empty;
        var text = buffer + StringData;
        var name = new StringBuilder();
        // Account names are short; anything past 64 characters is not one.
        for (var i = 0; i < 64 && text[i] != 0; i++) name.Append(LegACEy.Client.GameArt.Windows1252.ToChar(text[i]));
        return name.Length == 64 ? string.Empty : name.ToString();
    }
}
