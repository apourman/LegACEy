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
        new NativeUiEntry("UIElement::IsAncestorOfMe", 0x0045FBB0, Bytes("8B 01 FF 90 A0 00 00 00 85 C0 74 1A 56 8B 74 24 08 3B C6 74 0E 8B 10 8B C8 FF"), Source,
            "ThisCall (UIElement* ancestor) -> bool"),
        new NativeUiEntry("UIElement_ItemList::InqDropIconInfo", 0x004E3380, Bytes("8B 44 24 10 83 EC 3C 53 55 8B 6C 24 50 56 8B 74"), Source,
            "Cdecl (UIElement* dropIcon, uint* itemId, uint* spellId, DropItemFlags* flags)")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void StopDragFn(IntPtr manager);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte IsAncestorOfMeFn(IntPtr element, IntPtr ancestor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InqDropIconInfoFn(IntPtr dropIcon, out uint itemId, out uint spellId, out uint flags);

    private readonly Action<string> _log;
    private readonly StopDragFn? _stop;
    private readonly IsAncestorOfMeFn? _isAncestorOfMe;
    private readonly InqDropIconInfoFn? _inquire;
    private bool _loggedInventory;

    public RetailItemDrag(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        _log = log;
        Available = NativeUiCatalogue.Validate(readMemory, log, Entries);
        if (!Available)
        {
            log("Vault drag and drop disabled: the running client does not match its native drag entries.");
            return;
        }
        _stop = Function<StopDragFn>(0x00459880);
        _isAncestorOfMe = Function<IsAncestorOfMeFn>(0x0045FBB0);
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
            _log(exists ? "Vault drag: retail inventory panel found." : "Vault drag: retail inventory panel not found; drops outside the vault withdraw.");
        }
        if (!open || !Available) return false;
        var manager = Marshal.ReadIntPtr(ManagerInstance);
        var over = manager == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(manager, ElementLastOverOffset);
        if (over == IntPtr.Zero) return false;
        elementOver = unchecked((uint)Marshal.ReadInt32(over, ElementIdOffset));
        return over == panel || _isAncestorOfMe!(over, panel) != 0;
    }

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
