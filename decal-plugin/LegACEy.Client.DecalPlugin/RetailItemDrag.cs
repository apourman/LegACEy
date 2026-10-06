using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The retail client's item drag-and-drop, read without hooks. <c>UIElementManager</c> keeps the drag in
/// <c>m_dragElement</c> (+0x31C) and <c>m_bDragStarted</c> (+0x324); <c>UIElement_ItemList::InqDropIconInfo</c> turns
/// the drag icon into the dragged object's id. <c>ClearDragandDrop</c> ends a drag without delivering it to any
/// element, which is how a drop onto a LegACEy window is kept from reaching the world under it.
/// Offsets and functions come from Chorizite's AcClient bindings and our read of the installed acclient.exe.
/// Call on the game thread.
/// </summary>
internal sealed class RetailItemDrag
{
    private const string Source = "Chorizite AcClient bindings (UIElementManager, UIElement_ItemList); our capstone read of installed end-of-retail acclient.exe";
    private const int DragElementOffset = 0x31C;
    private const int DragStartedOffset = 0x324;
    /// <summary>InventoryPanel_Field, the retail inventory window (Chorizite UIElementId).</summary>
    internal const uint InventoryPanel = 0x1000018B;
    private static readonly IntPtr ManagerInstance = new(0x0083E03C);

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // The whole function: it clears m_dragElement (+0x31C), m_bDragStarted (+0x324), m_dragOwner (+0x320) and m_pcPotentialDragElement (+0x318).
        new NativeUiEntry("UIElementManager::ClearDragandDrop", 0x00459400, Bytes(
            "53 56 8B F1 8B 8E 1C 03 00 00 33 DB 3B CB 74 18 8B 81 54 05 00 00 C1 E8 0F A8 01 74 0B E8 2E 6E 00 00 " +
            "89 9E 1C 03 00 00 88 9E 24 03 00 00 89 9E 20 03 00 00 89 9E 18 03 00 00 5E B0 01 5B C3"), Source, "ThisCall () -> bool"),
        new NativeUiEntry("UIElement_ItemList::InqDropIconInfo", 0x004E3380, Bytes("8B 44 24 10 83 EC 3C 53 55 8B 6C 24 50 56 8B 74"), Source,
            "Cdecl (UIElement* dropIcon, uint* itemId, uint* spellId, DropItemFlags* flags)")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte ClearDragFn(IntPtr manager);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InqDropIconInfoFn(IntPtr dropIcon, out uint itemId, out uint spellId, out uint flags);

    private readonly Action<string> _log;
    private readonly ClearDragFn? _clear;
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
        _clear = Function<ClearDragFn>(0x00459400);
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

    /// <summary>Ends the retail drag without dropping the item anywhere.</summary>
    public void Cancel()
    {
        if (!Available) return;
        var manager = Marshal.ReadIntPtr(ManagerInstance);
        if (manager != IntPtr.Zero) _clear!(manager);
    }

    /// <summary>The retail inventory window's screen bounds; null when it is closed. Exists is false when the client has no such element.</summary>
    public Rectangle? InventoryBounds(out bool exists)
    {
        var element = NativeUi.GetElement(InventoryPanel);
        exists = element != IntPtr.Zero;
        if (!_loggedInventory)
        {
            _loggedInventory = true;
            _log(exists ? "Vault drag: retail inventory panel found." : "Vault drag: retail inventory panel not found; drops outside the vault withdraw.");
        }
        if (!exists || !NativeUi.IsVisible(element)) return null;
        return NativeUi.GetBounds(element);
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
