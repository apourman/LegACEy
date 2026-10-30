using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The panel takeover's port over the installed client, for one retail panel: what the panels share. A panel's place, save bit and move
/// are those of <see cref="Frame"/> (the panel itself unless a panel is framed); its close is <see cref="ClosePanel"/>. It calls only
/// addresses in <see cref="RetailPanelCatalogue"/> (and the shared entries NativeUiCatalogue already checks), and only on the game thread.
/// </summary>
internal abstract class NativePanelPort : IRetailPanelPort
{
    // UIElement::SetSaveLocation writes bit 4 of the dword at +0x554; reading that bit is the same field, so it is covered by its pin.
    internal const int SaveLocationOffset = 0x554;
    internal const int SaveLocationBit = 0x10;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void MoveToFn(IntPtr element, int x, int y);

    // The base move alone: it parks the element and leaves its saved place alone.
    private static readonly MoveToFn BaseMoveTo = (MoveToFn)Marshal.GetDelegateForFunctionPointer(
        new IntPtr(NativeUiCatalogue.UIElementMoveTo), typeof(MoveToFn));

    /// <summary>The panel's root element, or zero when the client has none (between panels, or not logged in).</summary>
    protected abstract IntPtr Element { get; }

    /// <summary>The element that carries the panel's place and save bit. Defaults to the panel itself.</summary>
    protected virtual IntPtr Frame()
    {
        var element = Element;
        if (element == IntPtr.Zero) throw new InvalidOperationException("The retail panel is absent.");
        return element;
    }

    public IntPtr Identity => Element;

    public virtual RetailPanelSlot? Slot => null;

    public bool IsOpen
    {
        get
        {
            var element = Element;
            return element != IntPtr.Zero && NativeUi.IsVisible(element);
        }
    }

    public Point Location => NativeUi.GetBounds(Frame()).Location;

    public bool SaveLocation => (Marshal.ReadInt32(Frame(), SaveLocationOffset) & SaveLocationBit) != 0;

    public void SetSaveLocation(bool save) => NativeUi.SetSaveLocation(Frame(), save);

    public void MoveTo(Point location) => BaseMoveTo(Frame(), location.X, location.Y);

    /// <summary>Hides the panel. A panel that retail's panel switch manages overrides this to close it through that switch.</summary>
    public virtual void ClosePanel()
    {
        var element = Element;
        if (element != IntPtr.Zero) NativeUi.SetVisible(element, false);
    }
}
