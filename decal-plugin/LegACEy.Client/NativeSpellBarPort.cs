using System;
using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The spell bar's port over retail's own spell bar (gmSpellcastingUI), which stays in the held combat panel: every read and click goes
/// through it, as its own tabs, cells and Cast button do, so retail's keys (tabs, selection, the number keys) and the Dereth bar stay in
/// step and retail keeps the server's favourites. It is built only once <see cref="Entries"/> has passed. <see cref="Poll"/> raises
/// <see cref="Changed"/> when the tab, its spells, its selection or the endowment moved, and is called once a frame on the game thread.
/// </summary>
internal sealed unsafe class NativeSpellBarPort : ISpellBarPort
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb (near build)";
    /// <summary>The spell bar as the combat panel (LayoutDesc 0x21000073) places it, on gmSpellcastingUI 0x1000009F of LayoutDesc 0x21000010.</summary>
    internal const uint SpellBar = 0x10000061;
    private const uint SpellcastingType = 0x10000015, ItemType = 0x10000032;
    private const int DynamicCastSlot = 0x94;
    // gmSpellcastingUI: its tab field at +0x5FC, then one SpellCastSubMenu per tab from +0x628, 0x1C apart, with the tab's item list at +4 and
    // its selected spell at +0xC. A list counts its items at +0x610; a spell's UIItem holds the spell at +0x600.
    private const int Field = 0x5FC, SubMenus = 0x628, SubMenuStride = 0x1C, SubMenuList = 0x4, SubMenuSelected = 0xC, ListCount = 0x610, ItemSpell = 0x600;
    // The endowment: the held item with an on-use spell that UpdateEndowmentIcon puts at the bar's left, its object id at +0x620 and its spell
    // at +0x624 of gmSpellcastingUI (zero for none); a tab's SpellCastSubMenu marks it selected with a byte at +0x18, which Cast then uses.
    private const int EndowmentItem = 0x620, EndowmentSpell = 0x624, SubMenuEndowmentSelected = 0x18;
    /// <summary>Retail's tab buttons, I to VIII: the ids gmSpellcastingUI::GetNextTabID steps through.</summary>
    private static readonly uint[] TabButtons = { 0x100000A3, 0x100000A4, 0x100000A5, 0x100000A6, 0x100000A7, 0x100000A8, 0x100000A9, 0x100005C2 };

    private const uint CurrentTab = 0x004C6500, ItemAt = 0x0046DC50, SetSelected = 0x004C66F0, UpdateEndowmentIcon = 0x004C6D10,
        UpdateCastButtonTooltip = 0x004C7620, CastSelected = 0x004C6C40, OpenTabButton = 0x0046C110, RemoveFromMenu = 0x004C74E0,
        AddSpellShortcut = 0x004C7F10;

    /// <summary>The spell bar's natives, checked on their own: any mismatch leaves retail's spell bar to the player.</summary>
    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        Entry("gmSpellcastingUI::GetOpenSubMenuIndex: the tab field at +0x5FC, its open page at +0x6D8", CurrentTab,
            "8B 81 FC 05 00 00 85 C0 74 4A 8B 80 D8 06 00 00", "thiscall () -> int"),
        Entry("UIElement_ListBox item at an index, counted at +0x610", ItemAt, "8B 44 24 04 3B 81 10 06 00 00 73 0C", "thiscall (int) -> UIElement*"),
        Entry("SpellCastSubMenu::SetSelected: the list at +4, the spell kept at +0xC", SetSelected, "56 8B F1 8B 46 04 8B 88 10 06 00 00 57 33 FF 85",
            "thiscall (uint spell)"),
        Entry("gmSpellcastingUI::UpdateEndowmentIcon", UpdateEndowmentIcon, "81 EC 30 01 00 00 55 56 8B F1 8B 86 08 06 00 00", "thiscall ()"),
        // Decal patches this one's prologue and first call in the running client, so its body after them is checked.
        Entry("gmSpellcastingUI::UpdateCastButtonTooltip body after its prologue and ClientMagicSystem lookup", UpdateCastButtonTooltip + 0xE,
            "85 C0 89 44 24 14 0F 84 04 06 00 00 8B 96 8C 06 00 00", "thiscall ()"),
        Entry("gmSpellcastingUI::Cast: the open tab's selected spell at +0x634", CastSelected,
            "81 EC 94 00 00 00 56 E8 B4 F8 FF FF 6B C0 1C 8B B4 08 34 06 00 00", "thiscall ()"),
        Entry("UIElement_Field opens the page of a tab button", OpenTabButton, "53 57 8B 7C 24 0C 33 D2 8B C7 F7 B1 5C 06 00 00", "thiscall (uint button) -> bool"),
        Entry("gmSpellcastingUI::RecvNotice_FirstSpellTab opens tab button I through the field at +4 of the notice handler", 0x004C8330,
            "56 8B F1 8B 4E 04 85 C9 74 1F 68 A3 00 00 10 E8 CC 3D FA FF", "the retail pattern followed here"),
        Entry("SpellCastSubMenu::RemoveSpellFromMenu", RemoveFromMenu, "8B 44 24 04 85 C0 57 8B F9 75 07 83 C8 FF 5F C2 04 00", "thiscall (uint spell) -> int"),
        Entry("gmSpellcastingUI::AddSpellShortcut", AddSpellShortcut, "8B 44 24 0C 8B 54 24 04 56 8B F1", "thiscall (uint spell, int index, bool select)"),
    };

    /// <summary>
    /// The endowment's natives, checked apart from the bar's: a mismatch only leaves the held item off the Dereth bar, not the whole bar.
    /// </summary>
    internal static readonly IReadOnlyList<NativeUiEntry> EndowmentEntries = new[]
    {
        Entry("gmSpellcastingUI::UpdateEndowmentIcon keeps the held item at +0x620 and its spell (the weenie's +0x110) at +0x624", 0x004C6DE5,
            "8B 8E 10 06 00 00 89 BE 20 06 00 00 8B 83 10 01 00 00 89 86 24 06 00 00", "the retail pattern read here"),
        Entry("gmSpellcastingUI::ListenToElementMessage: a click on the endowment icon 0x100000B1 marks the tab's +0x18, then SetSelected(0)",
            0x004C8765, "81 3F B1 00 00 10 0F 85 D5 00 00 00 8B 47 0C 83 F8 07 75 37 8B CE E8 80 DD FF FF 6B C0 1C 8D 8C 30 28 06 00 00 6A 00 C6 41 18 01",
            "the retail pattern followed here"),
        Entry("gmSpellcastingUI::Cast uses the endowment while the open tab's +0x640 is set", 0x004C6C5E,
            "8A 94 08 40 06 00 00 84 D2 74 0A 8B 89 20 06 00 00", "the retail pattern followed here"),
    };

    private readonly bool _lockAvailable, _endowmentAvailable;
    private readonly Action<uint> _examine;
    private readonly Func<uint, WieldedItem?> _wielded;
    private readonly Func<uint, string?> _spellName;
    private (int Tab, uint Selected, int Spells, uint Endowment, bool EndowmentSelected)? _last;

    /// <param name="lockAvailable">Whether retail's Lock UI natives matched; the window does not move while the UI is locked.</param>
    /// <param name="examine">Shows a spell in the appraisal window.</param>
    /// <param name="endowmentAvailable">Whether <see cref="EndowmentEntries"/> matched; without them the bar shows no held item.</param>
    /// <param name="wielded">A wielded item by object id, for the endowment's icon and name; null when it is not known.</param>
    /// <param name="spellName">A spell's name from the portal's spell table.</param>
    public NativeSpellBarPort(bool lockAvailable, Action<uint> examine, bool endowmentAvailable, Func<uint, WieldedItem?> wielded,
        Func<uint, string?> spellName)
    {
        _lockAvailable = lockAvailable;
        _endowmentAvailable = endowmentAvailable;
        _examine = examine ?? throw new ArgumentNullException(nameof(examine));
        _wielded = wielded ?? throw new ArgumentNullException(nameof(wielded));
        _spellName = spellName ?? throw new ArgumentNullException(nameof(spellName));
    }

    public event EventHandler? Changed;

    /// <summary>Whether the spell bar's natives and the combat panel's hold all match the running client.</summary>
    internal static bool Validate(Func<uint, int, byte[]> read, Action<string> log) =>
        NativeUiCatalogue.Validate(read, log, Entries.Concat(RetailAttackCatalogue.Hold),
            "Dereth spell bar is off: the running client does not match its native entries.");

    /// <summary>Whether the endowment's natives match the running client.</summary>
    internal static bool ValidateEndowment(Func<uint, int, byte[]> read, Action<string> log) =>
        NativeUiCatalogue.Validate(read, log, EndowmentEntries, "The Dereth spell bar shows no held item: the running client does not match its entries.");

    public int Tab
    {
        get
        {
            var ui = Ui();
            return ui == null ? 0 : ((delegate* unmanaged[Thiscall]<byte*, int>)CurrentTab)(ui);
        }
        set
        {
            var ui = Ui();
            if (ui == null || value < 0 || value >= TabButtons.Length) return;
            var tabs = *(byte**)(ui + Field);
            if (tabs == null) return;
            // Retail's order, as its tab keys do it: open the page, then the endowment icon and Cast's tooltip follow the tab.
            ((delegate* unmanaged[Thiscall]<byte*, uint, byte>)OpenTabButton)(tabs, TabButtons[value]);
            Refresh(ui);
        }
    }

    public IReadOnlyList<uint> Spells
    {
        get
        {
            var list = List(SubMenu(out _));
            var count = list == null ? 0 : *(int*)(list + ListCount);
            var spells = new List<uint>(count);
            for (var i = 0; i < count; i++)
            {
                // Retail's list keeps its empty cells as items too, holding no spell.
                var spell = SpellAt(list, i);
                if (spell != 0) spells.Add(spell);
            }
            return spells;
        }
    }

    public uint Selected
    {
        get
        {
            var menu = SubMenu(out _);
            return menu == null ? 0 : *(uint*)(menu + SubMenuSelected);
        }
    }

    public void Select(uint spell)
    {
        var menu = SubMenu(out var ui);
        if (menu == null) return;
        ((delegate* unmanaged[Thiscall]<byte*, uint, void>)SetSelected)(menu, spell);
        Refresh(ui);
    }

    public void Cast()
    {
        var ui = Ui();
        if (ui != null) ((delegate* unmanaged[Thiscall]<byte*, void>)CastSelected)(ui);
    }

    public void Remove(uint spell)
    {
        // As retail's drag start does: the spell leaves the tab and the player's favourites, then the icon and tooltip follow.
        var menu = SubMenu(out var ui);
        if (menu == null) return;
        ((delegate* unmanaged[Thiscall]<byte*, uint, int>)RemoveFromMenu)(menu, spell);
        Refresh(ui);
    }

    public void Insert(uint spell, int index)
    {
        var ui = Ui();
        if (ui != null) ((delegate* unmanaged[Thiscall]<byte*, uint, int, byte, void>)AddSpellShortcut)(ui, spell, index, 1);
    }

    public void Examine(uint spell) => _examine(spell);

    public SpellBarEndowment? Endowment
    {
        get
        {
            var menu = SubMenu(out var ui);
            if (!_endowmentAvailable || menu == null || *(uint*)(ui + EndowmentItem) is var id && id == 0 || _wielded(id) is not { } item) return null;
            // Retail's tooltip for it, "%s (%hs)": the item's name, then its spell's.
            var spell = _spellName(*(uint*)(ui + EndowmentSpell));
            return new SpellBarEndowment(item.Visual, spell == null ? item.Name : $"{item.Name} ({spell})", *(menu + SubMenuEndowmentSelected) != 0);
        }
    }

    public void SelectEndowment()
    {
        // As retail's click on the icon does: mark the tab's endowment, clear its spell, then the icon and tooltip follow.
        var menu = SubMenu(out var ui);
        if (!_endowmentAvailable || menu == null || *(uint*)(ui + EndowmentItem) == 0) return;
        *(menu + SubMenuEndowmentSelected) = 1;
        ((delegate* unmanaged[Thiscall]<byte*, uint, void>)SetSelected)(menu, 0);
        Refresh(ui);
    }

    public bool UiLocked => _lockAvailable && NativeUi.IsUiLocked;

    /// <summary>
    /// Raises <see cref="Changed"/> when the open tab, its spells, its selection or the endowment moved since the last call. Called every frame, so it reads
    /// retail's list in place and allocates nothing.
    /// </summary>
    public void Poll()
    {
        var menu = SubMenu(out var ui);
        var list = List(menu);
        var count = list == null ? 0 : *(int*)(list + ListCount);
        var spells = count;
        for (var i = 0; i < count; i++) spells = spells * 31 + (int)SpellAt(list, i);
        (int, uint, int, uint, bool)? now = menu == null ? null : (((delegate* unmanaged[Thiscall]<byte*, int>)CurrentTab)(ui),
            *(uint*)(menu + SubMenuSelected), spells, _endowmentAvailable ? *(uint*)(ui + EndowmentItem) : 0,
            _endowmentAvailable && *(menu + SubMenuEndowmentSelected) != 0);
        if (now == _last) return;
        _last = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Retail's spell bar, or null while the combat panel is not laid out.</summary>
    private static byte* Ui() => Cast((byte*)NativeUi.GetElement(SpellBar), SpellcastingType);

    /// <summary>The open tab's SpellCastSubMenu, or null.</summary>
    private static byte* SubMenu(out byte* ui)
    {
        ui = Ui();
        if (ui == null) return null;
        var tab = ((delegate* unmanaged[Thiscall]<byte*, int>)CurrentTab)(ui);
        return tab < 0 || tab >= TabButtons.Length ? null : ui + SubMenus + tab * SubMenuStride;
    }

    /// <summary>A tab's item list, or null.</summary>
    private static byte* List(byte* menu) => menu == null ? null : *(byte**)(menu + SubMenuList);

    /// <summary>The spell in the list's item at an index; zero for an empty cell.</summary>
    private static uint SpellAt(byte* list, int index)
    {
        var item = Cast(((delegate* unmanaged[Thiscall]<byte*, int, byte*>)ItemAt)(list, index), ItemType);
        return item == null ? 0 : *(uint*)(item + ItemSpell);
    }

    private static void Refresh(byte* ui)
    {
        ((delegate* unmanaged[Thiscall]<byte*, void>)UpdateEndowmentIcon)(ui);
        ((delegate* unmanaged[Thiscall]<byte*, void>)UpdateCastButtonTooltip)(ui);
    }

    /// <summary>UIElement::DynamicCast through its vtable: the element as the type, or null.</summary>
    private static byte* Cast(byte* element, uint type)
    {
        if (element == null) return null;
        var vtable = *(IntPtr**)element;
        return ((delegate* unmanaged[Thiscall]<byte*, uint, byte*>)vtable[DynamicCastSlot / IntPtr.Size])(element, type);
    }

    private static NativeUiEntry Entry(string name, uint address, string bytes, string convention) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, convention);
}
