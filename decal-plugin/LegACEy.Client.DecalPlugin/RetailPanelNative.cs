using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The panel takeover's port over the installed client, for the inventory panel. It calls only addresses in
/// <see cref="RetailPanelCatalogue"/> (and the shared entries NativeUi already checks), and only on the game thread.
/// </summary>
internal sealed class NativeRetailPanelPort : IRetailPanelPort
{
    // UIElement::SetSaveLocation writes bit 4 of the dword at +0x554; reading that bit is the same field, so it is covered by its pin.
    private const int SaveLocationOffset = 0x554;
    private const int SaveLocationBit = 0x10;
    // UIElement::MoveTo is the base move at 0x004634C0; the element's own MoveTo must be it, or the takeover refuses to move the panel.
    private const uint BaseMoveTo = 0x004634C0;
    // gmPanelUI's primary vtable, and the parent walk through vtable +0xA0 (GetParent, as IsAncestorOfMe calls it).
    private const uint GmPanelUIVtable = 0x007B5070;
    private const int GetParentSlot = 0xA0;
    // RecvNotice_SetPanelVisibility is called on gmPanelUI+0x5F8, the notice sub-object (pinned at the constructor's store).
    private const int NoticeHandlerOffset = 0x5F8;
    // The inventory panel's id in gmPanelUI's panel table: 7, from the element property 0x10000029 of InventoryPanel_Field
    // (layout 0x21000017, and the InventoryButton in layout 0x21000016 carries the same id; research R1, layout dump).
    private const uint InventoryPanelId = 7;
    private const uint RecvNoticeSetPanelVisibility = 0x004BD380;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RecvNoticeFn(IntPtr noticeHandler, uint panelId, uint visible);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetParentFn(IntPtr element);

    private static readonly RecvNoticeFn RecvNotice =
        (RecvNoticeFn)Marshal.GetDelegateForFunctionPointer(new IntPtr(RecvNoticeSetPanelVisibility), typeof(RecvNoticeFn));

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

    public Point Location => NativeUi.GetBounds(Present()).Location;

    public bool SaveLocation => (Marshal.ReadInt32(Present(), SaveLocationOffset) & SaveLocationBit) != 0;

    public void SetSaveLocation(bool save) => NativeUi.SetSaveLocation(Present(), save);

    public void MoveTo(Point location)
    {
        var element = Present();
        // The element's own MoveTo must be the base move, or an override would run in its place.
        if (NativeUiMovement.ResolveMoveTo(element) != new IntPtr(BaseMoveTo))
            throw new InvalidOperationException("The retail inventory panel overrides MoveTo; the takeover does not move it.");
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

    private static IntPtr Present()
    {
        var element = Element;
        if (element == IntPtr.Zero) throw new InvalidOperationException("The retail inventory panel is absent.");
        return element;
    }

    /// <summary>The gmPanelUI element at the panel or within three ancestors, or zero when none has its vtable.</summary>
    private static IntPtr PanelManagerOf(IntPtr element)
    {
        var current = element;
        for (var depth = 0; depth < 4 && current != IntPtr.Zero; depth++)
        {
            if (Marshal.ReadIntPtr(current) == new IntPtr(GmPanelUIVtable)) return current;
            current = Virtual<GetParentFn>(current, GetParentSlot)(current);
        }
        return IntPtr.Zero;
    }

    private static T Virtual<T>(IntPtr instance, int slot) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot), typeof(T));
}
