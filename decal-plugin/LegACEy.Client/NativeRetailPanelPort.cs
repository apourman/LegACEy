using System;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The port over the installed client for one of gmPanelUI's panels: the inventory, or the character panel (Attributes, Skills, Titles).
/// The shared parts are in <see cref="NativePanelPort"/>.
/// </summary>
internal sealed class NativeRetailPanelPort : NativePanelPort
{
    // RecvNotice_SetPanelVisibility is called on gmPanelUI+0x5F8, the notice sub-object (pinned at the constructor's store).
    private const int NoticeHandlerOffset = 0x5F8;
    // The inventory panel's id in gmPanelUI's panel table: 7, from the element property 0x10000029 of InventoryPanel_Field
    // (the InventoryButton in layout 0x21000016 carries the same id).
    private const uint InventoryPanelId = 7;
    // The character panel's number in gmPanelUI's panel table (RetailPanel.Skills).
    internal const uint CharacterPanelId = 11;

    private readonly uint _elementId;
    private readonly uint _panelId;

    /// <summary>The inventory's port; the character panel passes its own element and panel number.</summary>
    public NativeRetailPanelPort(uint elementId = NativeUi.InventoryPanel, uint panelId = InventoryPanelId)
    {
        _elementId = elementId;
        _panelId = panelId;
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RecvNoticeFn(IntPtr noticeHandler, uint panelId, uint visible);

    private static readonly RecvNoticeFn RecvNotice = (RecvNoticeFn)Marshal.GetDelegateForFunctionPointer(
        new IntPtr(RetailPanelCatalogue.RecvNoticeSetPanelVisibility), typeof(RecvNoticeFn));

    protected override IntPtr Element => NativeUi.GetElement(_elementId);

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
        if (element == IntPtr.Zero) throw new InvalidOperationException("The retail panel is absent.");
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
            SetPanelVisibility(panel, _panelId, false);
        else
            NativeUi.SetVisible(element, false);
    }

    /// <summary>Opens or closes a panel through gmPanelUI's notice, which keeps retail's current-panel pointer in step.</summary>
    internal static void SetPanelVisibility(IntPtr panelManager, uint panelId, bool visible) =>
        RecvNotice(IntPtr.Add(panelManager, NoticeHandlerOffset), panelId, visible ? 1u : 0u);

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
