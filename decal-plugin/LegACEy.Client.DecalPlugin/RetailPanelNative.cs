using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The panel takeover's port over the installed client. It calls only addresses in <see cref="RetailPanelCatalogue"/> (and the
/// shared entries NativeUi already checks), and only on the game thread.
/// </summary>
internal sealed class NativeRetailPanelPort : IRetailPanelPort
{
    // UIElement::SetSaveLocation writes bit 4 of the dword at +0x554; reading that bit is the same field, so it is covered by its pin.
    private const int SaveLocationOffset = 0x554;
    private const int SaveLocationBit = 0x10;
    // UIElement::MoveTo is the base move at 0x004634C0; the element's vtable must resolve +0x2C to it, or the takeover does not run.
    private const uint BaseMoveTo = 0x004634C0;
    private const int MoveToVtableSlot = 0x2C;
    // gmPanelUI's class vtable (primary) and the parent walk through vtable +0xA0 (GetParent, as IsAncestorOfMe calls it).
    private const uint GmPanelUIVtable = 0x007B5070;
    private const int GetParentSlot = 0xA0;
    // RecvNotice_SetPanelVisibility is called on gmPanelUI+0x5F8. The child table of {element, panel id} rows is at +0x5FC and its
    // count at +0x604 (research R1, disassembly of the function). These offsets are not byte-pinned: gate G1f checks them live.
    private const int NoticeHandlerOffset = 0x5F8;
    private const int ChildTableOffset = 0x5FC;
    private const int ChildCountOffset = 0x604;
    private const int MaxChildren = 64;
    private const uint RecvNoticeSetPanelVisibility = 0x004BD380;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void MoveToFn(IntPtr element, int x, int y);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RecvNoticeFn(IntPtr noticeHandler, uint panelId, uint visible);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetParentFn(IntPtr element);

    private static readonly MoveToFn BaseMove = (MoveToFn)Marshal.GetDelegateForFunctionPointer(new IntPtr(BaseMoveTo), typeof(MoveToFn));
    private static readonly RecvNoticeFn RecvNotice = (RecvNoticeFn)Marshal.GetDelegateForFunctionPointer(new IntPtr(RecvNoticeSetPanelVisibility), typeof(RecvNoticeFn));

    private readonly uint _rootId;

    public NativeRetailPanelPort(uint rootId) => _rootId = rootId;

    private IntPtr Element => NativeUi.GetElement(_rootId);

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
        // Called directly so the move is the base one; an element that overrides MoveTo is refused rather than moved by its own code.
        if (Marshal.ReadIntPtr(Marshal.ReadIntPtr(element), MoveToVtableSlot) != new IntPtr(BaseMoveTo))
            throw new InvalidOperationException("The retail inventory panel overrides MoveTo; the takeover does not park it.");
        BaseMove(element, location.X, location.Y);
    }

    /// <summary>
    /// Closes the panel through retail's panel switch, RecvNotice_SetPanelVisibility on gmPanelUI, which keeps retail's current-panel
    /// pointer in step. When the gmPanelUI or the panel's id is not found, the element is hidden instead.
    /// </summary>
    public void Close()
    {
        var element = Element;
        if (element == IntPtr.Zero) return;
        var panel = PanelManagerOf(element);
        var panelId = panel == IntPtr.Zero ? -1 : PanelIdOf(panel, element);
        if (panelId >= 0)
            RecvNotice(IntPtr.Add(panel, NoticeHandlerOffset), (uint)panelId, 0);
        else
            NativeUi.SetVisible(element, false);
    }

    private IntPtr Present()
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

    /// <summary>The panel id gmPanelUI files the element under (7 for the inventory), or -1 when the element is not in its table.</summary>
    private static int PanelIdOf(IntPtr panel, IntPtr element)
    {
        var table = Marshal.ReadIntPtr(panel, ChildTableOffset);
        var count = Marshal.ReadInt32(panel, ChildCountOffset);
        if (table == IntPtr.Zero || count <= 0 || count > MaxChildren) return -1;
        for (var index = 0; index < count; index++)
        {
            var row = IntPtr.Add(table, index * 8);
            if (Marshal.ReadIntPtr(row) == element) return Marshal.ReadInt32(row, 4);
        }
        return -1;
    }

    private static T Virtual<T>(IntPtr instance, int slot) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot), typeof(T));
}
