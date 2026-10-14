using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The native addresses the Dereth toolbar, its shortcut bars and the stance call. Its own list, so a mismatch turns only this takeover
/// off. Every address and byte list was read from the installed end-of-retail acclient.exe (SHA-256 bca95bbe...). acclient.pdb is a near
/// build: its function names are right, but its addresses differ from the exe's by a per-object offset, so each one was checked there.
/// </summary>
internal static class RetailToolbarCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>gmToolbarUI's primary vtable. A toolbar element whose first dword is this is the gmToolbarUI itself.</summary>
    internal const uint GmToolbarUIVtable = 0x007B5650;
    /// <summary>gmToolbarUI::UseShortcut (int slot, bool fromKey), ThisCall, ret 8. The number keys reach it through the global message handler.</summary>
    internal const uint UseShortcut = 0x004BDFE0;
    /// <summary>gmToolbarUI::AddShortcut (uint objectId, short slot, bool sendToServer), ThisCall, ret 0xC. Slot -1 takes the first empty slot.</summary>
    internal const uint AddShortcut = 0x004BE630;
    /// <summary>gmToolbarUI::RemoveShortcutInSlotNum (int slot, bool sendToServer), ThisCall, ret 8. Returns the object id it removed.</summary>
    internal const uint RemoveShortcutInSlotNum = 0x004BE180;
    /// <summary>gmToolbarUI::RemoveShortcut (uint objectId, bool sendToServer), ThisCall, ret 8: the slot of an object, cleared.</summary>
    internal const uint RemoveShortcut = 0x004BE0E0;
    /// <summary>gmToolbarUI::CreateShortcutToItem (uint objectId, short slot, bool, bool), ThisCall, ret 0x10: a drop onto an occupied slot.</summary>
    internal const uint CreateShortcutToItem = 0x004BE750;
    /// <summary>gmToolbarUI::IsShortcutSlotAvailable (int slot), ThisCall, ret 4: whether the slot is empty.</summary>
    internal const uint IsShortcutSlotAvailable = 0x004BE270;
    /// <summary>gmToolbarUI::GetFirstEmptyShortcutToTheRightOf (uint slot), ThisCall: the next empty slot, or -1.</summary>
    internal const uint GetFirstEmptyShortcutToTheRightOf = 0x004BE1F0;
    /// <summary>gmPanelUI's notice sub-object reads the panel count at +0x0C (gmPanelUI+0x604) inside RecvNotice_SetPanelVisibility.</summary>
    internal const uint PanelChildCount = 0x004BD394;
    /// <summary>...and the panel table at +0x04 (gmPanelUI+0x5FC): {element, panelId} pairs, 8 bytes each.</summary>
    internal const uint PanelChildTable = 0x004BD3A1;
    /// <summary>The same loop compares the element (+0) for null and the panel id (+4) with the requested id.</summary>
    internal const uint PanelChildPairs = 0x004BD3A5;
    /// <summary>PlayerModule::AddShortCut reads the ShortCutManager pointer at PlayerModule+0x04 (the manager is made on first use).</summary>
    internal const uint PlayerModuleShortCutManager = 0x005D39A3;
    /// <summary>ShortCutManager::AddShortCut rejects an index at or above 0x12 (18 slots), then reads the slot pointer at +4 + 4 * index.</summary>
    internal const uint ShortCutManagerSlots = 0x005D68B0;
    /// <summary>ShortCutManager::AddShortCut writes the index at entry +4 and the object id at entry +8.</summary>
    internal const uint ShortCutManagerSlotWrites = 0x005D6903;
    /// <summary>ClientCombatSystem::GetCombatSystem: returns the singleton from 0x0087166C, the receiver of ToggleCombatMode.</summary>
    internal const uint GetCombatSystem = 0x0056B210;
    /// <summary>ClientCombatSystem::ToggleCombatMode, ThisCall, no arguments: the stance button's click. Peace goes to the default stance, anything else to peace.</summary>
    internal const uint ToggleCombatMode = 0x0056D610;
    /// <summary>The typed or slid split amount is written to the global 0x0081D7EC, clamped to [1, stack size].</summary>
    internal const uint SplitAmountWrite = 0x004BFC02;
    /// <summary>The stack size of the selected object (0x0081D7F0), the clamp's upper bound.</summary>
    internal const uint SplitStackSizeRead = 0x004BFBBE;
    /// <summary>ItemHolder::AttemptMerge reads the split amount for the selected object (0x00588765).</summary>
    internal const uint SplitAmountConsumer = 0x00588765;
    /// <summary>A change of selection sets the split amount back to 1 (0x0081D7EC).</summary>
    internal const uint SplitSelectionReset = 0x004C01BD;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("gmToolbarUI vtable slot +0x0C holds ListenToElementMessage (class identity)", GmToolbarUIVtable + 0x0C,
            NativeUiCatalogue.Bytes("20 FB 4B 00"), Source, "data reference (vtable slot)"),
        new NativeUiEntry("gmToolbarUI::UseShortcut", UseShortcut, NativeUiCatalogue.Bytes("8B 44 24 04 85 C0 0F 8C B8 00 00 00 3B 81 3C 06 00 00"),
            Source, "ThisCall (int slot, bool fromKey), ret 8"),
        new NativeUiEntry("gmToolbarUI::AddShortcut", AddShortcut, NativeUiCatalogue.Bytes("83 EC 10 53 8B 5C 24 1C 85 DB 56 57 8B F1"),
            Source, "ThisCall (uint objectId, short slot, bool sendToServer), ret 0xC"),
        new NativeUiEntry("gmToolbarUI::RemoveShortcutInSlotNum", RemoveShortcutInSlotNum, NativeUiCatalogue.Bytes("8B 44 24 04 85 C0 56 57 8B F9 7C 51"),
            Source, "ThisCall (int slot, bool sendToServer), ret 8"),
        new NativeUiEntry("gmToolbarUI::RemoveShortcut", RemoveShortcut, NativeUiCatalogue.Bytes("53 55 56 8B D9 8B 83 3C 06 00 00"),
            Source, "ThisCall (uint objectId, bool sendToServer), ret 8"),
        new NativeUiEntry("gmToolbarUI::CreateShortcutToItem", CreateShortcutToItem, NativeUiCatalogue.Bytes("81 EC 9C 00 00 00 55 8B AC 24 A4 00 00 00"),
            Source, "ThisCall (uint objectId, short slot, bool, bool), ret 0x10"),
        new NativeUiEntry("gmToolbarUI::IsShortcutSlotAvailable", IsShortcutSlotAvailable, NativeUiCatalogue.Bytes("8B 44 24 04 85 C0 7C 23"),
            Source, "ThisCall (int slot), ret 4"),
        new NativeUiEntry("gmToolbarUI::GetFirstEmptyShortcutToTheRightOf", GetFirstEmptyShortcutToTheRightOf, NativeUiCatalogue.Bytes("53 8B 5C 24 08 56"),
            Source, "ThisCall (uint slot)"),
        new NativeUiEntry("gmPanelUI panel count (notice sub-object +0x0C)", PanelChildCount, NativeUiCatalogue.Bytes("8B 46 0C 33 C9"),
            Source, "instruction reference (child count)"),
        new NativeUiEntry("gmPanelUI panel table (notice sub-object +0x04): {element, panelId}", PanelChildTable,
            NativeUiCatalogue.Bytes("8B 46 04 57"), Source, "instruction reference (child table)"),
        new NativeUiEntry("gmPanelUI panel table compares the element (+0) and the panel id (+4)", PanelChildPairs,
            NativeUiCatalogue.Bytes("39 18 74 05 39 50 04 74 13"), Source, "instruction reference (pair layout)"),
        new NativeUiEntry("PlayerModule::AddShortCut reads the ShortCutManager at PlayerModule+0x04", PlayerModuleShortCutManager,
            NativeUiCatalogue.Bytes("8B 46 04 85 C0 75 1C"), Source, "instruction reference (manager pointer)"),
        new NativeUiEntry("ShortCutManager::AddShortCut bounds the 18 slots", ShortCutManagerSlots,
            NativeUiCatalogue.Bytes("83 FF 12 7D 68 8B 44 BB 04"), Source, "instruction reference (slot count and slot read)"),
        new NativeUiEntry("ShortCutManager::AddShortCut writes entry index (+4) and object id (+8)", ShortCutManagerSlotWrites,
            NativeUiCatalogue.Bytes("89 78 04 8B 56 08 89 50 08"), Source, "instruction reference (entry layout)"),
        new NativeUiEntry("ClientCombatSystem::GetCombatSystem", GetCombatSystem, NativeUiCatalogue.Bytes("A1 6C 16 87 00 C3"),
            Source, "Cdecl () -> ClientCombatSystem*, read from 0x0087166C"),
        new NativeUiEntry("ClientCombatSystem::ToggleCombatMode", ToggleCombatMode, NativeUiCatalogue.Bytes("56 8B F1 83 7E 1C 01 6A 01 75 11"),
            Source, "ThisCall, no arguments: peace <-> the default stance, sent to the server (the stance button's click)"),
        new NativeUiEntry("gmToolbarUI split amount write (0x0081D7EC, clamped to the stack size)", SplitAmountWrite,
            NativeUiCatalogue.Bytes("89 35 EC D7 81 00"), Source, "instruction reference (mov [0x81D7EC], esi)"),
        new NativeUiEntry("gmToolbarUI split stack size read (0x0081D7F0)", SplitStackSizeRead, NativeUiCatalogue.Bytes("A1 F0 D7 81 00"),
            Source, "instruction reference (mov eax, [0x81D7F0])"),
        new NativeUiEntry("ItemHolder::AttemptMerge reads the split amount (0x0081D7EC)", SplitAmountConsumer,
            NativeUiCatalogue.Bytes("8B 15 EC D7 81 00"), Source, "instruction reference (mov edx, [0x81D7EC])"),
        new NativeUiEntry("gmToolbarUI selection change resets the split amount (0x0081D7EC)", SplitSelectionReset,
            NativeUiCatalogue.Bytes("89 2D EC D7 81 00"), Source, "instruction reference (mov [0x81D7EC], ebp)")
    };
}
