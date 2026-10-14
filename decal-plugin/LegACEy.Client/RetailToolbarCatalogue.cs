using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The native addresses the Dereth toolbar and its shortcut bars call or read. Its own list, so a mismatch turns only this takeover off.
/// Every address and byte list was read from the installed end-of-retail acclient.exe (SHA-256 bca95bbe...). acclient.pdb is a near
/// build: its function names are right, but its addresses differ from the exe's by a per-object offset, so each one was checked there.
/// </summary>
internal static class RetailToolbarCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe (toolbar spike, ticket 01)";
    /// <summary>gmToolbarUI's primary vtable. A toolbar element whose first dword is this is the gmToolbarUI itself.</summary>
    internal const uint GmToolbarUIVtable = 0x007B5650;
    /// <summary>gmToolbarUI::UseShortcut (int slot, bool fromKey), ThisCall, ret 8. The number keys reach it through the global message handler.</summary>
    internal const uint UseShortcut = 0x004BDFE0;
    /// <summary>gmToolbarUI::AddShortcut (uint objectId, short slot, bool sendToServer), ThisCall, ret 0xC. Slot -1 takes the first empty slot.</summary>
    internal const uint AddShortcut = 0x004BE630;
    /// <summary>gmToolbarUI::RemoveShortcutInSlotNum (int slot, bool sendToServer), ThisCall, ret 8.</summary>
    internal const uint RemoveShortcutInSlotNum = 0x004BE180;
    /// <summary>The toolbar's slot array (this+0x634, one element per slot) is read at this offset.</summary>
    internal const uint ToolbarSlotArray = 0x004BDFF8;
    /// <summary>IAsheronsCallImpl::GetCombatMode's read of ClientUISystem+0x18, the stance (COMBAT_MODE: 1 peace, 2 melee, 4 missile, 8 magic).</summary>
    internal const uint CombatModeRead = 0x0055ACE0;
    /// <summary>gmPanelUI's notice sub-object reads the panel count at +0x0C (gmPanelUI+0x604) inside RecvNotice_SetPanelVisibility.</summary>
    internal const uint PanelChildCount = 0x004BD394;
    /// <summary>...and the panel table at +0x04 (gmPanelUI+0x5FC): {element, panelId} pairs.</summary>
    internal const uint PanelChildTable = 0x004BD3A1;
    /// <summary>PlayerModule::AddShortCut reads the ShortCutManager pointer at PlayerModule+0x04 (the manager is made on first use).</summary>
    internal const uint PlayerModuleShortCutManager = 0x005D39A3;
    /// <summary>ShortCutManager::AddShortCut rejects an index at or above 0x12 (18 slots), then reads the slot pointer at +4 + 4 * index.</summary>
    internal const uint ShortCutManagerSlots = 0x005D68B0;
    /// <summary>ShortCutManager::AddShortCut writes the index at entry +4 and the object id at entry +8.</summary>
    internal const uint ShortCutManagerSlotWrites = 0x005D6903;
    /// <summary>ClientCombatSystem::GetCombatSystem: returns the singleton from 0x0087166C, the receiver of the stance calls below.</summary>
    internal const uint GetCombatSystem = 0x0056B210;
    /// <summary>ClientCombatSystem::SetCombatMode (COMBAT_MODE mode, bool sendToServer), ThisCall, ret 8. Modes: 1 peace, 2 melee, 4 missile, 8 magic.</summary>
    internal const uint SetCombatMode = 0x0056CB80;
    /// <summary>ClientCombatSystem::ToggleCombatMode, ThisCall, no arguments: the stance button's click.</summary>
    internal const uint ToggleCombatMode = 0x0056D610;
    /// <summary>The toolbar's element switch: element id minus 0x10000192 (the stance icons), bounds 0x13.</summary>
    internal const uint StanceIdSwitch = 0x004BFD6C;
    /// <summary>...and the case map for those ids: case 0 (ToggleCombatMode) for ids 0x10000192 to 0x10000195.</summary>
    internal const uint StanceCaseMap = 0x004BFE8C;
    /// <summary>The typed or slid split amount is written to the global 0x0081D7EC, clamped to [1, stack size].</summary>
    internal const uint SplitAmountWrite = 0x004BFC02;
    /// <summary>The stack size of the selected object (0x0081D7F0), the clamp's upper bound.</summary>
    internal const uint SplitStackSizeRead = 0x004BFBBE;
    /// <summary>ItemHolder::AttemptMerge reads the split amount for the selected object (0x00588765).</summary>
    internal const uint SplitAmountConsumer = 0x00588765;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        Shared("ClientUISystem::GetUISystem"),
        new NativeUiEntry("gmToolbarUI vtable slot +0x0C holds ListenToElementMessage (class identity)", GmToolbarUIVtable + 0x0C,
            NativeUiCatalogue.Bytes("20 FB 4B 00"), Source, "data reference (vtable slot)"),
        new NativeUiEntry("gmToolbarUI::UseShortcut", UseShortcut, NativeUiCatalogue.Bytes("8B 44 24 04 85 C0 0F 8C B8 00 00 00 3B 81 3C 06 00 00"),
            Source, "ThisCall (int slot, bool fromKey), ret 8"),
        new NativeUiEntry("gmToolbarUI slot array read (this+0x634)", ToolbarSlotArray, NativeUiCatalogue.Bytes("8B 89 34 06 00 00"),
            Source, "instruction reference (slot array pointer)"),
        new NativeUiEntry("gmToolbarUI::AddShortcut", AddShortcut, NativeUiCatalogue.Bytes("83 EC 10 53 8B 5C 24 1C 85 DB 56 57 8B F1"),
            Source, "ThisCall (uint objectId, short slot, bool sendToServer), ret 0xC"),
        new NativeUiEntry("gmToolbarUI::RemoveShortcutInSlotNum", RemoveShortcutInSlotNum, NativeUiCatalogue.Bytes("8B 44 24 04 85 C0 56 57 8B F9 7C 51"),
            Source, "ThisCall (int slot, bool sendToServer), ret 8"),
        new NativeUiEntry("IAsheronsCallImpl::GetCombatMode reads ClientUISystem+0x18", CombatModeRead,
            NativeUiCatalogue.Bytes("8B 40 18 8B 4C 24 0C 89 01 33 C0 59"), Source, "instruction reference (stance field)"),
        new NativeUiEntry("gmPanelUI panel count (notice sub-object +0x0C)", PanelChildCount, NativeUiCatalogue.Bytes("8B 46 0C 33 C9"),
            Source, "instruction reference (child count)"),
        new NativeUiEntry("gmPanelUI panel table (notice sub-object +0x04): {element, panelId}", PanelChildTable,
            NativeUiCatalogue.Bytes("8B 46 04 57"), Source, "instruction reference (child table)"),
        new NativeUiEntry("PlayerModule::AddShortCut reads the ShortCutManager at PlayerModule+0x04", PlayerModuleShortCutManager,
            NativeUiCatalogue.Bytes("8B 46 04 85 C0 75 1C"), Source, "instruction reference (manager pointer)"),
        new NativeUiEntry("ShortCutManager::AddShortCut bounds the 18 slots", ShortCutManagerSlots,
            NativeUiCatalogue.Bytes("83 FF 12 7D 68 8B 44 BB 04"), Source, "instruction reference (slot count and slot read)"),
        new NativeUiEntry("ShortCutManager::AddShortCut writes entry index (+4) and object id (+8)", ShortCutManagerSlotWrites,
            NativeUiCatalogue.Bytes("89 78 04 8B 56 08 89 50 08"), Source, "instruction reference (entry layout)"),
        new NativeUiEntry("ClientCombatSystem::GetCombatSystem", GetCombatSystem, NativeUiCatalogue.Bytes("A1 6C 16 87 00 C3"),
            Source, "Cdecl () -> ClientCombatSystem*, read from 0x0087166C"),
        new NativeUiEntry("ClientCombatSystem::SetCombatMode", SetCombatMode, NativeUiCatalogue.Bytes("51 53 56 8B F1 8B 46 1C 57 8B 7C 24 14"),
            Source, "ThisCall (COMBAT_MODE mode, bool sendToServer), ret 8; returns early when the mode is unchanged"),
        new NativeUiEntry("ClientCombatSystem::ToggleCombatMode", ToggleCombatMode, NativeUiCatalogue.Bytes("56 8B F1 83 7E 1C 01 6A 01 75 11"),
            Source, "ThisCall, no arguments: peace <-> the default mode, sent to the server (the stance button's click)"),
        new NativeUiEntry("gmToolbarUI element switch: stance ids 0x10000192.. (element id minus base)", StanceIdSwitch,
            NativeUiCatalogue.Bytes("05 6E FE FF EF 83 F8 13"), Source, "instruction reference (id base 0x10000192, bound 0x13)"),
        new NativeUiEntry("gmToolbarUI element switch: case map for ids 0x10000192..0x10000195 (case 0 = ToggleCombatMode)", StanceCaseMap,
            NativeUiCatalogue.Bytes("00 00 00 00 03 03 03 03"), Source, "data reference (case map)"),
        new NativeUiEntry("gmToolbarUI split amount write (0x0081D7EC, clamped to the stack size)", SplitAmountWrite,
            NativeUiCatalogue.Bytes("89 35 EC D7 81 00"), Source, "instruction reference (mov [0x81D7EC], esi)"),
        new NativeUiEntry("gmToolbarUI split stack size read (0x0081D7F0)", SplitStackSizeRead, NativeUiCatalogue.Bytes("A1 F0 D7 81 00"),
            Source, "instruction reference (mov eax, [0x81D7F0])"),
        new NativeUiEntry("ItemHolder::AttemptMerge reads the split amount (0x0081D7EC)", SplitAmountConsumer,
            NativeUiCatalogue.Bytes("8B 15 EC D7 81 00"), Source, "instruction reference (mov edx, [0x81D7EC])")
    };

    private static NativeUiEntry Shared(string name) => RetailTargetMode.Entries.Single(entry => entry.Name == name);
}
