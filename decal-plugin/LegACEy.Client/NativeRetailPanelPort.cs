using System;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>The inventory panel's port over the installed client. The shared parts are in <see cref="NativePanelPort"/>.</summary>
internal sealed class NativeRetailPanelPort : NativePanelPort
{
    // RecvNotice_SetPanelVisibility is called on gmPanelUI+0x5F8, the notice sub-object (pinned at the constructor's store).
    private const int NoticeHandlerOffset = 0x5F8;
    // The inventory panel's id in gmPanelUI's panel table: 7, from the element property 0x10000029 of InventoryPanel_Field
    // (the InventoryButton in layout 0x21000016 carries the same id).
    private const uint InventoryPanelId = 7;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RecvNoticeFn(IntPtr noticeHandler, uint panelId, uint visible);

    private static readonly RecvNoticeFn RecvNotice = (RecvNoticeFn)Marshal.GetDelegateForFunctionPointer(
        new IntPtr(RetailPanelCatalogue.RecvNoticeSetPanelVisibility), typeof(RecvNoticeFn));

    protected override IntPtr Element => NativeUi.GetElement(NativeUi.InventoryPanel);

    // The place, save bit and move are the panel frame's, which draws the frame the inventory sits in: moving only the inventory element
    // left that frame on screen, empty. Places are relative to the parent, so the frame's is its place on screen.
    /// <summary>
    /// The panel frame holding the inventory: its grandparent, above PanelPages (seen in play: inventory at (0,0) in PanelPages at
    /// (5,5) in the frame at (1557,284), 309x795, whose parent is the full-screen root). The frame is not registered by id, and its
    /// vtable is 0x007BC450, not gmPanelUI's. Falls back to the inventory element when the chain is short.
    /// </summary>
    protected override IntPtr Frame()
    {
        var element = Element;
        if (element == IntPtr.Zero) throw new InvalidOperationException("The retail inventory panel is absent.");
        var frame = Parent(Parent(element));
        return frame != IntPtr.Zero ? frame : element;
    }

    /// <summary>
    /// Hides the panel through retail's panel switch, which keeps retail's current-panel pointer in step. When gmPanelUI is
    /// not found from the element, the element is hidden directly.
    /// </summary>
    public override void ClosePanel()
    {
        var element = Element;
        if (element == IntPtr.Zero) return;
        var panel = PanelManagerOf(element);
        if (panel != IntPtr.Zero)
            SetPanelVisibility(panel, InventoryPanelId, false);
        else
            NativeUi.SetVisible(element, false);
    }

    /// <summary>Opens or closes a panel through gmPanelUI's notice, which keeps retail's current-panel pointer in step.</summary>
    internal static void SetPanelVisibility(IntPtr panelManager, uint panelId, bool visible) =>
        RecvNotice(IntPtr.Add(panelManager, NoticeHandlerOffset), panelId, visible ? 1u : 0u);

    /// <summary>
    /// The element gmPanelUI lists for a panel id, or zero when its table does not list it. The table is a pointer at the notice
    /// sub-object's +0x04 (gmPanelUI+0x5FC) with a count at +0x0C; each entry is {element, panelId} in 8 bytes, as retail's loop reads it.
    /// </summary>
    internal static IntPtr PanelElement(IntPtr panelManager, uint panelId)
    {
        var notice = IntPtr.Add(panelManager, NoticeHandlerOffset);
        var count = Marshal.ReadInt32(notice, 0x0C);
        var table = Marshal.ReadIntPtr(notice, 0x04);
        for (var i = 0; i < count && table != IntPtr.Zero; i++)
        {
            var entry = IntPtr.Add(table, 8 * i);
            var element = Marshal.ReadIntPtr(entry);
            if (element != IntPtr.Zero && (uint)Marshal.ReadInt32(entry, 4) == panelId) return element;
        }
        return IntPtr.Zero;
    }

    private static IntPtr Parent(IntPtr element) =>
        element == IntPtr.Zero ? IntPtr.Zero : NativeUi.Virtual<NativeUi.GetParentFn>(element, NativeUi.GetParentSlot)(element);

    /// <summary>The gmPanelUI element at the panel or within three ancestors, or zero when none has its vtable.</summary>
    internal static IntPtr PanelManagerOf(IntPtr element)
    {
        var current = element;
        for (var depth = 0; depth < 4 && current != IntPtr.Zero; depth++)
        {
            if (Marshal.ReadIntPtr(current) == new IntPtr(RetailPanelCatalogue.GmPanelUIVtable)) return current;
            current = NativeUi.Virtual<NativeUi.GetParentFn>(current, NativeUi.GetParentSlot)(current);
        }
        return IntPtr.Zero;
    }
}
