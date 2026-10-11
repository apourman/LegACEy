using System;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The examination panel's port over the installed client. Its root element is the panel itself (no frame). It is hidden directly, not
/// through gmPanelUI's panel switch: its layout (0x2100006B) carries no panel id to send that switch, so there is no switch route to use.
/// The shared parts are in <see cref="NativePanelPort"/>.
/// </summary>
internal sealed class NativeExaminationPort : NativePanelPort
{
    // An ancestor walk bound: the examination layout is a few levels deep, so a longer chain is not the panel.
    private const int MaxDepth = 32;

    protected override IntPtr Element => NativeUi.GetElement(NativeUi.Examination);

    /// <summary>
    /// Checks, at runtime, that the root element is the examination panel: the inscription field resolves and sits under the root. Read
    /// on the first show, before the panel is parked. Any failed read is a failed check, so retail's panel is then left alone.
    /// </summary>
    public bool VerifyRoot()
    {
        try
        {
            var root = Element;
            var field = NativeUi.GetElement(NativeUi.ExaminationInscription);
            if (root == IntPtr.Zero || field == IntPtr.Zero) return false;
            var current = field;
            for (var depth = 0; depth < MaxDepth && current != IntPtr.Zero; depth++)
            {
                if (current == root) return true;
                current = NativeUi.Virtual<NativeUi.GetParentFn>(current, NativeUi.GetParentSlot)(current);
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
