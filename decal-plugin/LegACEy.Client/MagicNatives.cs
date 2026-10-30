using System;
using System.Collections.Generic;
using System.Text;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// What the Dereth magic window asks of retail itself: adding a spell to the spell bar's current tab, as a double click in retail's spellbook
/// does (gmSpellbookUI sends CM_Magic::SendNotice_AddSpellShortcut, which retail's spellcasting UI takes), and the account name, which
/// picks the tapers in the player's spell formulas (ClientMagicSystem::GetAppropriateSpellFormula reads it from CPlayerSystem + 0x1C).
/// A spell dragged from our window over the spell bar lights the cell under the pointer with retail's own accept mark and is dropped into that
/// cell, as gmSpellcastingUI::HandleDropRelease places a spell dragged from retail's spellbook.
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
    /// <summary>UIElementManager's instance, and the element its hit test last found under the pointer (m_pElementLastOver).</summary>
    private const uint Manager = 0x0083E03C;
    private const int ElementLastOver = 0x244;
    /// <summary>UIElement's DynamicCast vtable slot, and the types it is asked for: gmSpellcastingUI and UIElement_UIItem.</summary>
    private const int DynamicCastSlot = 0x94;
    private const uint SpellcastingType = 0x10000015, ItemType = 0x10000032;
    /// <summary>gmSpellcastingUI: its current tab (thiscall () -> int) and its tabs, 0x1C apart, each with its item list at + 0x62C.</summary>
    private const uint CurrentTab = 0x004C6500;
    private const int TabList = 0x62C, TabStride = 0x1C;
    /// <summary>gmSpellcastingUI::AddSpellShortcut (thiscall (spell, index, select)).</summary>
    private const uint AddSpellShortcut = 0x004C7F10;
    /// <summary>UIElement_ListBox: the item under the mouse (thiscall () -> UIElement*), and an item's index (thiscall (item) -> int).</summary>
    private const uint ItemAtMouse = 0x0046E5E0, IndexOfItem = 0x00480780;
    /// <summary>UIElement::IsAncestorOfMe (thiscall (ancestor) -> bool).</summary>
    private const uint IsAncestorOfMe = 0x0045FBB0;
    /// <summary>UIElement_UIItem::SetDragAcceptState (thiscall (state)): none, or accept (retail's mark on a cell a drop would land in).</summary>
    private const uint SetDragAcceptState = 0x004E1F20, DragAcceptNone = 0x1000003F, DragAcceptYes = 0x10000040;
    private const int MaxDepth = 32;

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
        new NativeUiEntry("gmSpellcastingUI::HandleDropRelease: the current tab's list (+0x62C), its item under the mouse, as a UIItem, its index, AddSpellShortcut",
            0x004C8585, NativeUiCatalogue.Bytes("8A 44 24 08 8B CE E8 70 DF FF FF 6B C0 1C 8B 8C 30 2C 06 00 00 E8 41 60 FA FF 85 C0 0F 84 AC 00 00 00 " +
                "8B 10 68 32 00 00 10 8B C8 FF 92 94 00 00 00 85 C0 0F 84 95 00 00 00 6A 01 50 8B CE E8 38 DF FF FF 6B C0 1C 8B 8C 30 2C 06 00 00 E8"),
            Source, "the retail pattern followed here"),
        new NativeUiEntry("gmSpellcastingUI::DynamicCast", 0x004C6652, NativeUiCatalogue.Bytes("8B 4C 24 04 81 F9 15 00 00 10"), Source,
            "virtual thiscall (uint type) -> gmSpellcastingUI* for type 0x10000015"),
        new NativeUiEntry("gmSpellcastingUI current tab", CurrentTab, NativeUiCatalogue.Bytes("8B 81 FC 05 00 00 85 C0 74 4A"), Source, "thiscall () -> int"),
        new NativeUiEntry("gmSpellcastingUI::AddSpellShortcut", AddSpellShortcut, NativeUiCatalogue.Bytes("8B 44 24 0C 8B 54 24 04 56 8B F1"), Source,
            "thiscall (uint spell, int index, bool select)"),
        new NativeUiEntry("UIElement_ListBox item under the mouse", ItemAtMouse, NativeUiCatalogue.Bytes("51 56 8B F1 8B 8E A4 00 00 00"), Source,
            "thiscall () -> UIElement*"),
        new NativeUiEntry("UIElement_ListBox index of an item", IndexOfItem, NativeUiCatalogue.Bytes("8B 91 10 06 00 00 56 33 C0 85 D2"), Source,
            "thiscall (UIElement* item) -> int, -1 when not listed"),
        new NativeUiEntry("UIElement::IsAncestorOfMe", IsAncestorOfMe, NativeUiCatalogue.Bytes("8B 01 FF 90 A0 00 00 00 85 C0 74 1A 56 8B 74 24 08 3B C6 74 0E"),
            Source, "thiscall (UIElement* ancestor) -> bool"),
        new NativeUiEntry("UIElement_UIItem::SetDragAcceptState", SetDragAcceptState, NativeUiCatalogue.Bytes(
            "8B 89 88 06 00 00 85 C9 74 18 8B 44 24 04 3B 81 00 04 00 00 74 0C 8B 11 89 44 24 04 FF A2 9C 00 00 00"), Source, "thiscall (uint state)"),
        new NativeUiEntry("UIElementManager::MouseUpEvent reads m_pElementLastOver", 0x0045DF23, NativeUiCatalogue.Bytes("8B 8E 44 02 00 00"), Source,
            "offset reference (+0x244)"),
        new NativeUiEntry("GetAppropriateSpellFormula takes the account name at CPlayerSystem + 0x1C", 0x00568B29,
            NativeUiCatalogue.Bytes("83 C7 1C 57"), Source, "instruction reference"),
    };

    // Null until checked.
    private static bool? _ok;
    // The spell bar cell lit for the drag and the list it was in, or zero.
    private static IntPtr _lit, _litList;

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

    /// <summary>
    /// A spell drag from our window moved (<paramref name="dragging"/> true) or ended (false): lights the spell bar cell under the pointer with
    /// retail's accept mark, and clears the cell lit before.
    /// </summary>
    public static void HoverSpellBar(bool dragging, Action<string> log)
    {
        var (_, list, cell, _) = dragging && Ok(log) ? SpellBarTarget() : default;
        if (cell == _lit) return;
        // The tab lists live as long as the spell bar, but their cells can be rebuilt: the old cell is cleared only while its list still holds it.
        if (_lit != IntPtr.Zero && ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int>)IndexOfItem)(_litList, _lit) >= 0)
            ((delegate* unmanaged[Thiscall]<IntPtr, uint, void>)SetDragAcceptState)(_lit, DragAcceptNone);
        (_lit, _litList) = (cell, list);
        if (cell != IntPtr.Zero) ((delegate* unmanaged[Thiscall]<IntPtr, uint, void>)SetDragAcceptState)(cell, DragAcceptYes);
    }

    /// <summary>Logoff or unload: forgets the lit cell, which goes with the spell bar.</summary>
    public static void EndSession() => (_lit, _litList) = (IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Drops the spell on the spell bar as retail's drop does: into the cell under the pointer, or at the current tab's end over the bar's
    /// empty slots. False when the client does not match or the pointer is not over the current tab's list.
    /// </summary>
    public static bool DropOnSpellBar(uint spell, Action<string> log)
    {
        if (!Ok(log)) return false;
        var (ui, _, cell, index) = SpellBarTarget();
        if (ui == IntPtr.Zero) return false;
        ((delegate* unmanaged[Thiscall]<IntPtr, uint, int, byte, void>)AddSpellShortcut)(ui, spell, cell == IntPtr.Zero ? -1 : index, 1);
        return true;
    }

    /// <summary>
    /// The spell bar under the pointer: its gmSpellcastingUI, the current tab's list, the cell (a UIItem in it) under the pointer and its index.
    /// The cell is zero over the list's empty slots; all are zero when the pointer is not over the current tab's list.
    /// </summary>
    private static (IntPtr Ui, IntPtr List, IntPtr Cell, int Index) SpellBarTarget()
    {
        var manager = *(byte**)Manager;
        if (manager == null) return default;
        var over = *(IntPtr*)(manager + ElementLastOver);
        var element = over;
        for (var depth = 0; depth < MaxDepth && element != IntPtr.Zero; depth++)
        {
            var ui = Cast(element, SpellcastingType);
            if (ui != IntPtr.Zero)
            {
                var tab = ((delegate* unmanaged[Thiscall]<IntPtr, int>)CurrentTab)(ui);
                var list = *(IntPtr*)((byte*)ui + TabList + tab * TabStride);
                if (list == IntPtr.Zero || over != list && ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, byte>)IsAncestorOfMe)(over, list) == 0)
                    return default;
                var cell = Cast(((delegate* unmanaged[Thiscall]<IntPtr, IntPtr>)ItemAtMouse)(list), ItemType);
                if (cell == IntPtr.Zero) return (ui, list, IntPtr.Zero, -1);
                return (ui, list, cell, ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int>)IndexOfItem)(list, cell));
            }
            element = NativeUi.Virtual<NativeUi.GetParentFn>(element, NativeUi.GetParentSlot)(element);
        }
        return default;
    }

    /// <summary>UIElement::DynamicCast through its vtable: the element as the type, or zero.</summary>
    private static IntPtr Cast(IntPtr element, uint type)
    {
        if (element == IntPtr.Zero) return IntPtr.Zero;
        var vtable = *(IntPtr**)element;
        return ((delegate* unmanaged[Thiscall]<IntPtr, uint, IntPtr>)vtable[DynamicCastSlot / IntPtr.Size])(element, type);
    }

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
