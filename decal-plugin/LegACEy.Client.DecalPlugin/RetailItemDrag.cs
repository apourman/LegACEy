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
    /// <summary>InventoryPanel_Field, the retail inventory window (Chorizite UIElementId).</summary>
    internal const uint InventoryPanel = 0x1000018B;
    private static readonly IntPtr ManagerInstance = new(0x0083E03C);

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // Through its first use of the catcher: reads m_dragElement (+0x31C), m_pElementLastDragCursorOver (+0x250) and m_dragOwner (+0x320).
        new NativeUiEntry("UIElementManager::StopDragandDrop", 0x00459880, Bytes(
            "53 57 8B F9 8B 87 1C 03 00 00 33 DB 3B C3 0F 84 92 00 00 00 56 6A 18 E8 59 58 18 00 83 C4 04 3B C3 74 1D " +
            "C7 40 04 01 00 00 00 C7 00 94 CD 79 00 89 58 08 89 58 0C 89 58 10 88 58 14 8B F0 EB 02 33 F6 8B 87 1C 03 00 00 " +
            "89 46 08 8B 8F 50 02 00 00 89 4E 10 8B 97 20 03 00 00 89 56 0C C6 46 14 01 8B 8F 50 02 00 00 3B CB 74 0E"), Source, "ThisCall ()"),
        new NativeUiEntry("UIElementManager::MouseUpEvent reads m_bDragStarted and m_dragElement", 0x0045DEFB,
            Bytes("8A 86 24 03 00 00 84 C0 74 46 8B 86 1C 03 00 00"), Source, "offset reference (+0x324, +0x31C)"),
        new NativeUiEntry("UIElementManager::MouseUpEvent reads m_pElementLastOver", 0x0045DF23, Bytes("8B 8E 44 02 00 00"), Source, "offset reference (+0x244)"),
        new NativeUiEntry("UIElement_UIItem::SetDragAcceptState", 0x004E1F20, Bytes(
            "8B 89 88 06 00 00 85 C9 74 18 8B 44 24 04 3B 81 00 04 00 00 74 0C 8B 11 89 44 24 04 FF A2 9C 00 00 00 C2"), Source,
            "ThisCall (uint state); 0x1000003F none, 0x10000040 accept (green circle), 0x10000041 reject (showed red in game)"),
        new NativeUiEntry("UIElement_UIItem::DynamicCast", 0x004E1D40, Bytes("8B C1 8B 4C 24 04 81 F9 32 00 00 10 74 0B 33 D2 83 F9 03 0F 95 C2 4A 23 C2 C2 04"), Source,
            "virtual ThisCall (uint type) -> UIElement_UIItem* for type 0x10000032"),
        new NativeUiEntry("DynamicCast to UIItem through vtable +0x94", 0x0048B762, Bytes("68 32 00 00 10 8B C8 FF 92 94 00 00 00"), Source, "virtual slot reference"),
        new NativeUiEntry("UIElement::IsAncestorOfMe", 0x0045FBB0, Bytes("8B 01 FF 90 A0 00 00 00 85 C0 74 1A 56 8B 74 24 08 3B C6 74 0E 8B 10 8B C8 FF"), Source,
            "ThisCall (UIElement* ancestor) -> bool"),
        new NativeUiEntry("UIElement_ItemList::InqDropIconInfo", 0x004E3380, Bytes("8B 44 24 10 83 EC 3C 53 55 8B 6C 24 50 56 8B 74"), Source,
            "Cdecl (UIElement* dropIcon, uint* itemId, uint* spellId, DropItemFlags* flags)"),
        // Starting a drag from a retail item, as UIElement_ItemList::ItemList_BeginDrag does: the element is the source
        // (a UIItem's drag icon, or the press element) and x, y are the grab offset. The client records the drag and
        // hands the pointer to its own drop catchers. Ends with ret 0xC.
        new NativeUiEntry("UIElementManager::StartDragandDrop", 0x0045E120, Bytes(
            "83 EC 34 53 55 56 57 8B 7C 24 48 85 FF 8B F1 0F 84 52 01 00 00"), Source, "ThisCall (UIElement* element, int x, int y) -> bool; ret 0xC"),
        // Prepares the drag icon of a retail item (UIItem +0x69C) from the item, as ItemList_BeginDrag does before it starts the drag.
        new NativeUiEntry("UIElement_ItemList::PrepareDragIcon", 0x004E36E0, Bytes(
            "55 8B EC 83 E4 F8 83 EC 34 53 56 57 8B 7D 08 8B B7 9C 06 00 00 85 F6 89"), Source, "ThisCall (UIElement_UIItem* item) -> bool; ret 4")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void StopDragFn(IntPtr manager);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte IsAncestorOfMeFn(IntPtr element, IntPtr ancestor);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetDragAcceptStateFn(IntPtr item, uint state);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr DynamicCastFn(IntPtr element, uint type);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetParentFn(IntPtr element);

    private const uint UiItemType = 0x10000032;
    private const uint DragAcceptNone = 0x1000003F;
    private const uint DragAcceptYes = 0x10000040;
    private const int DynamicCastSlot = 0x94;
    private const int GetParentSlot = 0xA0;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InqDropIconInfoFn(IntPtr dropIcon, out uint itemId, out uint spellId, out uint flags);

    private readonly Action<string> _log;
    private readonly StopDragFn? _stop;
    private readonly IsAncestorOfMeFn? _isAncestorOfMe;
    private readonly SetDragAcceptStateFn? _setDragAccept;
    private IntPtr _acceptCell;
    private readonly InqDropIconInfoFn? _inquire;
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
    /// Whether the client's own hit test puts the pointer over the retail inventory window (its packs, paperdoll and
    /// container list included). Exists is false when the client has no inventory element; Open, whether it is shown.
    /// </summary>
    public bool IsPointerOverInventory(out bool exists, out bool open, out uint elementOver)
    {
        var panel = NativeUi.GetElement(InventoryPanel);
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
        var panel = NativeUi.GetElement(InventoryPanel);
        if (panel == IntPtr.Zero || !NativeUi.IsVisible(panel) || (over != panel && _isAncestorOfMe!(over, panel) == 0)) return IntPtr.Zero;
        // The hit element is usually a part of the cell (its icon); walk up to the cell itself.
        var element = over;
        for (var depth = 0; depth < 16 && element != IntPtr.Zero && element != panel; depth++, element = Virtual<GetParentFn>(element, GetParentSlot)(element))
        {
            var item = Virtual<DynamicCastFn>(element, DynamicCastSlot)(element, UiItemType);
            if (item != IntPtr.Zero) return item;
        }
        return IntPtr.Zero;
    }

    private static T Virtual<T>(IntPtr instance, int slot) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot), typeof(T));

    private static T Function<T>(uint address) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(new IntPtr(address), typeof(T));

    private static byte[] Bytes(string bytes)
    {
        var parts = bytes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++) result[i] = Convert.ToByte(parts[i], 16);
        return result;
    }
}
