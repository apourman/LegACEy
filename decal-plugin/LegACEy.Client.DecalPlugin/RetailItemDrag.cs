using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The retail client's item drag-and-drop, read without hooks. <c>UIElementManager</c> keeps the drag in
/// <c>m_dragElement</c> (+0x31C) and <c>m_bDragStarted</c> (+0x324); <c>UIElement_ItemList::InqDropIconInfo</c> turns
/// the drag icon into the dragged object's id. A drop onto a LegACEy window is cancelled the way retail ends a drag
/// that nothing catches: <c>StopDragandDrop</c> with no catcher (+0x250), which tells the source (the inventory)
/// that the drop failed so it restores the item, and never delivers it to the world under the window.
/// What the pointer is over comes from the client's own hit test, <c>m_pElementLastOver</c> (+0x244).
/// Offsets and functions come from Chorizite's AcClient bindings and our read of the installed acclient.exe.
/// Call on the game thread.
/// </summary>
internal sealed class RetailItemDrag
{
    private const string Source = "Chorizite AcClient bindings (UIElementManager, UIElement_ItemList); our capstone read of installed end-of-retail acclient.exe";
    private const int DragElementOffset = 0x31C;
    private const int DragStartedOffset = 0x324;
    private const int DragCatcherOffset = 0x250;
    private const int PotentialDragElementOffset = 0x318;
    private const int ElementLastOverOffset = 0x244;
    private const int ElementIdOffset = 0x2E4;
    private static readonly IntPtr ManagerInstance = new(0x0083E03C);

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // Through its first use of the catcher: reads m_dragElement (+0x31C), m_pElementLastDragCursorOver (+0x250) and m_dragOwner (+0x320).
        new NativeUiEntry("UIElementManager::StopDragandDrop", 0x00459880, NativeUiCatalogue.Bytes(
            "53 57 8B F9 8B 87 1C 03 00 00 33 DB 3B C3 0F 84 92 00 00 00 56 6A 18 E8 59 58 18 00 83 C4 04 3B C3 74 1D " +
            "C7 40 04 01 00 00 00 C7 00 94 CD 79 00 89 58 08 89 58 0C 89 58 10 88 58 14 8B F0 EB 02 33 F6 8B 87 1C 03 00 00 " +
            "89 46 08 8B 8F 50 02 00 00 89 4E 10 8B 97 20 03 00 00 89 56 0C C6 46 14 01 8B 8F 50 02 00 00 3B CB 74 0E"), Source, "ThisCall ()"),
        new NativeUiEntry("UIElementManager::MouseUpEvent reads m_bDragStarted and m_dragElement", 0x0045DEFB,
            NativeUiCatalogue.Bytes("8A 86 24 03 00 00 84 C0 74 46 8B 86 1C 03 00 00"), Source, "offset reference (+0x324, +0x31C)"),
        new NativeUiEntry("UIElementManager::MouseUpEvent reads m_pElementLastOver", 0x0045DF23, NativeUiCatalogue.Bytes("8B 8E 44 02 00 00"), Source, "offset reference (+0x244)"),
        new NativeUiEntry("UIElement_UIItem::SetDragAcceptState", 0x004E1F20, NativeUiCatalogue.Bytes(
            "8B 89 88 06 00 00 85 C9 74 18 8B 44 24 04 3B 81 00 04 00 00 74 0C 8B 11 89 44 24 04 FF A2 9C 00 00 00 C2"), Source,
            "ThisCall (uint state); 0x1000003F none, 0x10000040 accept (green circle), 0x10000041 reject (showed red in game)"),
        new NativeUiEntry("UIElement_UIItem::DynamicCast", 0x004E1D40, NativeUiCatalogue.Bytes("8B C1 8B 4C 24 04 81 F9 32 00 00 10 74 0B 33 D2 83 F9 03 0F 95 C2 4A 23 C2 C2 04"), Source,
            "virtual ThisCall (uint type) -> UIElement_UIItem* for type 0x10000032"),
        new NativeUiEntry("DynamicCast to UIItem through vtable +0x94", 0x0048B762, NativeUiCatalogue.Bytes("68 32 00 00 10 8B C8 FF 92 94 00 00 00"), Source, "virtual slot reference"),
        new NativeUiEntry("UIElement::IsAncestorOfMe", 0x0045FBB0, NativeUiCatalogue.Bytes("8B 01 FF 90 A0 00 00 00 85 C0 74 1A 56 8B 74 24 08 3B C6 74 0E 8B 10 8B C8 FF"), Source,
            "ThisCall (UIElement* ancestor) -> bool"),
        new NativeUiEntry("UIElement_ItemList::InqDropIconInfo", 0x004E3380, NativeUiCatalogue.Bytes("8B 44 24 10 83 EC 3C 53 55 8B 6C 24 50 56 8B 74"), Source,
            "Cdecl (UIElement* dropIcon, uint* itemId, uint* spellId, DropItemFlags* flags)"),
        // The hand-off: names from the acclient.pdb beside the installed client (a near build; addresses checked here).
        new NativeUiEntry("UIElement_ItemList::ItemList_GetItem", 0x004E3BC0, NativeUiCatalogue.Bytes(
            "53 56 57 8B F9 8B 87 10 06 00 00 33 F6 85 C0 76 35 8B 5C 24 10 8B 87 08 06 00 00 8B 0C B0 85 C9"), HandOffSource,
            "ThisCall (uint objectId) -> UIElement_UIItem*, the list item whose object id (+0x5FC) matches, or 0"),
        new NativeUiEntry("UIElement_ItemList::ItemList_BeginDrag", 0x004E3F60, NativeUiCatalogue.Bytes(
            "51 56 8D 44 24 07 50 68 16 00 00 10 8B F1 E8 4D CD F7 FF 8A 44 24 07 84 C0 0F 84 FE 00 00 00 8B"), HandOffSource,
            "ThisCall (int x, int y): hit-tests the item at the screen point, selects it, and starts retail's drag of it"),
        new NativeUiEntry("UIElement::GetAbsoluteX", 0x0069FE00, NativeUiCatalogue.Bytes("56 8B F1 8B 86 B0 00 00 00 85 C0 74 05 8B 40 20 5E C3 8B 8E AC 00 00 00"),
            HandOffSource, "ThisCall () -> int, screen x through the parent chain"),
        new NativeUiEntry("UIElement::GetAbsoluteY", 0x0069FE30, NativeUiCatalogue.Bytes("56 8B F1 8B 86 B0 00 00 00 85 C0 74 05 8B 40 24 5E C3 8B 8E AC 00 00 00"),
            HandOffSource, "ThisCall () -> int, screen y through the parent chain"),
        new NativeUiEntry("UIElement_ItemList::DynamicCast", 0x004E4830, NativeUiCatalogue.Bytes("8B C1 8B 4C 24 04 81 F9 31 00 00 10 74 10"), HandOffSource,
            "virtual ThisCall (uint type) -> UIElement_ItemList* for type 0x10000031")
    };
    private const string HandOffSource = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build) for names; our objdump read of installed acclient.exe";
    // The inventory panel's item lists: gm3DItemsUI's pack contents, then gmBackpackUI's two (gm*UI::PostInit).
    private static readonly uint[] InventoryLists = { 0x100001C6, 0x100001C9, 0x100001CA };
    private const uint ItemListType = 0x10000031;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void StopDragFn(IntPtr manager);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte IsAncestorOfMeFn(IntPtr element, IntPtr ancestor);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetDragAcceptStateFn(IntPtr item, uint state);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr DynamicCastFn(IntPtr element, uint type);
    private const uint UiItemType = 0x10000032;
    private const uint DragAcceptNone = 0x1000003F;
    private const uint DragAcceptYes = 0x10000040;
    private const int DynamicCastSlot = 0x94;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InqDropIconInfoFn(IntPtr dropIcon, out uint itemId, out uint spellId, out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetItemFn(IntPtr list, uint objectId);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void BeginDragFn(IntPtr list, int x, int y);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int AbsoluteFn(IntPtr element);

    private readonly Action<string> _log;
    private readonly StopDragFn? _stop;
    private readonly IsAncestorOfMeFn? _isAncestorOfMe;
    private readonly SetDragAcceptStateFn? _setDragAccept;
    private IntPtr _acceptCell;
    private readonly InqDropIconInfoFn? _inquire;
    private readonly GetItemFn? _getItem;
    private readonly BeginDragFn? _beginDrag;
    private readonly AbsoluteFn? _absoluteX;
    private readonly AbsoluteFn? _absoluteY;
    private bool _loggedInventory;

    public RetailItemDrag(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        _log = log;
        Available = NativeUiCatalogue.Validate(readMemory, log, Entries);
        if (!Available)
        {
            log("Item drag and drop disabled: the running client does not match its native drag entries.");
            return;
        }
        _stop = Function<StopDragFn>(0x00459880);
        _isAncestorOfMe = Function<IsAncestorOfMeFn>(0x0045FBB0);
        _setDragAccept = Function<SetDragAcceptStateFn>(0x004E1F20);
        _inquire = Function<InqDropIconInfoFn>(0x004E3380);
        _getItem = Function<GetItemFn>(0x004E3BC0);
        _beginDrag = Function<BeginDragFn>(0x004E3F60);
        _absoluteX = Function<AbsoluteFn>(0x0069FE00);
        _absoluteY = Function<AbsoluteFn>(0x0069FE30);
    }

    public bool Available { get; }

    /// <summary>The object being dragged in the retail UI, or 0 (no drag, or a spell).</summary>
    public uint CurrentItem()
    {
        if (!Available) return 0;
        var manager = Marshal.ReadIntPtr(ManagerInstance);
        if (manager == IntPtr.Zero || Marshal.ReadByte(manager, DragStartedOffset) == 0) return 0;
        var icon = Marshal.ReadIntPtr(manager, DragElementOffset);
        if (icon == IntPtr.Zero) return 0;
        _inquire!(icon, out var itemId, out _, out _);
        return itemId;
    }

    /// <summary>
    /// Ends the retail drag as one that nothing caught: the source restores the item, and nothing is dropped.
    /// Call before the client sees the button-up; it then sees an ordinary release with no drag in progress.
    /// </summary>
    public void Cancel()
    {
        if (!Available) return;
        var manager = Marshal.ReadIntPtr(ManagerInstance);
        if (manager == IntPtr.Zero || Marshal.ReadIntPtr(manager, DragElementOffset) == IntPtr.Zero) return;
        Marshal.WriteIntPtr(manager, DragCatcherOffset, IntPtr.Zero);
        _stop!(manager);
        Marshal.WriteIntPtr(manager, PotentialDragElementOffset, IntPtr.Zero);
    }

    /// <summary>
    /// The retail inventory's list and item element for an object, or zeros when no list there shows it (an item in a pack the
    /// retail panel is not showing, or an equipped one). The panel need not be visible: a parked one keeps its lists.
    /// </summary>
    public (IntPtr List, IntPtr Item) FindItem(uint objectId)
    {
        var panel = Available ? NativeUi.GetElement(NativeUi.InventoryPanel) : IntPtr.Zero;
        if (panel == IntPtr.Zero) return default;
        foreach (var id in InventoryLists)
        {
            var element = NativeUi.GetElement(id);
            if (element == IntPtr.Zero || _isAncestorOfMe!(element, panel) == 0) continue;
            var list = NativeUi.Virtual<DynamicCastFn>(element, DynamicCastSlot)(element, ItemListType);
            var item = list == IntPtr.Zero ? IntPtr.Zero : _getItem!(list, objectId);
            if (item != IntPtr.Zero) return (list, item);
        }
        return default;
    }

    /// <summary>
    /// Starts retail's own drag of the item, as a press on it in its list does. Retail checks that the left button is held and
    /// has moved 4 pixels from where the client saw it go down, so the client must have seen the press. True when retail now
    /// drags this object; a drag of anything else is cancelled.
    /// </summary>
    public bool BeginDrag(IntPtr list, IntPtr item, uint objectId)
    {
        // A point just inside the item: the list hit-tests it, wherever the panel is parked.
        _beginDrag!(list, _absoluteX!(item) + 2, _absoluteY!(item) + 2);
        var dragged = CurrentItem();
        if (dragged == objectId) return true;
        if (dragged != 0) Cancel();
        return false;
    }

    /// <summary>
    /// Forgets the element the client's last left press landed on, so the client's next move does not start a drag of it.
    /// </summary>
    public void ClearPotentialDrag()
    {
        var manager = Available ? Marshal.ReadIntPtr(ManagerInstance) : IntPtr.Zero;
        if (manager != IntPtr.Zero) Marshal.WriteIntPtr(manager, PotentialDragElementOffset, IntPtr.Zero);
    }

    /// <summary>
    /// Whether the client's own hit test puts the pointer over the retail inventory window (its packs, paperdoll and
    /// container list included). Exists is false when the client has no inventory element; Open, whether it is shown.
    /// </summary>
    public bool IsPointerOverInventory(out bool exists, out bool open, out uint elementOver)
    {
        var panel = NativeUi.GetElement(NativeUi.InventoryPanel);
        exists = panel != IntPtr.Zero;
        open = exists && NativeUi.IsVisible(panel);
        elementOver = 0;
        if (!_loggedInventory)
        {
            _loggedInventory = true;
            _log(exists ? "Item drag: retail inventory panel found." : "Item drag: retail inventory panel not found; drops outside LegACEy windows withdraw.");
        }
        if (!open || !Available) return false;
        var manager = Marshal.ReadIntPtr(ManagerInstance);
        var over = manager == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(manager, ElementLastOverOffset);
        if (over == IntPtr.Zero) return false;
        elementOver = unchecked((uint)Marshal.ReadInt32(over, ElementIdOffset));
        return over == panel || _isAncestorOfMe!(over, panel) != 0;
    }

    /// <summary>
    /// Shows the client's own drop indicator (the green circle) on the inventory cell under the pointer, as a retail
    /// drag over the inventory does, and clears it from the cell it was on. Call every frame of a LegACEy item drag;
    /// call <see cref="ClearDropIndicator"/> when it ends.
    /// </summary>
    public void UpdateDropIndicator()
    {
        var cell = Available ? InventoryCellUnderPointer() : IntPtr.Zero;
        if (cell == _acceptCell) return;
        ClearDropIndicator();
        if (cell == IntPtr.Zero) return;
        _setDragAccept!(cell, DragAcceptYes);
        _acceptCell = cell;
    }

    public void ClearDropIndicator()
    {
        // The cell was under the pointer on the last frame; inventory cells outlive a drag unless the inventory rebuilds.
        if (_acceptCell != IntPtr.Zero) _setDragAccept!(_acceptCell, DragAcceptNone);
        _acceptCell = IntPtr.Zero;
    }

    /// <summary>The inventory cell (UIElement_UIItem) under the pointer, by the client's hit test, or zero.</summary>
    private IntPtr InventoryCellUnderPointer()
    {
        var manager = Marshal.ReadIntPtr(ManagerInstance);
        var over = manager == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(manager, ElementLastOverOffset);
        if (over == IntPtr.Zero) return IntPtr.Zero;
        var panel = NativeUi.GetElement(NativeUi.InventoryPanel);
        if (panel == IntPtr.Zero || !NativeUi.IsVisible(panel) || (over != panel && _isAncestorOfMe!(over, panel) == 0)) return IntPtr.Zero;
        // The hit element is usually a part of the cell (its icon); walk up to the cell itself.
        var element = over;
        for (var depth = 0; depth < 16 && element != IntPtr.Zero && element != panel;
             depth++, element = NativeUi.Virtual<NativeUi.GetParentFn>(element, NativeUi.GetParentSlot)(element))
        {
            var item = NativeUi.Virtual<DynamicCastFn>(element, DynamicCastSlot)(element, UiItemType);
            if (item != IntPtr.Zero) return item;
        }
        return IntPtr.Zero;
    }

    private static T Function<T>(uint address) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(new IntPtr(address), typeof(T));
}
