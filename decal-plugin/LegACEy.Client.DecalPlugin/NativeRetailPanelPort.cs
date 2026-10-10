using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The panel takeover's port over the installed client, for the inventory panel. It calls only addresses in
/// <see cref="RetailPanelCatalogue"/> (and the shared entries NativeUiCatalogue already checks), and only on the game thread.
/// </summary>
internal sealed class NativeRetailPanelPort : IRetailPanelPort
{
    // UIElement::SetSaveLocation writes bit 4 of the dword at +0x554; reading that bit is the same field, so it is covered by its pin.
    private const int SaveLocationOffset = 0x554;
    private const int SaveLocationBit = 0x10;
    // RecvNotice_SetPanelVisibility is called on gmPanelUI+0x5F8, the notice sub-object (pinned at the constructor's store).
    private const int NoticeHandlerOffset = 0x5F8;
    // The inventory panel's id in gmPanelUI's panel table: 7, from the element property 0x10000029 of InventoryPanel_Field
    // (the InventoryButton in layout 0x21000016 carries the same id).
    private const uint InventoryPanelId = 7;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RecvNoticeFn(IntPtr noticeHandler, uint panelId, uint visible);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void MoveToFn(IntPtr element, int x, int y);

    private static readonly MoveToFn BaseMoveTo = (MoveToFn)Marshal.GetDelegateForFunctionPointer(
        new IntPtr(NativeUiCatalogue.UIElementMoveTo), typeof(MoveToFn));

    private static readonly RecvNoticeFn RecvNotice = (RecvNoticeFn)Marshal.GetDelegateForFunctionPointer(
        new IntPtr(RetailPanelCatalogue.RecvNoticeSetPanelVisibility), typeof(RecvNoticeFn));

    private static IntPtr Element => NativeUi.GetElement(NativeUi.InventoryPanel);

    public IntPtr Identity => Element;

    public bool IsOpen
    {
        get
        {
            var element = Element;
            return element != IntPtr.Zero && NativeUi.IsVisible(element);
        }
    }

    // The place, save bit and move are the panel frame's, which draws the frame the inventory sits in: moving only the inventory element
    // left that frame on screen, empty. Places are relative to the parent, so the frame's is its place on screen.
    public Point Location => NativeUi.GetBounds(Frame()).Location;

    public bool SaveLocation => (Marshal.ReadInt32(Frame(), SaveLocationOffset) & SaveLocationBit) != 0;

    public void SetSaveLocation(bool save) => NativeUi.SetSaveLocation(Frame(), save);

    // The frame's own MoveTo (0x004D1800) clamps the place inside its parent, so it can never go off-screen, then calls the base move
    // and saves the place to the character's settings. The base move alone parks it and leaves the saved place alone.
    public void MoveTo(Point location) => BaseMoveTo(Frame(), location.X, location.Y);

    public void OpenPanel() => SwitchPanel(true);

    public void ClosePanel() => SwitchPanel(false);

    /// <summary>
    /// Shows or hides the panel through retail's panel switch, which keeps retail's current-panel pointer in step. When gmPanelUI is
    /// not found from the element, the element is shown or hidden directly.
    /// </summary>
    private static void SwitchPanel(bool visible)
    {
        var element = Element;
        if (element == IntPtr.Zero) return;
        var panel = PanelManagerOf(element);
        if (panel != IntPtr.Zero)
            RecvNotice(IntPtr.Add(panel, NoticeHandlerOffset), InventoryPanelId, visible ? 1u : 0u);
        else
            NativeUi.SetVisible(element, visible);
    }

    /// <summary>
    /// The panel frame holding the inventory: its grandparent, above PanelPages (seen in play: inventory at (0,0) in PanelPages at
    /// (5,5) in the frame at (1557,284), 309x795, whose parent is the full-screen root). The frame is not registered by id, and its
    /// vtable is 0x007BC450, not gmPanelUI's. Falls back to the inventory element when the chain is short.
    /// </summary>
    private static IntPtr Frame()
    {
        var element = Element;
        if (element == IntPtr.Zero) throw new InvalidOperationException("The retail inventory panel is absent.");
        var frame = Parent(Parent(element));
        return frame != IntPtr.Zero ? frame : element;
    }

    private static IntPtr Parent(IntPtr element) =>
        element == IntPtr.Zero ? IntPtr.Zero : NativeUi.Virtual<NativeUi.GetParentFn>(element, NativeUi.GetParentSlot)(element);

    /// <summary>The gmPanelUI element at the panel or within three ancestors, or zero when none has its vtable.</summary>
    private static IntPtr PanelManagerOf(IntPtr element)
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
