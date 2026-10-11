using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The panel takeover's port over the installed client, for the examination panel. Its root element is the panel itself, so the place,
/// the save bit and the move are the element's own (no frame, unlike the inventory's). It calls only the shared UIElement entries
/// NativeUiCatalogue checks, and the examination root, which the catalogue lists. Only on the game thread.
/// </summary>
internal sealed class NativeExaminationPort : IRetailPanelPort
{
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void MoveToFn(IntPtr element, int x, int y);

    // The base move alone: it parks the element and leaves its saved place alone, as the inventory's frame move does.
    private static readonly MoveToFn BaseMoveTo = (MoveToFn)Marshal.GetDelegateForFunctionPointer(
        new IntPtr(NativeUiCatalogue.UIElementMoveTo), typeof(MoveToFn));

    private static IntPtr Element => NativeUi.GetElement(NativeUi.Examination);

    private static IntPtr Panel()
    {
        var element = Element;
        if (element == IntPtr.Zero) throw new InvalidOperationException("The retail examination panel is absent.");
        return element;
    }

    public IntPtr Identity => Element;

    public bool IsOpen
    {
        get
        {
            var element = Element;
            return element != IntPtr.Zero && NativeUi.IsVisible(element);
        }
    }

    public Point Location => NativeUi.GetBounds(Panel()).Location;

    public bool SaveLocation => (Marshal.ReadInt32(Panel(), NativeRetailPanelPort.SaveLocationOffset) & NativeRetailPanelPort.SaveLocationBit) != 0;

    public void SetSaveLocation(bool save) => NativeUi.SetSaveLocation(Panel(), save);

    public void MoveTo(Point location) => BaseMoveTo(Panel(), location.X, location.Y);

    /// <summary>Hides the panel. Retail shows it again on its next examination, which is the transition the takeover waits for.</summary>
    public void ClosePanel()
    {
        var element = Element;
        if (element != IntPtr.Zero) NativeUi.SetVisible(element, false);
    }
}
