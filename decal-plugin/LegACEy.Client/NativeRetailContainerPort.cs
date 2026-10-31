using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The panel takeover's port for retail's container view (gmExternalContainerUI), which shows an opened chest, corpse or ground container.
/// It is retail's own element, shown and hidden by retail as a container opens and closes; the takeover parks it while it is open and
/// the Dereth window takes its place. Its frame, the floaty environment panel it sits in, carries its place. Retail's close box is a
/// SetVisible(false) of the element (gmExternalContainerUI::ListenToElementMessage), so the window's close box does that too, and
/// retail tells the server it no longer views the container as it does for its own box. The shared parts are in <see cref="NativePanelPort"/>.
/// </summary>
internal sealed class NativeRetailContainerPort : NativePanelPort
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb (near build)";
    /// <summary>gmExternalContainerUI's vtable: slot +0x0C is its ListenToElementMessage, slot +0x98 its GetUIElementType (type 0x1000000D).</summary>
    private const uint Vtable = 0x007B9198;
    private const uint ListenToElementMessage = 0x004CC6C0;

    // The prologues are left out: Decal patches prologues in the running client.
    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("gmExternalContainerUI vtable slot +0x0C holds ListenToElementMessage", Vtable + 0x0C,
            NativeUiCatalogue.Bytes("C0 C6 4C 00"), Source, "data reference (vtable slot)"),
        new NativeUiEntry("gmExternalContainerUI vtable slot +0x98 holds GetUIElementType", Vtable + 0x98,
            NativeUiCatalogue.Bytes("B0 C6 4C 00"), Source, "data reference (vtable slot)"),
        new NativeUiEntry("gmExternalContainerUI::GetUIElementType returns 0x1000000D", 0x004CC6B0,
            NativeUiCatalogue.Bytes("B8 0D 00 00 10 C3"), Source, "ThisCall () -> uint"),
        // A click (message type 1) on the close box (0x10000068) calls vtable slot +0x18 with 0: UIElement::SetVisible(false).
        new NativeUiEntry("gmExternalContainerUI::ListenToElementMessage: close box (0x10000068) hides the element", ListenToElementMessage + 6,
            NativeUiCatalogue.Bytes("83 7F 08 01 8B F1 75 0F 81 3F 68 00 00 10 75 07 8B 06 6A 00 FF 50 18"), Source,
            "instruction reference (cmp [edi+8],1; cmp [edi],0x10000068; push 0; call [eax+0x18])"),
    };

    /// <summary>The container view, or zero when the client has none or the element is not gmExternalContainerUI.</summary>
    protected override IntPtr Element
    {
        get
        {
            var element = NativeUi.GetElement(NativeUi.ExternalContainer);
            return element != IntPtr.Zero && Marshal.ReadIntPtr(element) == new IntPtr(Vtable) ? element : IntPtr.Zero;
        }
    }

    /// <summary>The panel the view sits in, which carries its place: the element's parent, or the element when it has none.</summary>
    protected override IntPtr Frame()
    {
        var element = Element;
        if (element == IntPtr.Zero) throw new InvalidOperationException("The retail container view is absent.");
        var parent = NativeUi.Virtual<NativeUi.GetParentFn>(element, NativeUi.GetParentSlot)(element);
        return parent != IntPtr.Zero ? parent : element;
    }
}
