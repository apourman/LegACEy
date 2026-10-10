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

    // The place, save bit and move are the panel frame's (RootPanel_Field), which draws the frame the inventory sits in: moving only
    // the inventory element left that frame on screen, empty. The gmPanelUI parent walk did not reach it in play, so it is found by id.
    public Point Location => NativeUi.GetBounds(Frame()).Location;

    public bool SaveLocation => (Marshal.ReadInt32(Frame(), SaveLocationOffset) & SaveLocationBit) != 0;

    public void SetSaveLocation(bool save) => NativeUi.SetSaveLocation(Frame(), save);

    public void MoveTo(Point location)
    {
        var element = Frame();
        // The element's own MoveTo must be the base move, or an override would run in its place.
        if (NativeUiMovement.ResolveMoveTo(element) != new IntPtr(NativeUiCatalogue.UIElementMoveTo))
            throw new InvalidOperationException("The retail panel frame overrides MoveTo; the takeover does not move it.");
        NativeUi.MoveTo(element, location);
    }

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

    /// <summary>The panel frame holding the inventory, or the inventory element itself when the frame is not found.</summary>
    private static IntPtr Frame()
    {
        var element = Element;
        if (element == IntPtr.Zero) throw new InvalidOperationException("The retail inventory panel is absent.");
        var frame = NativeUi.GetElement(NativeUi.PanelFrame);
        return frame != IntPtr.Zero ? frame : element;
    }

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
